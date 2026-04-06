# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/qwen_asr_provider.py
#
# QwenAsrProvider — ASR provider using Qwen3-ASR-1.7B or Qwen3-ASR-0.6B.
# Runs inside the rrv-qwen-asr worker subprocess.
#
# Model expected at: models/qwen-asr/Qwen3-ASR-1.7B/ or Qwen3-ASR-0.6B/
# Download: huggingface-cli download Qwen/Qwen3-ASR-1.7B --local-dir models/qwen-asr/Qwen3-ASR-1.7B
#
# Apache 2.0 license.

from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)

_MODEL_DIRS = {
    "1.7b": "Qwen3-ASR-1.7B",
    "0.6b": "Qwen3-ASR-0.6B",
}


class QwenAsrProvider(AbstractAsrProvider):
    """
    Qwen3-ASR ASR provider.
    State-of-the-art open ASR model, Apache 2.0.
    Supports 52 languages and dialects.
    """

    def __init__(self, models_dir: Path, gpu_provider: str = "cpu", size: str = "1.7b") -> None:
        self._models_dir = Path(models_dir)
        self._gpu_provider = gpu_provider
        self._size = size.lower()
        self._model_dir: Optional[Path] = None
        self._processor = None
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

        log.info("Loading Qwen3-ASR from %s (gpu=%s)", self._model_dir, self._gpu_provider)

        try:
            import torch
            from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor

            device = "cuda" if self._gpu_provider == "cuda" else "cpu"
            torch_dtype = torch.float16 if device == "cuda" else torch.float32

            self._processor = AutoProcessor.from_pretrained(
                str(self._model_dir),
                local_files_only=True,
                trust_remote_code=True,
            )
            self._model = AutoModelForSpeechSeq2Seq.from_pretrained(
                str(self._model_dir),
                torch_dtype=torch_dtype,
                low_cpu_mem_usage=True,
                local_files_only=True,
                trust_remote_code=True,
            )
            self._model.to(device)
            self._model.eval()
            self._loaded = True
            log.info("Qwen3-ASR loaded successfully on %s", device)

        except Exception as e:
            raise RuntimeError(f"Failed to load Qwen3-ASR: {e}") from e

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        import asyncio
        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(None, self._transcribe_sync, request)

    def _transcribe_sync(self, request: TranscriptionRequest) -> TranscriptionResult:
        import soundfile as sf
        import torch

        raw_audio, sample_rate = sf.read(str(request.audio_path), dtype="float32")
        if len(raw_audio.shape) > 1:
            audio_data = raw_audio.mean(axis=1)
        else:
            audio_data = raw_audio

        log.info(
            "Qwen3-ASR transcribing: '%s' sample_rate=%d samples=%d",
            request.audio_path.name, sample_rate, len(audio_data)
        )

        inputs = self._processor(
            audio_data,
            sampling_rate=sample_rate,
            return_tensors="pt",
            language=request.language_hint,
        )

        device = next(self._model.parameters()).device
        inputs = {k: v.to(device) for k, v in inputs.items()}

        with torch.no_grad():
            generated_ids = self._model.generate(
                **inputs,
                max_new_tokens=512,
                return_timestamps=request.return_timestamps,
            )

        transcription = self._processor.batch_decode(
            generated_ids,
            skip_special_tokens=True,
            decode_with_timestamps=request.return_timestamps,
        )[0].strip()

        log.info(
            "Qwen3-ASR output: '%s' chars=%d text='%s'",
            request.audio_path.name, len(transcription),
            transcription[:80] + ("..." if len(transcription) > 80 else "")
        )

        return TranscriptionResult(
            text=transcription,
            language=request.language_hint,
            chunks=[],  # Qwen3-ASR timestamp support varies by version
        )

    def shutdown(self) -> None:
        try:
            import torch
            del self._model
            del self._processor
            self._model = None
            self._processor = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
            self._loaded = False
            log.info("Qwen3-ASR unloaded")
        except Exception as e:
            log.warning("Error unloading Qwen3-ASR: %s", e)
