# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# server/backends/lux_backend.py
#
# LuxTTS backend — high-speed flow-matching voice cloning.
# Based on ZipVoice with 4-step distillation and a 48kHz vocoder.
# Apache 2.0 licensed.
#
# Install (in rrv-lux/.venv):
#   git clone https://github.com/ysharma3501/LuxTTS.git lux-src
#   cd lux-src && pip install -r requirements.txt
#   pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
#
# Model — auto-downloads from HuggingFace on first load, or pre-stage:
#   snapshot_download('YatharthS/LuxTTS', local_dir='data/models/lux')
#
# Output: 48000 Hz, mono, float32 — upsampled naturally by pcm_to_ogg.
#
# Speed: ~150x realtime on GPU — by far the fastest provider in the stack.
# Quality: Good voice similarity, natural prosody. Not as expressive as
#          Chatterbox but excellent for high-volume real-time gameplay use.
#
# Prompt encoding: LuxTTS separates encode_prompt() from generate_speech().
# This backend caches the encoded prompt per sample_id to avoid re-encoding
# on consecutive requests for the same speaker — the librosa init cost on
# first encode is ~10s, subsequent encodes are fast but still measurable.

from __future__ import annotations

import asyncio
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)

# Sampling parameters — defaults, overridable via config
_T_SHIFT    = 0.7    # lower = cleaner/less raspy; higher = better quality but artifacts
_RMS        = 0.01   # reference audio volume normalization
_RETURN_SMOOTH = True  # smoothing reduces metallic/raspy artifacts
_NUM_STEPS_DEFAULT = 10


class LuxBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, num_steps: int = _NUM_STEPS_DEFAULT) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._num_steps     = num_steps
        self._model         = None
        self._model_version = ""
        self._sample_rate   = 48000  # LuxTTS always outputs at 48kHz

        # Encoded prompt cache — keyed by sample_id
        # encode_prompt() runs librosa internally which has a 10s init cost
        # on the first call. Cache prevents re-encoding for consecutive requests
        # for the same NPC voice.
        self._prompt_cache: dict[str, object] = {}

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "lux"

    @property
    def display_name(self) -> str:
        return "LuxTTS"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return True

    @property
    def supports_voice_blending(self) -> bool:
        return False

    @property
    def supports_inline_pronunciation(self) -> bool:
        return False

    @property
    def languages(self) -> list[str]:
        return ["en"]

    @property
    def model_version(self) -> str:
        return self._model_version

    def extra_controls(self) -> dict:
        return {
            "lux_num_steps": {
                "type":        "int",
                "default":     10,
                "min":         4,
                "max":         32,
                "description": "ODE solver steps — higher = better quality, slightly slower. Range 4–32.",
            },
            "lux_t_shift": {
                "type":        "float",
                "default":     0.7,
                "min":         0.1,
                "max":         1.0,
                "description": "Sampling time shift — lower = cleaner/less raspy, higher = better voice similarity.",
            },
            "lux_return_smooth": {
                "type":        "bool",
                "default":     True,
                "description": "Apply smoothing to reduce metallic or raspy artifacts.",
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info("LuxTTS loaded: device=%s sample_rate=%d num_steps=%d",
                 self._torch_device, self._sample_rate, self._num_steps)

    def _load_sync(self) -> None:
        # LuxTTS source lives in the worker venv's lux-src directory.
        # The run_worker.py launcher adds it to sys.path.
        try:
            from zipvoice.luxvoice import LuxTTS  # type: ignore
        except ImportError:
            raise RuntimeError(
                "LuxTTS (zipvoice) is not installed. "
                "Ensure lux-src is cloned and installed in the worker venv."
            )

        # Use pre-staged model if available, otherwise auto-download
        local_model_dir = self._models_dir / "lux"
        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            model_path = str(local_model_dir)
            log.info("LuxTTS: loading from %s", local_model_dir)
        else:
            model_path = "YatharthS/LuxTTS"
            log.info("LuxTTS: model not staged locally — downloading from HuggingFace")

        self._model = LuxTTS(model_path, device=self._torch_device)

        # Model version — hash the local model dir if staged, else use HF id
        import hashlib
        if local_model_dir.exists():
            files = sorted(str(p) for p in local_model_dir.rglob("*.pt")
                          if p.is_file())
            if not files:
                files = sorted(str(p) for p in local_model_dir.iterdir()
                               if p.is_file())
            self._model_version = (
                hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
                if files else "hf-lux"
            )
        else:
            self._model_version = "hf-lux"

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("LuxTTS backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "LuxTTS requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        loop = asyncio.get_event_loop()
        ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _get_encoded_prompt(self, sample_id: str, sample_path: Path) -> object:
        """
        Return the encoded prompt for a sample, using cache when available.
        The cache key is sample_id — if the file changes on disk the server
        restarts which clears the cache naturally.
        """
        if sample_id not in self._prompt_cache:
            log.info("LuxTTS: encoding prompt for sample_id='%s' path=%s",
                     sample_id, sample_path)
            encoded = self._model.encode_prompt(
                str(sample_path),
                duration=5,   # reference duration limit in seconds
                rms=_RMS,
            )
            self._prompt_cache[sample_id] = encoded
            log.debug("LuxTTS: prompt encoded and cached for '%s'", sample_id)
        else:
            log.debug("LuxTTS: prompt cache HIT for '%s'", sample_id)
        return self._prompt_cache[sample_id]

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        # Get or encode the speaker prompt
        sample_id = request.sample_id or str(request.sample_path)
        encoded_prompt = self._get_encoded_prompt(sample_id, request.sample_path)

        # Per-request controls — client overrides take priority over backend defaults
        num_steps    = int(request.lux_num_steps)    if request.lux_num_steps    is not None else self._num_steps
        t_shift      = float(request.lux_t_shift)    if request.lux_t_shift      is not None else _T_SHIFT
        return_smooth = bool(request.lux_return_smooth) if request.lux_return_smooth is not None else _RETURN_SMOOTH
        speed        = request.speech_rate if request.speech_rate is not None else 1.0

        log.debug("LuxTTS: synthesizing %d chars steps=%d t_shift=%.2f smooth=%s speed=%.2f",
                  len(request.text), num_steps, t_shift, return_smooth, speed)

        wav = self._model.generate_speech(
            request.text,
            encoded_prompt,
            num_steps=num_steps,
            t_shift=t_shift,
            speed=speed,
            return_smooth=return_smooth,
        )

        # wav is a torch tensor — convert to numpy
        if hasattr(wav, 'numpy'):
            samples = wav.numpy().squeeze()
        else:
            samples = np.array(wav).squeeze()

        # Ensure float32
        samples = samples.astype(np.float32)

        return pcm_to_ogg(samples, self._sample_rate)
