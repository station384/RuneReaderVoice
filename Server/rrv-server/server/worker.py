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
# server/worker.py
#
# Standalone worker process entrypoint.
#
# Each TTS backend runs as an isolated worker subprocess in its own venv.
# This file is executed directly by the backend venv's Python interpreter:
#
#   /path/to/backend/.venv/bin/python -m server.worker \
#       --backend kokoro \
#       --socket /tmp/rrv_worker_kokoro_<pid>.sock \
#       --models-dir /path/to/models \
#       --gpu auto
#
# Communication with the host process is via a Unix domain socket using a
# length-prefixed protocol:
#
#   Request  (host → worker):
#     [4 bytes big-endian uint32: JSON length]
#     [N bytes: UTF-8 JSON body]
#
#   Response (worker → host):
#     [4 bytes big-endian uint32: JSON header length]
#     [N bytes: UTF-8 JSON header]
#     On success: header includes "ogg_len"; followed by:
#       [4 bytes big-endian uint32: OGG length]
#       [N bytes: raw OGG bytes]
#     On error: header has "status": "error", no audio follows.
#
# Supported commands:
#   ping        — liveness check; returns {"status": "ok", "backend": "<id>"}
#   capabilities — returns the backend's capability dict
#   synthesize  — synthesize audio; returns OGG bytes
#
# The worker exits cleanly on SIGTERM or when stdin reaches EOF.

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import signal
import struct
import sys
from pathlib import Path
from typing import Optional

log = logging.getLogger("rrv.worker")


# ── Wire protocol helpers ─────────────────────────────────────────────────────

async def _recv_message(reader: asyncio.StreamReader) -> Optional[dict]:
    """Read a length-prefixed JSON message. Returns None on clean EOF."""
    try:
        length_bytes = await reader.readexactly(4)
    except asyncio.IncompleteReadError:
        return None  # EOF — host shut down
    length = struct.unpack(">I", length_bytes)[0]
    body = await reader.readexactly(length)
    return json.loads(body.decode("utf-8"))


async def _send_message(writer: asyncio.StreamWriter, obj: dict) -> None:
    """Send a length-prefixed JSON message."""
    body = json.dumps(obj).encode("utf-8")
    writer.write(struct.pack(">I", len(body)))
    writer.write(body)
    await writer.drain()


async def _send_audio_response(
    writer: asyncio.StreamWriter,
    header: dict,
    ogg_bytes: bytes,
) -> None:
    """Send JSON header followed by length-prefixed OGG bytes."""
    header["ogg_len"] = len(ogg_bytes)
    await _send_message(writer, header)
    writer.write(struct.pack(">I", len(ogg_bytes)))
    writer.write(ogg_bytes)
    await writer.drain()


# ── Request dispatch ──────────────────────────────────────────────────────────

async def _handle_connection(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    backend,
    gpu_provider: str,
) -> None:
    """
    Handle one client connection (the host WorkerBackend).
    In practice this is a single persistent connection for the lifetime of
    the worker process. Loops until EOF.
    """
    from .backends.base import SynthesisRequest

    peer = writer.get_extra_info("peername") or "<unix>"
    log.debug("Connection from %s", peer)

    try:
        while True:
            msg = await _recv_message(reader)
            if msg is None:
                log.info("Host closed connection — worker exiting")
                break

            cmd = msg.get("cmd")

            if cmd == "ping":
                await _send_message(writer, {
                    "status": "ok",
                    "backend": backend.provider_id,
                })

            elif cmd == "capabilities":
                caps = backend.capability_dict(execution_provider=gpu_provider)
                await _send_message(writer, {"status": "ok", "capabilities": caps})

            elif cmd == "voices":
                voices = [
                    {
                        "voice_id":     v.voice_id,
                        "display_name": v.display_name,
                        "language":     v.language,
                        "gender":       v.gender,
                        "type":         v.type,
                    }
                    for v in backend.get_voices()
                ]
                await _send_message(writer, {"status": "ok", "voices": voices})

            elif cmd == "synthesize":
                try:
                    request = _build_request(msg)
                    result = await backend.synthesize(request)
                    await _send_audio_response(
                        writer,
                        {
                            "status":       "ok",
                            "duration_sec": result.duration_sec,
                        },
                        result.ogg_bytes,
                    )
                except (ValueError, RuntimeError) as exc:
                    log.warning("Synthesis error: %s", exc)
                    await _send_message(writer, {"status": "error", "message": str(exc)})
                except Exception as exc:
                    log.exception("Unexpected synthesis error")
                    await _send_message(writer, {"status": "error", "message": repr(exc)})

            else:
                await _send_message(writer, {
                    "status":  "error",
                    "message": f"Unknown command: {cmd!r}",
                })

    except Exception:
        log.exception("Connection handler error")
    finally:
        writer.close()
        try:
            await writer.wait_closed()
        except Exception:
            pass


def _build_request(msg: dict):
    """Deserialize a synthesize message into a SynthesisRequest."""
    from .backends.base import SynthesisRequest

    sample_path_str = msg.get("sample_path")
    samples_dir_str = msg.get("samples_dir")

    return SynthesisRequest(
        text=msg["text"],
        lang_code=msg.get("lang_code", "en"),
        speech_rate=float(msg.get("speech_rate", 1.0)),
        voice_id=msg.get("voice_id"),
        sample_path=Path(sample_path_str) if sample_path_str else None,
        sample_id=msg.get("sample_id"),
        samples_dir=Path(samples_dir_str) if samples_dir_str else None,
        ref_text=msg.get("ref_text", ""),
        blend=msg.get("blend") or [],
        cfg_weight=_opt_float(msg, "cfg_weight"),
        exaggeration=_opt_float(msg, "exaggeration"),
        cfg_strength=_opt_float(msg, "cfg_strength"),
        nfe_step=_opt_int(msg, "nfe_step"),
        cross_fade_duration=_opt_float(msg, "cross_fade_duration"),
        sway_sampling_coef=_opt_float(msg, "sway_sampling_coef"),
        # progress_callback is never set cross-process — host handles progress
        progress_callback=None,
    )


def _opt_float(d: dict, key: str):
    v = d.get(key)
    return float(v) if v is not None else None


def _opt_int(d: dict, key: str):
    v = d.get(key)
    return int(v) if v is not None else None


# ── Main ──────────────────────────────────────────────────────────────────────

async def _main(args: argparse.Namespace) -> None:
    # Configure logging — write to stdout so WorkerBackend can forward it
    logging.basicConfig(
        level=args.log_level.upper(),
        format=f"[worker:{args.backend}] %(levelname)-8s %(name)s — %(message)s",
        stream=sys.stdout,
    )

    # GPU detection — reuse the host module if available in this venv,
    # otherwise fall back to a simple cpu-only stub.
    try:
        from .gpu_detect import detect as detect_gpu
        gpu = detect_gpu(args.gpu)
        gpu_provider = gpu.provider
    except ImportError:
        log.warning("gpu_detect not available in worker venv — defaulting to cpu")
        gpu_provider = "cpu"

    # Instantiate and load the backend
    models_dir = Path(args.models_dir)
    samples_dir = Path(args.samples_dir)

    # We need a minimal GpuInfo-like object for backends that use it
    class _GpuInfo:
        def __init__(self, provider: str):
            self.provider = provider
            # ORT execution providers: prefer CUDA/ROCm over CPU
            if provider == "cuda":
                self.ort_providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
            elif provider == "rocm":
                self.ort_providers = ["ROCMExecutionProvider", "CPUExecutionProvider"]
            else:
                self.ort_providers = ["CPUExecutionProvider"]
            # torch device string
            self.torch_device = "cuda" if provider == "cuda" else (
                "rocm" if provider == "rocm" else "cpu"
            )

    try:
        gpu_info = _GpuInfo(gpu_provider)
    except Exception:
        gpu_info = _GpuInfo("cpu")

    backend = _instantiate_backend(args.backend, models_dir, samples_dir, gpu_info, args)
    log.info("Loading backend '%s'...", args.backend)
    await backend.load()
    log.info("Backend '%s' loaded (model_version=%s)", args.backend, backend.model_version)

    # Set up SIGTERM handler for clean shutdown
    loop = asyncio.get_running_loop()
    stop_event = asyncio.Event()

    def _on_sigterm():
        log.info("SIGTERM received — shutting down")
        stop_event.set()

    loop.add_signal_handler(signal.SIGTERM, _on_sigterm)

    # Start Unix domain socket server
    socket_path = args.socket
    # Remove stale socket file if present
    try:
        os.unlink(socket_path)
    except FileNotFoundError:
        pass

    server = await asyncio.start_unix_server(
        lambda r, w: _handle_connection(r, w, backend, gpu_provider),
        path=socket_path,
    )

    log.info("Worker '%s' listening on %s", args.backend, socket_path)

    # Signal readiness by printing a line the host watches for
    print(f"READY:{args.backend}:{socket_path}", flush=True)

    async with server:
        await stop_event.wait()

    log.info("Worker '%s' shut down cleanly", args.backend)

    # Clean up socket file
    try:
        os.unlink(socket_path)
    except FileNotFoundError:
        pass


def _instantiate_backend(name: str, models_dir: Path, samples_dir: Path, gpu_info, args: argparse.Namespace):
    """Instantiate the correct backend class for this worker."""
    max_concurrent = getattr(args, "max_concurrent", 2) or 2

    if name == "kokoro":
        from .backends.kokoro_backend import KokoroBackend
        return KokoroBackend(models_dir=models_dir, ort_providers=gpu_info.ort_providers)

    elif name == "f5tts":
        from .backends.f5tts_backend import F5TtsBackend
        return F5TtsBackend(models_dir=models_dir, torch_device=gpu_info.torch_device)

    elif name == "chatterbox":
        from .backends.chatterbox_backend import ChatterboxBackend
        return ChatterboxBackend(
            models_dir=models_dir,
            torch_device=gpu_info.torch_device,
            max_concurrent=max_concurrent,
        )

    elif name == "chatterbox_full":
        from .backends.chatterbox_full_backend import ChatterboxFullBackend
        return ChatterboxFullBackend(
            models_dir=models_dir,
            torch_device=gpu_info.torch_device,
            max_concurrent=max_concurrent,
        )

    elif name == "chatterbox_multilingual":
        from .backends.chatterbox_multilingual_backend import ChatterboxMultilingualBackend
        return ChatterboxMultilingualBackend(
            models_dir=models_dir,
            torch_device=gpu_info.torch_device,
            max_concurrent=1,  # always serial
        )

    elif name == "qwen":
        from .backends.qwen_backend import QwenBackend
        return QwenBackend(models_dir=models_dir, torch_device=gpu_info.torch_device)

    else:
        raise ValueError(f"Unknown backend: {name!r}")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="rrv-worker",
        description="RuneReader Voice worker subprocess — runs one TTS backend in isolation",
    )
    parser.add_argument("--backend",  required=True, help="Backend name (e.g. kokoro, chatterbox)")
    parser.add_argument("--socket",   required=True, help="Unix socket path to bind")
    parser.add_argument("--models-dir", dest="models_dir", required=True, help="Model files directory")
    parser.add_argument("--samples-dir", dest="samples_dir", required=True, help="Reference audio samples directory")
    parser.add_argument("--gpu",      default="auto",
                        choices=["auto", "cuda", "rocm", "cpu"],
                        help="GPU execution provider (default: auto)")
    parser.add_argument("--max-concurrent", dest="max_concurrent", type=int, default=2,
                        help="Max concurrent synthesis (Chatterbox backends only; default: 2)")
    parser.add_argument("--log-level", dest="log_level", default="info",
                        choices=["debug", "info", "warning", "error"])
    return parser.parse_args()


if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
