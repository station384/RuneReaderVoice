# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/whisper_asr.py
#
# WhisperAsrProvider — in-process ASR provider using OpenAI Whisper.
# Wraps the existing _load_whisper / _transcribe_one_static logic from
# TranscriptionService so that Whisper participates in the ASR provider
# abstraction without requiring a subprocess.
#
# CPU-capable: loads on CPU when GPU VRAM is insufficient.
# Unloads after each transcription pass to free memory (existing behaviour).

from __future__ import annotations

import logging
import os
from pathlib import Path
from typing import Optional

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult, TranscriptionChunk

log = logging.getLogger(__name__)

# Minimum free VRAM (MiB) required to load Whisper on GPU
_WHISPER_MIN_VRAM_MIB = 1800


class WhisperAsrProvider(AbstractAsrProvider):
    """
    In-process Whisper ASR provider.
    Loads the model lazily per-request and unloads immediately after.
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
        # Can run on CPU — does not require GPU eviction
        return False

    @property
    def is_loaded(self) -> bool:
        return self._available

    async def load(self) -> None:
        """Validate that the Whisper model directory exists and is complete."""
        if not self._whisper_dir.exists():
            raise RuntimeError(
                f"Whisper model directory not found: {self._whisper_dir}"
            )
        if not (self._whisper_dir / "config.json").exists():
            raise RuntimeError(
                f"Whisper model directory appears incomplete (config.json not found): {self._whisper_dir}"
            )
        self._available = True
        log.info("Whisper ASR provider ready — model: %s", self._whisper_dir)

    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        """
        Load Whisper, transcribe the audio, unload Whisper, return result.
        Runs synchronously in a thread pool to avoid blocking the event loop.
        """
        import asyncio
        loop = asyncio.get_running_loop()
        result = await loop.run_in_executor(None, self._transcribe_sync, request)
        return result

    def _transcribe_sync(self, request: TranscriptionRequest) -> TranscriptionResult:
        """Synchronous transcription — runs in thread pool."""
        pipe = self._load_whisper()
        if pipe is None:
            raise RuntimeError("Whisper failed to load — check model directory and dependencies")

        try:
            text, language, chunks = self._transcribe_one_static(
                pipe,
                request.audio_path,
                language_hint=request.language_hint,
            )
        finally:
            # Unload to free memory
            self._unload_whisper(pipe)

        if not text:
            return TranscriptionResult(text="", language=language or "en", chunks=[])

        tc = [
            TranscriptionChunk(
                text=c.get("text", ""),
                start=c.get("timestamp", [None, None])[0] if isinstance(c.get("timestamp"), list) else None,
                end=c.get("timestamp", [None, None])[1] if isinstance(c.get("timestamp"), list) else None,
            )
            for c in (chunks or [])
        ]

        return TranscriptionResult(text=text, language=language or "en", chunks=tc)

    def _load_whisper(self):
        """Load Whisper pipeline. Returns pipeline or None on failure."""
        try:
            import torch
            from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
        except ImportError:
            log.error("transformers or torch not installed — cannot run Whisper transcription")
            return None

        try:
            # VRAM check — load on CPU if insufficient headroom
            _cuda_ok = False
            if torch.cuda.is_available():
                _free_mib = (
                    torch.cuda.get_device_properties(0).total_memory
                    - torch.cuda.memory_reserved(0)
                ) / (1024 * 1024)
                _cuda_ok = _free_mib >= _WHISPER_MIN_VRAM_MIB
                if not _cuda_ok:
                    log.info(
                        "Whisper: only %.0fMiB VRAM free (need %dMiB) — loading on CPU",
                        _free_mib, _WHISPER_MIN_VRAM_MIB
                    )

            device = "cuda" if _cuda_ok else "cpu"
            torch_dtype = torch.float16 if _cuda_ok else torch.float32

            log.info("Loading Whisper from %s (device=%s)", self._whisper_dir, device)

            model = AutoModelForSpeechSeq2Seq.from_pretrained(
                str(self._whisper_dir),
                torch_dtype=torch_dtype,
                low_cpu_mem_usage=True,
                use_safetensors=True,
                local_files_only=True,
            )
            model.to(device)

            processor = AutoProcessor.from_pretrained(
                str(self._whisper_dir),
                local_files_only=True,
            )

            pipe = pipeline(
                "automatic-speech-recognition",
                model=model,
                tokenizer=processor.tokenizer,
                feature_extractor=processor.feature_extractor,
                torch_dtype=torch_dtype,
                device=device,
            )

            gc = getattr(model, "generation_config", None)
            if gc is not None:
                gc.condition_on_previous_text = False
                gc.suppress_tokens = None
                gc.begin_suppress_tokens = None

            log.info("Whisper loaded successfully")
            return pipe

        except Exception as e:
            log.error("Whisper load failed: %s", e)
            return None

    def _unload_whisper(self, pipe) -> None:
        """Unload Whisper pipeline and free memory."""
        try:
            import torch
            del pipe
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception as e:
            log.warning("Error unloading Whisper: %s", e)

    @staticmethod
    def _transcribe_one_static(pipe, audio_path: Path, language_hint: str = "en") -> tuple[str, str, list]:
        """Transcribe a single audio file. Returns (text, language, chunks)."""
        import soundfile as sf
        import numpy as np

        try:
            raw_audio, sample_rate = sf.read(str(audio_path), dtype="float32")
        except Exception as e:
            raise RuntimeError(f"Cannot read audio file '{audio_path}': {e}") from e

        # Mix to mono
        if len(raw_audio.shape) > 1:
            audio_data = raw_audio.mean(axis=1)
        else:
            audio_data = raw_audio

        log.info(
            "Whisper input: audio='%s' sample_rate=%d samples=%d",
            audio_path, sample_rate, len(audio_data)
        )

        generate_kwargs = {
            "task": "transcribe",
            "language": language_hint,
            "no_repeat_ngram_size": 5,
        }

        result = pipe(
            {"array": audio_data, "sampling_rate": sample_rate},
            generate_kwargs=generate_kwargs,
            return_timestamps="word",
        )

        text = result.get("text", "").strip() if isinstance(result, dict) else ""
        chunks = result.get("chunks", []) if isinstance(result, dict) else []
        language = generate_kwargs.get("language", "en")

        log.info(
            "Whisper output: audio='%s' chars=%d chunks=%d text='%s'",
            audio_path, len(text), len(chunks), text[:80] + ("..." if len(text) > 80 else "")
        )

        return text, language, chunks
