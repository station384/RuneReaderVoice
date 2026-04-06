# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# RuneReader Voice Server is free software: you can redistribute it and/or
# modify it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# RuneReader Voice Server is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with RuneReader Voice Server. If not, see <https://www.gnu.org/licenses/>.
# server/main.py
#
# FastAPI application entry point.
#
# Startup sequence:
#   1. Parse CLI args and apply overrides to settings
#   2. Configure logging
#   3. Ensure directories exist
#   4. Detect GPU execution provider
#   5. Initialize audio cache (DB + startup integrity check)
#   6. Load TTS backends
#   7. Start transcription service (if voice-matching backends loaded)
#   8. Start serving

from __future__ import annotations

import os as _os
import pathlib as _pathlib
# Redirect HuggingFace hub downloads (wetext FSTs, model internals)
# into the managed data directory instead of ~/.cache/huggingface/.
# Must be set before any huggingface_hub import.
_hf_cache = _pathlib.Path(__file__).parent.parent.parent / "data" / "models" / "hf-cache"
_hf_cache.mkdir(parents=True, exist_ok=True)
_os.environ.setdefault("HF_HUB_CACHE", str(_hf_cache))

import argparse
import asyncio
import logging
import sys
from contextlib import asynccontextmanager

import uvicorn
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse

from .config import settings
from .gpu_detect import detect as detect_gpu
from .cache import AudioCache
from .backends import load_backends
from .transcriber import TranscriptionService
from .asr import load_asr_provider, AsrRegistry
from .community_db import CommunityDb

log = logging.getLogger(__name__)


# ── Lifespan ──────────────────────────────────────────────────────────────────

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Startup and shutdown logic."""

    # Ensure all required directories exist
    settings.ensure_directories()

    # GPU detection
    gpu = detect_gpu(settings.gpu)
    app.state.gpu      = gpu
    app.state.settings = settings

    # Community DB (NPC overrides crowd-source store)
    community_db = CommunityDb(settings.community_db_path)
    await community_db.initialize()
    app.state.community_db = community_db

    # Audio cache
    cache = AudioCache(
        cache_dir=settings.cache_dir,
        db_path=settings.db_path,
        max_bytes=settings.cache_max_bytes,
    )
    await cache.initialize()
    app.state.cache = cache

    # TTS backends
    registry = await load_backends(
        backend_names=settings.backends,
        models_dir=settings.models_dir,
        gpu=gpu,
        settings=settings,
    )
    app.state.registry = registry

    # Transcription service — only started when a voice-matching backend is loaded.
    # Handles both ffmpeg audio/video conversion AND Whisper transcription.
    # ffmpeg conversion runs regardless of Whisper availability — a file can be
    # converted to WAV even if auto-transcription is disabled.
    poll_task = None
    has_voice_matching = any(b.supports_voice_matching for b in registry.all())

    asr_registry = None
    if has_voice_matching:
        transcriber = TranscriptionService(
            whisper_model_dir=settings.whisper_model_dir,
            samples_dir=settings.samples_dir,
        )
        transcriber.cleanup_tmp_sidecars()  # remove leftover .tmp sidecars from interrupted writes
        transcriber.check_availability()   # checks both ffmpeg and Whisper, logs results

        # Load ASR provider — may be whisper (in-process) or a worker subprocess
        asr_registry = await load_asr_provider(
            provider_name=settings.asr_provider,
            models_dir=settings.models_dir,
            whisper_model_dir=settings.whisper_model_dir,
            gpu=gpu,
            settings=settings,
        )
        app.state.asr_registry = asr_registry
        transcriber.set_asr_registry(asr_registry)

        # Initial scan at startup — convert + transcribe any pending files
        await transcriber.scan_and_transcribe()

        # Background polling loop — always start if voice-matching backends loaded
        poll_task = asyncio.create_task(
            _sample_poll_loop(transcriber, settings.sample_scan_interval)
        )
        log.info(
            "Sample watcher started — polling every %ds",
            settings.sample_scan_interval,
        )
    else:
        log.debug("No voice-matching backends loaded — transcription service not started")

    log.info(
        "RuneReader Voice Server ready — %s:%d | backends: %s | gpu: %s",
        settings.host, settings.port,
        ", ".join(registry.provider_ids()) or "none",
        gpu.provider,
    )

    yield

    # Shutdown
    if poll_task is not None:
        poll_task.cancel()
        try:
            await poll_task
        except asyncio.CancelledError:
            pass

    # Shut down ASR provider subprocess (if any)
    if asr_registry is not None:
        asr_registry.shutdown()

    # Shut down any worker subprocess backends before closing cache/db
    from .backends.worker_backend import WorkerBackend as _WorkerBackend
    for _backend in registry.all():
        if isinstance(_backend, _WorkerBackend):
            try:
                _backend.shutdown()
            except Exception as _exc:
                log.warning("Error shutting down worker '%s': %s", _backend.provider_id, _exc)

    await cache.close()
    await community_db.close()
    log.info("Server shut down cleanly")


# ── Background polling loop ───────────────────────────────────────────────────

async def _sample_poll_loop(transcriber: TranscriptionService, interval: int) -> None:
    """
    Polls the samples directory every `interval` seconds.
    Transcribes any audio files that have appeared since the last scan.
    Runs as a background asyncio task for the lifetime of the server.
    """
    while True:
        try:
            await asyncio.sleep(interval)
            count = await transcriber.scan_and_transcribe()
            if count:
                log.info("Sample watcher: transcribed %d new file(s)", count)
        except asyncio.CancelledError:
            break
        except Exception as e:
            log.error("Sample watcher error: %s", e)


# ── App ───────────────────────────────────────────────────────────────────────

app = FastAPI(
    title="RuneReader Voice Server",
    version="0.1.0",
    description="Shared L2 TTS render cache for RuneReader Voice clients.",
    lifespan=lifespan,
    docs_url="/docs",       # Swagger UI — useful for testing without the client
    redoc_url="/redoc",
)

# ── Reverse proxy support ─────────────────────────────────────────────────────
# When running behind Caddy (or any reverse proxy), trust X-Forwarded-For
# so logs and auth middleware see the real client IP instead of the proxy IP.
# RRV_TRUSTED_PROXY_IPS: comma-separated list of trusted proxy IPs/CIDRs.
# Defaults to "127.0.0.1" (loopback) which covers Caddy on the same host.
# Set to "0.0.0.0/0" to trust all proxies (only safe on a private LAN).

from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

_trusted_proxies = settings.trusted_proxy_ips
app.add_middleware(ProxyHeadersMiddleware, trusted_hosts=_trusted_proxies)


# ── Auth middleware ───────────────────────────────────────────────────────────

@app.middleware("http")
async def auth_middleware(request: Request, call_next):
    """
    Optional API key authentication.
    Enabled only when RRV_API_KEY is non-empty.
    Passes all requests through when auth is disabled (default LAN mode).
    /api/v1/health is always allowed regardless of auth state.
    """
    if not settings.auth_enabled:
        return await call_next(request)

    if request.url.path == "/api/v1/health":
        return await call_next(request)

    auth_header = request.headers.get("Authorization", "")
    if not auth_header.startswith("Bearer "):
        return JSONResponse(
            status_code=401,
            content={"detail": "Authorization header required: Bearer <api_key>"},
        )

    token = auth_header[len("Bearer "):]
    if token != settings.api_key:
        return JSONResponse(
            status_code=401,
            content={"detail": "Invalid API key"},
        )

    return await call_next(request)


# ── Routes ────────────────────────────────────────────────────────────────────

from .routes.health        import router as health_router
from .routes.capabilities  import router as capabilities_router
from .routes.providers     import router as providers_router
from .routes.synthesize    import router as synthesize_router
from .routes.synthesize_v2 import router as synthesize_v2_router
from .routes.npc_overrides import router as npc_overrides_router
from .routes.defaults      import router as defaults_router

app.include_router(health_router)
app.include_router(capabilities_router)
app.include_router(providers_router)
app.include_router(synthesize_router)
app.include_router(synthesize_v2_router)
app.include_router(npc_overrides_router)
app.include_router(defaults_router)


# ── CLI entry point ───────────────────────────────────────────────────────────

def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="rrv-server",
        description="RuneReader Voice TTS Server",
    )
    parser.add_argument("--host",        help="Bind address (default: 0.0.0.0)")
    parser.add_argument("--port",        type=int, help="Port (default: 8765)")
    parser.add_argument("--cache-dir",   dest="cache_dir",   help="OGG cache directory")
    parser.add_argument("--db-path",     dest="db_path",     help="SQLite manifest path")
    parser.add_argument("--models-dir",  dest="models_dir",  help="Model files directory")
    parser.add_argument("--samples-dir",       dest="samples_dir",       help="Reference samples directory")
    parser.add_argument("--whisper-model-dir", dest="whisper_model_dir", help="Whisper model directory for auto-transcription")
    parser.add_argument("--sample-scan-interval", dest="sample_scan_interval", type=int,
                        help="Seconds between sample directory scans (default: 30)")
    parser.add_argument("--backends",    help="Comma-separated backend list: kokoro,f5tts,chatterbox,chatterbox_full")
    parser.add_argument("--gpu",         choices=["auto", "cuda", "rocm", "cpu"],
                        help="GPU execution provider (default: auto)")
    parser.add_argument("--cache-max-mb", dest="cache_max_mb", type=int,
                        help="Max cache size in MB (default: 2048)")
    parser.add_argument("--api-key",     dest="api_key",     help="API key (disables auth if empty)")
    parser.add_argument("--log-level",   dest="log_level",
                        choices=["debug", "info", "warning", "error"],
                        help="Log level (default: info)")
    return parser.parse_args()


def run() -> None:
    """Entry point for the rrv-server console script."""
    args = _parse_args()

    # Apply CLI overrides to the module-level settings singleton
    settings.override(
        host=args.host,
        port=args.port,
        cache_dir=args.cache_dir,
        db_path=args.db_path,
        models_dir=args.models_dir,
        samples_dir=args.samples_dir,
        whisper_model_dir=args.whisper_model_dir,
        sample_scan_interval=args.sample_scan_interval,
        backends=args.backends,
        gpu=args.gpu,
        cache_max_mb=args.cache_max_mb,
        api_key=args.api_key,
        log_level=args.log_level,
    )

    # Configure logging
    logging.basicConfig(
        level=settings.log_level.upper(),
        format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    log.info("Starting RuneReader Voice Server — %s", settings)

    uvicorn.run(
        "server.main:app",
        host=settings.host,
        port=settings.port,
        log_level=settings.log_level,
        reload=False,
    )


if __name__ == "__main__":
    run()
    