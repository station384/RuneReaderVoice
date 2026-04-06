# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/qwen_asr_provider.py
#
# QwenAsrProvider — ASR provider using Qwen3-ASR-1.7B or Qwen3-ASR-0.6B.
# Uses the official qwen-asr package with vLLM backend for KV-cache
# accelerated inference. Without vLLM the model is impractically slow.
#
# Timestamps require Qwen3-ForcedAligner-0.6B loaded alongside the ASR model.
# Model paths:
#   models/qwen-asr/Qwen3-ASR-1.7B/
#   models/qwen-asr/Qwen3-ForcedAligner-0.6B/  (optional, for timestamps)
#
# Apache 2.0 license.

from __future__ import annotations

import logging
import os
from pathlib import Path
from typing import Optional

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)

_MODEL_DIRS = {
    "1.7b": "Qwen3-ASR-1.7B",
    "0.6b": "Qwen3-ASR-0.6B",
}

_ALIGNER_DIR = "Qwen3-ForcedAligner-0.6B"

_GPU_MEMORY_UTILIZATION = float(os.environ.get("RRV_QWEN_ASR_GPU_MEMORY_UTILIZATION", "0.4"))


class QwenAsrProvider(AbstractAsrProvider):
    """
    Qwen3-ASR provider using the qwen-asr package with vLLM backend.
    vLLM KV cache is required for practical inference speed.

    Word-level timestamps are produced by Qwen3-ForcedAligner-0.6B when
    present at models/qwen-asr/Qwen3-ForcedAligner-0.6B/. Without it,
    transcription still works but no chunks are returned, which means
    smart clip extraction is skipped.
    """

    def __init__(self, models_dir: Path, gpu_provider: str = "cpu", size: str = "1.7b") -> None:
        self._models_dir = Path(models_dir)
        self._gpu_provider = gpu_provider
        self._size = size.lower()
        self._model_dir: Optional[Path] = None
        self._aligner_dir: Optional[Path] = None
        self._model = None
        self._loaded = False

    @property
    def provider_id(self) -> str:
        return "qwen_asr"

    @property
    def display_name(self) -> str:
        return f"Qwen3-ASR-{self._size.upper()}"

    @property
    def requires_gpu(self) -> bool:
        return True

    @property
    def is_loaded(self) -> bool:
        return self._loaded

    async def load(self) -> None:
        dir_name = _MODEL_DIRS.get(self._size, _MODEL_DIRS["1.7b"])
        self._model_dir = self._models_dir / "qwen-asr" / dir_name

        if not self._model_dir.exists():
            raise RuntimeError(
                f"Qwen3-ASR model directory not found: {self._model_dir}\n"
                f"Download with: huggingface-cli download Qwen/Qwen3-ASR-{self._size.upper()} "
                f"--local-dir {self._model_dir}"
            )

        # ForcedAligner is optional — enables word-level timestamps for clip extraction
        aligner_path = self._models_dir / "qwen-asr" / _ALIGNER_DIR
        if aligner_path.exists() and (aligner_path / "config.json").exists():
            self._aligner_dir = aligner_path
            log.info("Qwen3-ForcedAligner found at %s — timestamps enabled", aligner_path)
        else:
            self._aligner_dir = None
            log.warning(
                "Qwen3-ForcedAligner not found at %s — timestamps disabled, "
                "clip extraction will be skipped. "
                "Download: huggingface-cli download Qwen/Qwen3-ForcedAligner-0.6B "
                "--local-dir %s",
                aligner_path, aligner_path
            )

        import asyncio
        loop = asyncio.get_running_loop()
        await loop.run_in_executor(None, self._load_sync)

    def _load_sync(self) -> None:
        log.info(
            "Loading Qwen3-ASR via qwen-asr package (vLLM backend) — "
            "model=%s gpu_memory_utilization=%.2f aligner=%s",
            self._model_dir, _GPU_MEMORY_UTILIZATION,
            self._aligner_dir.name if self._aligner_dir else "none"
        )
        try:
            from qwen_asr import Qwen3ASRModel

            kwargs = dict(
                model=str(self._model_dir),
                gpu_memory_utilization=_GPU_MEMORY_UTILIZATION,
                max_inference_batch_size=1,
                max_new_tokens=1024,
                max_model_len=2048,
            )

            if self._aligner_dir is not None:
                import torch
                kwargs["forced_aligner"] = str(self._aligner_dir)
                kwargs["forced_aligner_kwargs"] = dict(
                    dtype=torch.bfloat16,
                    device_map="cuda" if self._gpu_provider == "cuda" else "cpu",
                )

            self._model = Qwen3ASRModel.LLM(**kwargs)
            self._loaded = True
            log.info(
                "Qwen3-ASR loaded successfully (timestamps=%s)",
                "enabled" if self._aligner_dir else "disabled"
            )
        except Exception as e:
            raise RuntimeError(f"Failed to load Qwen3-ASR: {e}") from e

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        import asyncio
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(None, self._transcribe_sync, request)

    def _transcribe_sync(self, request: TranscriptionRequest) -> TranscriptionResult:
        if self._model is None:
            raise RuntimeError("Qwen3-ASR model not loaded")

        log.info("Qwen3-ASR transcribing: '%s'", request.audio_path.name)

        try:
            transcribe_kwargs = dict(
                audio=[str(request.audio_path)],
                language=request.language_hint if request.language_hint != "en" else None,
            )
            if self._aligner_dir is not None:
                transcribe_kwargs["return_time_stamps"] = True

            results = self._model.transcribe(**transcribe_kwargs)
        except Exception as e:
            raise RuntimeError(f"Qwen3-ASR transcription failed: {e}") from e

        if not results:
            return TranscriptionResult(text="", language=request.language_hint, chunks=[])

        result = results[0]

        # Extract text
        if hasattr(result, "text"):
            text = result.text.strip()
        elif isinstance(result, str):
            text = result.strip()
        elif isinstance(result, dict):
            text = result.get("text", "").strip()
        else:
            text = str(result).strip()

        # Extract word-level timestamps from ForcedAligner output
        # result.time_stamps is a list of {word, start, end} dicts
        chunks = []
        time_stamps = getattr(result, "time_stamps", None) or []
        for ts in time_stamps:
            if isinstance(ts, dict):
                word = ts.get("word", ts.get("text", ""))
                start = ts.get("start")
                end = ts.get("end")
            else:
                word = getattr(ts, "word", getattr(ts, "text", ""))
                start = getattr(ts, "start", None)
                end = getattr(ts, "end", None)
            if word:
                chunks.append(TranscriptionChunk(text=word, start=start, end=end))

        log.info(
            "Qwen3-ASR output: '%s' chars=%d chunks=%d text='%s'",
            request.audio_path.name, len(text), len(chunks),
            text[:80] + ("..." if len(text) > 80 else "")
        )

        return TranscriptionResult(text=text, language=request.language_hint, chunks=chunks)

    def shutdown(self) -> None:
        try:
            del self._model
            self._model = None
            self._loaded = False
            log.info("Qwen3-ASR unloaded")
        except Exception as e:
            log.warning("Error unloading Qwen3-ASR: %s", e)
