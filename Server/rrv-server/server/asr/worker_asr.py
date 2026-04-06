# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/worker_asr.py
#
# WorkerAsr — implements AbstractAsrProvider as a transparent proxy to an
# ASR worker subprocess running in its own isolated Python venv.
#
# Mirrors the WorkerBackend pattern used for TTS backends, but the wire
# protocol carries audio paths and returns text rather than audio bytes.
#
# Wire protocol (same length-prefixed JSON as TTS workers):
#   Request:  {cmd: "transcribe", audio_path: str, language_hint: str,
#              return_timestamps: bool}
#   Response: {status: "ok", text: str, language: str,
#              chunks: [{text, start, end}, ...]}
#           | {status: "error", message: str}

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

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)

_WORKER_START_TIMEOUT = 60.0
_PING_TIMEOUT = 10.0
_TRANSCRIPTION_TIMEOUT = 300.0


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


class WorkerAsr(AbstractAsrProvider):
    """
    ASR provider that delegates transcription to a worker subprocess.
    The worker runs in its own isolated venv with its own model dependencies.
    """

    def __init__(
        self,
        provider_name: str,
        venv_path: Path,
        models_dir: Path,
        gpu: str = "auto",
        log_level: str = "info",
        extra_env: Optional[dict] = None,
    ) -> None:
        self._provider_name = provider_name
        self._venv_path = Path(venv_path)
        self._models_dir = models_dir
        self._gpu = gpu
        self._log_level = log_level
        self._extra_env = extra_env or {}

        self._process: Optional[subprocess.Popen] = None
        self._socket_path: Optional[str] = None
        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        self._lock = asyncio.Lock()
        self._capabilities: dict = {}
        self._loaded_flag = False

        # Usage tracking for resource manager
        self._last_used: float = 0.0
        self._use_count: int = 0
        self._manager = None  # set by AsrRegistry after registration

    @property
    def provider_id(self) -> str:
        return self._provider_name

    @property
    def display_name(self) -> str:
        return self._capabilities.get("display_name", self._provider_name)

    @property
    def requires_gpu(self) -> bool:
        return self._capabilities.get("requires_gpu", False)

    @property
    def is_loaded(self) -> bool:
        return self._loaded_flag and self._process is not None and self._process.poll() is None

    @property
    def vram_used_mib(self) -> float:
        return float(self._capabilities.get("vram_used_mib", 0.0))

    @property
    def resource_id(self) -> str:
        return self._provider_name

    @property
    def last_used(self) -> float:
        return self._last_used

    @property
    def use_count(self) -> int:
        return self._use_count

    async def unload(self) -> None:
        """
        Shut down the worker subprocess to free GPU memory.
        Can be reloaded on demand by calling load() again.
        Called by ResourceManager during eviction.
        """
        log.info("Unloading ASR worker '%s' to free GPU memory", self._provider_name)
        import asyncio
        await asyncio.get_event_loop().run_in_executor(None, self.shutdown)

    def _venv_python(self) -> Path:
        candidate = self._venv_path / "bin" / "python"
        if candidate.exists():
            return candidate
        return self._venv_path / "Scripts" / "python.exe"

    def _wait_for_ready(self) -> None:
        assert self._process is not None
        assert self._process.stdout is not None
        for raw_line in self._process.stdout:
            line = raw_line.decode("utf-8", errors="replace").rstrip()
            if line.startswith("READY:"):
                log.debug("ASR worker stdout: %s", line)
                return
            log.info("[asr_worker:%s] %s", self._provider_name, line)
        rc = self._process.wait()
        raise RuntimeError(
            f"ASR worker '{self._provider_name}' exited with code {rc} before becoming ready"
        )

    async def load(self) -> None:
        python = self._venv_python()
        if not python.exists():
            raise RuntimeError(
                f"ASR worker venv Python not found: {python}\n"
                f"  Provider: {self._provider_name}\n"
                f"  Venv:    {self._venv_path}"
            )

        launcher = self._venv_path.parent / "run_asr_worker.py"
        if not launcher.exists():
            raise RuntimeError(
                f"ASR worker launcher not found: {launcher}\n"
                f"  Each ASR worker directory must contain run_asr_worker.py"
            )

        self._socket_path = os.path.join(
            tempfile.gettempdir(),
            f"rrv_asr_{self._provider_name}_{os.getpid()}.sock",
        )

        cmd = [
            str(python), str(launcher),
            "--provider",   self._provider_name,
            "--socket",     self._socket_path,
            "--models-dir", str(self._models_dir),
            "--gpu",        self._gpu,
            "--log-level",  self._log_level,
        ]

        log.info("Spawning ASR worker '%s' — launcher: %s", self._provider_name, launcher)

        spawn_env = os.environ.copy()
        spawn_env.update(self._extra_env)

        self._process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=None,
            env=spawn_env,
        )

        try:
            await asyncio.wait_for(
                asyncio.get_event_loop().run_in_executor(None, self._wait_for_ready),
                timeout=_WORKER_START_TIMEOUT,
            )
        except asyncio.TimeoutError:
            self._kill_worker()
            raise RuntimeError(
                f"ASR worker '{self._provider_name}' did not become ready within "
                f"{_WORKER_START_TIMEOUT:.0f}s"
            )
        except Exception as exc:
            self._kill_worker()
            raise RuntimeError(f"ASR worker '{self._provider_name}' startup failed: {exc}") from exc

        try:
            self._reader, self._writer = await asyncio.open_unix_connection(self._socket_path)
        except Exception as exc:
            self._kill_worker()
            raise RuntimeError(
                f"Cannot connect to ASR worker socket '{self._socket_path}': {exc}"
            ) from exc

        # Ping
        try:
            await asyncio.wait_for(self._ping(), timeout=_PING_TIMEOUT)
        except asyncio.TimeoutError:
            self._kill_worker()
            raise RuntimeError(f"ASR worker '{self._provider_name}' ping timed out")

        # Capabilities
        await _send_message(self._writer, {"cmd": "capabilities"})
        resp = await asyncio.wait_for(_recv_message(self._reader), timeout=_PING_TIMEOUT)
        if resp is None or resp.get("status") != "ok":
            self._kill_worker()
            raise RuntimeError(f"ASR worker '{self._provider_name}' capabilities failed: {resp}")
        self._capabilities = resp.get("capabilities", {})
        self._loaded_flag = True

        log.info(
            "ASR worker '%s' ready — display_name=%s requires_gpu=%s",
            self._provider_name,
            self.display_name,
            self.requires_gpu,
        )

    async def _ping(self) -> None:
        await _send_message(self._writer, {"cmd": "ping"})
        resp = await _recv_message(self._reader)
        if resp is None or resp.get("status") != "ok":
            raise RuntimeError(f"ASR worker ping failed: {resp!r}")

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        # If unloaded, request resources from manager then reload
        if not self.is_loaded:
            if self._manager is not None:
                await self._manager.request_load(self)
            log.info("ASR worker '%s' is unloaded — reloading on demand", self._provider_name)
            await self.load()
        if not self.is_loaded:
            raise RuntimeError(f"ASR worker '{self._provider_name}' failed to reload")

        msg = {
            "cmd": "transcribe",
            "audio_path": str(request.audio_path),
            "language_hint": request.language_hint,
            "return_timestamps": request.return_timestamps,
        }

        async with self._lock:
            try:
                await _send_message(self._writer, msg)
                resp = await asyncio.wait_for(
                    _recv_message(self._reader),
                    timeout=_TRANSCRIPTION_TIMEOUT,
                )
            except asyncio.TimeoutError:
                raise RuntimeError(
                    f"ASR worker '{self._provider_name}' transcription timed out"
                )

            if resp is None:
                raise RuntimeError(f"ASR worker '{self._provider_name}' closed connection")
            if resp.get("status") == "error":
                raise RuntimeError(resp.get("message", "ASR worker error"))

        self._last_used = time.monotonic()
        self._use_count += 1

        chunks = [
            TranscriptionChunk(
                text=c.get("text", ""),
                start=c.get("start"),
                end=c.get("end"),
            )
            for c in resp.get("chunks", [])
        ]

        return TranscriptionResult(
            text=resp.get("text", ""),
            language=resp.get("language", "en"),
            chunks=chunks,
        )

    def shutdown(self) -> None:
        self._loaded_flag = False
        if self._writer is not None:
            try:
                self._writer.close()
            except Exception:
                pass
            self._writer = None
            self._reader = None

        if self._process is None:
            return

        rc = self._process.poll()
        if rc is not None:
            return

        log.info("Sending SIGTERM to ASR worker '%s' (pid %d)", self._provider_name, self._process.pid)
        try:
            self._process.send_signal(signal.SIGTERM)
        except ProcessLookupError:
            pass

        try:
            self._process.wait(timeout=10)
            log.info("ASR worker '%s' exited cleanly", self._provider_name)
        except subprocess.TimeoutExpired:
            log.warning("ASR worker '%s' did not exit after SIGTERM — sending SIGKILL", self._provider_name)
            self._process.kill()
            self._process.wait()

        if self._socket_path:
            try:
                os.unlink(self._socket_path)
            except FileNotFoundError:
                pass

        self._process = None

    def _kill_worker(self) -> None:
        if self._process is not None and self._process.poll() is None:
            try:
                self._process.kill()
                self._process.wait()
            except Exception:
                pass
