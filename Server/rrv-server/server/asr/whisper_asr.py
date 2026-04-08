# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/whisper_asr.py
#
# WhisperAsrProvider — subprocess-based Whisper ASR provider.
# Runs Whisper in a separate process via rrv-whisper/run_asr_worker.py,
# using the same WorkerAsr socket protocol as other ASR providers.
# Loads on demand, transcribes a batch, then unloads to free VRAM/RAM.
#
# Uses the existing rrv-whisper venv which already has transformers,
# torch, and soundfile installed alongside the TTS workers.

from __future__ import annotations

import logging
from pathlib import Path

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult

log = logging.getLogger(__name__)


class WhisperAsrProvider(AbstractAsrProvider):
    """
    Whisper ASR via subprocess worker.
    Delegates all work to rrv-whisper/run_asr_worker.py which runs the
    in-process Whisper logic inside an isolated subprocess so that GPU
    memory is released when the process exits after each batch.

    This provider itself is a thin stub — the real implementation lives
    in the subprocess. The WorkerAsr class in worker_asr.py handles all
    the process lifecycle, socket communication, and load/unload logic.
    """

    def __init__(self, whisper_model_dir: Path) -> None:
        self._whisper_dir = whisper_model_dir
        self._available = False

    @property
    def provider_id(self) -> str:
        return "whisper"

    @property
    def display_name(self) -> str:
        return "Whisper"

    @property
    def requires_gpu(self) -> bool:
        # Whisper can run on CPU — does not block GPU eviction.
        # Set False so ResourceManager doesn't evict backends for it.
        return False

    @property
    def is_loaded(self) -> bool:
        return self._available

    async def load(self) -> None:
        """Validate that the Whisper model directory exists."""
        if not self._whisper_dir.exists():
            raise RuntimeError(
                f"Whisper model directory not found: {self._whisper_dir}"
            )
        if not (self._whisper_dir / "config.json").exists():
            raise RuntimeError(
                f"Whisper model directory appears incomplete: {self._whisper_dir}"
            )
        self._available = True
        log.info("Whisper ASR provider ready — model: %s", self._whisper_dir)

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        raise NotImplementedError(
            "WhisperAsrProvider.transcribe() should not be called directly. "
            "This provider is used as a subprocess via WorkerAsr."
        )

    def shutdown(self) -> None:
        self._available = False
