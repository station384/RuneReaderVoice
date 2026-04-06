# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/crisper_whisper_provider.py
#
# CrisperWhisperProvider — verbatim ASR using CrisperWhisper.
# Runs inside the rrv-crisper-whisper worker subprocess.
#
# CrisperWhisper is a fine-tuned Whisper variant optimised for verbatim
# transcription including fillers ("um", "uh"), pauses, and stutters.
# Accurate verbatim transcription improves voice cloning quality since
# ref_text must match what is actually spoken in the reference clip.
#
# Model expected at: models/crisper-whisper/
# Download: huggingface-cli download nyrahealth/CrisperWhisper
#           --local-dir models/crisper-whisper
#
# License: CC-BY-NC-4.0 (non-commercial only — server-side tool, not shipped in client)
# Note: CrisperWhisper also requires the whisper-timestamped package.

from __future__ import annotations

import logging
from pathlib import Path

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)


class CrisperWhisperProvider(AbstractAsrProvider):
    """
    CrisperWhisper ASR provider — verbatim transcription with accurate
    word-level timestamps. Best choice for voice cloning ref_text generation.

    Requires: pip install git+https://github.com/nyrahealth/CrisperWhisper
    """

    def __init__(self, models_dir: Path, gpu_provider: str = "cpu") -> None:
        self._models_dir = Path(models_dir)
        self._gpu_provider = gpu_provider
        self._model_dir = self._models_dir / "crisper-whisper"
        self._model = None
        self._loaded = False

    @property
    def provider_id(self) -> str:
        return "crisper_whisper"

    @property
    def display_name(self) -> str:
        return "CrisperWhisper"

    @property
    def requires_gpu(self) -> bool:
        return True

    @property
    def is_loaded(self) -> bool:
        return self._loaded

    async def load(self) -> None:
        if not self._model_dir.exists():
            raise RuntimeError(
                f"CrisperWhisper model directory not found: {self._model_dir}\n"
                f"Download with: huggingface-cli download nyrahealth/CrisperWhisper "
                f"--local-dir {self._model_dir}"
            )

        log.info("Loading CrisperWhisper from %s (gpu=%s)", self._model_dir, self._gpu_provider)

        try:
            import torch
            from transformers import pipeline

            device = "cuda" if self._gpu_provider == "cuda" else "cpu"
            torch_dtype = torch.float16 if device == "cuda" else torch.float32

            self._pipe = pipeline(
                "automatic-speech-recognition",
                model=str(self._model_dir),
                torch_dtype=torch_dtype,
                device=device,
                local_files_only=True,
            )

            # Patch generation config for verbatim transcription
            gc = getattr(self._pipe.model, "generation_config", None)
            if gc is not None:
                gc.condition_on_previous_text = False
                gc.suppress_tokens = None
                gc.begin_suppress_tokens = None

            self._loaded = True
            log.info("CrisperWhisper loaded successfully on %s", device)

        except Exception as e:
            raise RuntimeError(f"Failed to load CrisperWhisper: {e}") from e

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        import asyncio
        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(None, self._transcribe_sync, request)

    def _transcribe_sync(self, request: TranscriptionRequest) -> TranscriptionResult:
        import soundfile as sf

        raw_audio, sample_rate = sf.read(str(request.audio_path), dtype="float32")
        if len(raw_audio.shape) > 1:
            audio_data = raw_audio.mean(axis=1)
        else:
            audio_data = raw_audio

        log.info(
            "CrisperWhisper transcribing: '%s' sample_rate=%d samples=%d",
            request.audio_path.name, sample_rate, len(audio_data)
        )

        generate_kwargs = {
            "task": "transcribe",
            "language": request.language_hint,
            "no_repeat_ngram_size": 5,
        }

        result = self._pipe(
            {"array": audio_data, "sampling_rate": sample_rate},
            generate_kwargs=generate_kwargs,
            return_timestamps="word",
        )

        text = result.get("text", "").strip() if isinstance(result, dict) else ""
        raw_chunks = result.get("chunks", []) if isinstance(result, dict) else []

        chunks = [
            TranscriptionChunk(
                text=c.get("text", ""),
                start=c.get("timestamp", [None, None])[0] if isinstance(c.get("timestamp"), list) else None,
                end=c.get("timestamp", [None, None])[1] if isinstance(c.get("timestamp"), list) else None,
            )
            for c in raw_chunks
        ]

        log.info(
            "CrisperWhisper output: '%s' chars=%d chunks=%d text='%s'",
            request.audio_path.name, len(text), len(chunks),
            text[:80] + ("..." if len(text) > 80 else "")
        )

        return TranscriptionResult(text=text, language=request.language_hint, chunks=chunks)

    def shutdown(self) -> None:
        try:
            import torch
            del self._pipe
            self._pipe = None
            self._model = None
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
            self._loaded = False
            log.info("CrisperWhisper unloaded")
        except Exception as e:
            log.warning("Error unloading CrisperWhisper: %s", e)
