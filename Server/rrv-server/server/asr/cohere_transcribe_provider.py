# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/cohere_transcribe_provider.py
#
# CohereTranscribeProvider — ASR using Cohere Transcribe (2B Conformer).
# Runs inside the rrv-cohere-transcribe worker subprocess.
#
# Model expected at: models/cohere-transcribe/
# Download: huggingface-cli download CohereLabs/cohere-transcribe-03-2026
#           --local-dir models/cohere-transcribe
# (Requires agreeing to HuggingFace terms before download)
#
# License: Apache 2.0
# Supports 14 languages. 2B parameter Conformer encoder + Transformer decoder.

from __future__ import annotations

import logging
from pathlib import Path

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)


class CohereTranscribeProvider(AbstractAsrProvider):
    """
    Cohere Transcribe ASR provider.
    2B parameter dedicated ASR model, Apache 2.0.
    Uses model.transcribe() API which handles long-form audio chunking automatically.
    """

    def __init__(self, models_dir: Path, gpu_provider: str = "cpu") -> None:
        self._models_dir = Path(models_dir)
        self._gpu_provider = gpu_provider
        self._model_dir = self._models_dir / "cohere-transcribe"
        self._model = None
        self._loaded = False

    @property
    def provider_id(self) -> str:
        return "cohere_transcribe"

    @property
    def display_name(self) -> str:
        return "Cohere Transcribe"

    @property
    def requires_gpu(self) -> bool:
        return True

    @property
    def is_loaded(self) -> bool:
        return self._loaded

    async def load(self) -> None:
        if not self._model_dir.exists():
            raise RuntimeError(
                f"Cohere Transcribe model directory not found: {self._model_dir}\n"
                f"Download with: huggingface-cli download CohereLabs/cohere-transcribe-03-2026 "
                f"--local-dir {self._model_dir}\n"
                f"Note: You must agree to terms on HuggingFace before downloading."
            )

        log.info("Loading Cohere Transcribe from %s (gpu=%s)", self._model_dir, self._gpu_provider)

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
            log.info("Cohere Transcribe loaded successfully on %s", device)

        except Exception as e:
            raise RuntimeError(f"Failed to load Cohere Transcribe: {e}") from e

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
            "Cohere Transcribe transcribing: '%s' sample_rate=%d samples=%d",
            request.audio_path.name, sample_rate, len(audio_data)
        )

        try:
            # Use model.transcribe() if available (Cohere's recommended API)
            if hasattr(self._model, "transcribe"):
                result = self._model.transcribe(
                    {"array": audio_data, "sampling_rate": sample_rate},
                    language=request.language_hint,
                )
                text = result if isinstance(result, str) else result.get("text", "")
                chunks = []
            else:
                # Fallback: standard HF pipeline approach
                inputs = self._processor(
                    audio_data,
                    sampling_rate=sample_rate,
                    return_tensors="pt",
                )
                device = next(self._model.parameters()).device
                inputs = {k: v.to(device) for k, v in inputs.items()}

                with torch.no_grad():
                    generated_ids = self._model.generate(**inputs, max_new_tokens=512)

                text = self._processor.batch_decode(
                    generated_ids,
                    skip_special_tokens=True,
                )[0].strip()
                chunks = []

        except Exception as e:
            raise RuntimeError(f"Cohere Transcribe inference failed: {e}") from e

        log.info(
            "Cohere Transcribe output: '%s' chars=%d text='%s'",
            request.audio_path.name, len(text),
            text[:80] + ("..." if len(text) > 80 else "")
        )

        return TranscriptionResult(text=text, language=request.language_hint, chunks=chunks)

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
            log.info("Cohere Transcribe unloaded")
        except Exception as e:
            log.warning("Error unloading Cohere Transcribe: %s", e)
