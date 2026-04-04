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
# server/backends/worker_backend.py
#
# WorkerBackend — implements AbstractTtsBackend as a transparent proxy to a
# worker subprocess running in its own isolated Python venv.
#
# The host process stays thin (no ML imports except Whisper). Each TTS backend
# runs in a worker subprocess launched with the backend-specific venv's Python
# interpreter. Communication is over a Unix domain socket using the same
# length-prefixed JSON + raw bytes protocol defined in server/worker.py.
#
# Lifecycle:
#   load()     — spawns the worker, waits for READY signal + ping/pong handshake,
#                fetches capabilities from the worker, caches them.
#   synthesize()  — forwards the SynthesisRequest over the socket, reads OGG bytes.
#   shutdown()    — sends SIGTERM to the worker process, waits for it to exit.
#
# The socket connection is persistent for the lifetime of the worker. If the
# worker dies unexpectedly (crash, OOM), synthesize() raises RuntimeError with
# a clear message. The host can log the error — no automatic restart is
# attempted (a crashed worker almost certainly means a model/memory problem
# that will recur immediately).
#
# Thread / async safety:
#   synthesize() is protected by an asyncio.Lock so concurrent callers serialize
#   at the socket level. Backends that support concurrency can be run as multiple
#   workers instead (one worker per slot). For now a single worker per backend
#   is the expected topology.

from __future__ import annotations

import asyncio
import json
import logging
import os
import signal
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo

log = logging.getLogger(__name__)

# How long to wait for the worker to print its READY line (seconds)
_WORKER_START_TIMEOUT = 60.0
# How long to wait for a ping response (seconds)
_PING_TIMEOUT = 10.0
# How long to wait for synthesis to complete (seconds)
# Long timeout: Chatterbox full model on CPU can take 60+ seconds
_SYNTHESIS_TIMEOUT = 300.0


# ── Wire protocol helpers (duplicated from worker.py to keep host-side clean) ─

async def _send_message(writer: asyncio.StreamWriter, obj: dict) -> None:
    body = json.dumps(obj).encode("utf-8")
    writer.write(struct.pack(">I", len(body)))
    writer.write(body)
    await writer.drain()


async def _recv_message(reader: asyncio.StreamReader) -> Optional[dict]:
    try:
        length_bytes = await reader.readexactly(4)
    except (asyncio.IncompleteReadError, ConnectionResetError):
        return None
    length = struct.unpack(">I", length_bytes)[0]
    body = await reader.readexactly(length)
    return json.loads(body.decode("utf-8"))


async def _recv_audio(reader: asyncio.StreamReader) -> bytes:
    """Read length-prefixed raw bytes (OGG audio)."""
    length_bytes = await reader.readexactly(4)
    length = struct.unpack(">I", length_bytes)[0]
    return await reader.readexactly(length)


# ── WorkerBackend ─────────────────────────────────────────────────────────────

class WorkerBackend(AbstractTtsBackend):
    """
    Proxy backend that delegates synthesis to a worker subprocess.

    Instantiate instead of a direct backend class when an
    RRV_WORKER_VENV_<name> path is configured in settings.

    The worker subprocess runs run_worker.py via the backend venv Python.
    No ML libraries are imported in the host process.
    """

    def __init__(
        self,
        backend_name: str,
        venv_path: Path,
        models_dir: Path,
        samples_dir: Path,
        gpu: str = "auto",
        max_concurrent: int = 2,
        log_level: str = "info",
        qwen_size: str = "large",
    ) -> None:
        self._backend_name = backend_name
        self._venv_path = Path(venv_path)
        self._models_dir = models_dir
        self._samples_dir = samples_dir
        self._gpu = gpu
        self._max_concurrent = max_concurrent
        self._log_level = log_level
        self._qwen_size = qwen_size

        # Set after load()
        self._process: Optional[subprocess.Popen] = None
        self._socket_path: Optional[str] = None
        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        self._lock = asyncio.Lock()

        # Cached from worker after handshake
        self._capabilities: dict = {}
        self._voices: list[VoiceInfo] = []
        self._model_version_str: str = ""

    # ── AbstractTtsBackend properties ─────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return self._backend_name

    @property
    def display_name(self) -> str:
        return self._capabilities.get("display_name", self._backend_name)

    @property
    def supports_base_voices(self) -> bool:
        return self._capabilities.get("supports_base_voices", False)

    @property
    def supports_voice_matching(self) -> bool:
        return self._capabilities.get("supports_voice_matching", False)

    @property
    def supports_voice_blending(self) -> bool:
        return self._capabilities.get("supports_voice_blending", False)

    @property
    def supports_inline_pronunciation(self) -> bool:
        return self._capabilities.get("supports_inline_pronunciation", False)

    @property
    def supports_voice_instruct(self) -> bool:
        return self._capabilities.get("supports_voice_instruct", False)

    @property
    def supports_voice_design(self) -> bool:
        return self._capabilities.get("supports_voice_design", False)

    @property
    def languages(self) -> list[str]:
        return self._capabilities.get("languages", ["en"])

    @property
    def model_version(self) -> str:
        return self._model_version_str

    def extra_controls(self) -> dict:
        return self._capabilities.get("controls", {})

    def capability_dict(self, execution_provider: str) -> dict:
        # Return the cached capabilities from the worker, overlaid with the
        # execution_provider resolved by the host (it may differ from what
        # the worker self-reported if host overrides it).
        caps = dict(self._capabilities)
        caps["execution_provider"] = execution_provider
        caps["loaded"] = True
        return caps

    # ── Load / startup ─────────────────────────────────────────────────────────

    async def load(self) -> None:
        """
        Spawn the worker subprocess and wait for it to be ready.
        Raises RuntimeError if the worker fails to start or the handshake fails.
        """
        python = self._venv_python()
        if not python.exists():
            raise RuntimeError(
                f"Worker venv Python not found: {python}\n"
                f"  Backend: {self._backend_name}\n"
                f"  Venv:    {self._venv_path}"
            )

        # Launcher script lives at <worker_dir>/run_worker.py.
        # The venv is always at <worker_dir>/.venv, so worker_dir = venv_path.parent.
        launcher = self._venv_path.parent / "run_worker.py"
        if not launcher.exists():
            raise RuntimeError(
                f"Worker launcher not found: {launcher}\n"
                f"  Each worker directory must contain a run_worker.py thin launcher."
            )

        # Unique socket path per worker instance
        self._socket_path = os.path.join(
            tempfile.gettempdir(),
            f"rrv_worker_{self._backend_name}_{os.getpid()}.sock",
        )

        cmd = [
            str(python), str(launcher),
            "--backend",     self._backend_name,
            "--socket",      self._socket_path,
            "--models-dir",  str(self._models_dir),
            "--samples-dir", str(self._samples_dir),
            "--gpu",         self._gpu,
            "--max-concurrent", str(self._max_concurrent),
            "--qwen-size",   self._qwen_size,
            "--log-level",   self._log_level,
        ]

        log.info(
            "Spawning worker '%s' — launcher: %s",
            self._backend_name, launcher,
        )

        # Spawn the worker.
        # stdout is piped so we can read the READY line.
        # stderr is inherited so worker log output appears in the host log stream.
        self._process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=None,
        )


        # Wait for READY line on stdout (in a thread to stay async-friendly)
        try:
            await asyncio.wait_for(
                asyncio.get_event_loop().run_in_executor(None, self._wait_for_ready),
                timeout=_WORKER_START_TIMEOUT,
            )
        except asyncio.TimeoutError:
            self._kill_worker()
            raise RuntimeError(
                f"Worker '{self._backend_name}' did not become ready within "
                f"{_WORKER_START_TIMEOUT:.0f}s — check model files and venv dependencies"
            )
        except Exception as exc:
            self._kill_worker()
            raise RuntimeError(f"Worker '{self._backend_name}' startup failed: {exc}") from exc

        # Connect to the worker's Unix socket
        try:
            self._reader, self._writer = await asyncio.open_unix_connection(self._socket_path)
        except Exception as exc:
            self._kill_worker()
            raise RuntimeError(
                f"Cannot connect to worker socket '{self._socket_path}': {exc}"
            ) from exc

        # Ping / pong handshake
        try:
            await asyncio.wait_for(self._ping(), timeout=_PING_TIMEOUT)
        except asyncio.TimeoutError:
            self._kill_worker()
            raise RuntimeError(f"Worker '{self._backend_name}' ping timed out")

        # Fetch capabilities
        await _send_message(self._writer, {"cmd": "capabilities"})
        resp = await asyncio.wait_for(_recv_message(self._reader), timeout=_PING_TIMEOUT)
        if resp is None or resp.get("status") != "ok":
            self._kill_worker()
            raise RuntimeError(
                f"Worker '{self._backend_name}' capabilities fetch failed: {resp}"
            )
        self._capabilities = resp.get("capabilities", {})
        self._model_version_str = self._capabilities.get("model_version", "")

        # Fetch voice list (for base-voice backends like Kokoro)
        await _send_message(self._writer, {"cmd": "voices"})
        resp = await asyncio.wait_for(_recv_message(self._reader), timeout=_PING_TIMEOUT)
        if resp and resp.get("status") == "ok":
            self._voices = [
                VoiceInfo(
                    voice_id=v["voice_id"],
                    display_name=v["display_name"],
                    language=v["language"],
                    gender=v["gender"],
                    type=v["type"],
                )
                for v in resp.get("voices", [])
            ]

        log.info(
            "Worker '%s' ready — model_version=%s capabilities=%s",
            self._backend_name,
            self._model_version_str,
            {k: v for k, v in self._capabilities.items() if k not in ("controls",)},
        )

    def _venv_python(self) -> Path:
        """Return the Python interpreter path inside the configured venv."""
        # Linux/macOS layout
        candidate = self._venv_path / "bin" / "python"
        if candidate.exists():
            return candidate
        # Windows layout (Scripts/ instead of bin/)
        candidate = self._venv_path / "Scripts" / "python.exe"
        return candidate

    def _wait_for_ready(self) -> None:
        """
        Blocking read of the worker's stdout until we see the READY line.
        Forwards other stdout lines to the host logger.
        Must be called in a thread (not the event loop).
        """
        assert self._process is not None
        assert self._process.stdout is not None

        for raw_line in self._process.stdout:
            line = raw_line.decode("utf-8", errors="replace").rstrip()
            if line.startswith("READY:"):
                log.debug("Worker stdout: %s", line)
                return
            # Forward non-READY lines (worker log output before the socket is up)
            log.debug("[worker:%s] %s", self._backend_name, line)

        # stdout closed before READY — worker exited prematurely
        rc = self._process.wait()
        raise RuntimeError(
            f"Worker '{self._backend_name}' exited with code {rc} before becoming ready"
        )

    async def _ping(self) -> None:
        await _send_message(self._writer, {"cmd": "ping"})
        resp = await _recv_message(self._reader)
        if resp is None or resp.get("status") != "ok":
            raise RuntimeError(f"Ping failed: {resp!r}")
        log.debug("Worker '%s' ping OK", self._backend_name)

    # ── Synthesis ──────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return list(self._voices)

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._writer is None or self._reader is None:
            raise RuntimeError(f"Worker '{self._backend_name}' is not loaded")
        if self._process is not None and self._process.poll() is not None:
            raise RuntimeError(
                f"Worker '{self._backend_name}' has exited unexpectedly "
                f"(exit code {self._process.returncode})"
            )

        msg = _request_to_dict(request)

        async with self._lock:
            try:
                await _send_message(self._writer, msg)
                header = await asyncio.wait_for(
                    _recv_message(self._reader),
                    timeout=_SYNTHESIS_TIMEOUT,
                )
            except asyncio.TimeoutError:
                raise RuntimeError(
                    f"Worker '{self._backend_name}' synthesis timed out "
                    f"after {_SYNTHESIS_TIMEOUT:.0f}s"
                )

            if header is None:
                raise RuntimeError(
                    f"Worker '{self._backend_name}' closed the connection unexpectedly"
                )

            if header.get("status") == "error":
                raise ValueError(header.get("message", "Worker synthesis error"))

            # Read OGG bytes
            ogg_bytes = await asyncio.wait_for(
                _recv_audio(self._reader),
                timeout=30.0,
            )

        return SynthesisResult(
            ogg_bytes=ogg_bytes,
            duration_sec=header.get("duration_sec", 0.0),
        )

    # ── Shutdown ───────────────────────────────────────────────────────────────

    def shutdown(self) -> None:
        """
        Gracefully terminate the worker process.
        Called by main.py during server lifespan shutdown.
        Synchronous (not async) because it's called from the lifespan finally block.
        """
        if self._process is None:
            return

        if self._writer is not None:
            try:
                self._writer.close()
            except Exception:
                pass
            self._writer = None
            self._reader = None

        rc = self._process.poll()
        if rc is not None:
            log.debug("Worker '%s' already exited (code %d)", self._backend_name, rc)
            return

        log.info("Sending SIGTERM to worker '%s' (pid %d)", self._backend_name, self._process.pid)
        try:
            self._process.send_signal(signal.SIGTERM)
        except ProcessLookupError:
            pass

        try:
            self._process.wait(timeout=10)
            log.info("Worker '%s' exited cleanly", self._backend_name)
        except subprocess.TimeoutExpired:
            log.warning("Worker '%s' did not exit after SIGTERM — sending SIGKILL", self._backend_name)
            self._process.kill()
            self._process.wait()

        # Clean up socket file
        if self._socket_path:
            try:
                os.unlink(self._socket_path)
            except FileNotFoundError:
                pass

    def _kill_worker(self) -> None:
        """Kill the worker process without waiting (used during failed startup)."""
        if self._process is not None and self._process.poll() is None:
            try:
                self._process.kill()
                self._process.wait()
            except Exception:
                pass


# ── Helpers ───────────────────────────────────────────────────────────────────

def _request_to_dict(request: SynthesisRequest) -> dict:
    """Serialize a SynthesisRequest to a wire-protocol dict for the worker."""
    d: dict = {
        "cmd":        "synthesize",
        "text":       request.text,
        "lang_code":  request.lang_code,
        "speech_rate": request.speech_rate,
    }
    if request.voice_id is not None:
        d["voice_id"] = request.voice_id
    if request.sample_path is not None:
        d["sample_path"] = str(request.sample_path)
    if request.sample_id is not None:
        d["sample_id"] = request.sample_id
    if request.samples_dir is not None:
        d["samples_dir"] = str(request.samples_dir)
    if request.ref_text:
        d["ref_text"] = request.ref_text
    if request.blend:
        d["blend"] = request.blend
    if request.cfg_weight is not None:
        d["cfg_weight"] = request.cfg_weight
    if request.exaggeration is not None:
        d["exaggeration"] = request.exaggeration
    if request.cfg_strength is not None:
        d["cfg_strength"] = request.cfg_strength
    if request.nfe_step is not None:
        d["nfe_step"] = request.nfe_step
    if request.cross_fade_duration is not None:
        d["cross_fade_duration"] = request.cross_fade_duration
    if request.sway_sampling_coef is not None:
        d["sway_sampling_coef"] = request.sway_sampling_coef
    if request.voice_instruct is not None:
        d["voice_instruct"] = request.voice_instruct
    if request.voice_description is not None:
        d["voice_description"] = request.voice_description
    # progress_callback is not serializable — skip it; host handles progress
    return d
