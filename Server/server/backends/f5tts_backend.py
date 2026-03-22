# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/f5tts_backend.py
#
# F5-TTS backend (Phase 5b — GPU-gated).
#
# Requires: pip install f5-tts torch torchaudio
#
# Model files MUST be pre-placed in RRV_MODELS_DIR/f5tts/ before starting.
# The server NEVER downloads anything at runtime — no HuggingFace, no Whisper.
#
# Required files:
#   data/models/f5tts/F5TTS_v1_Base/model_1250000.safetensors  (~1.2 GB)
#
# Download from: https://huggingface.co/SWivid/F5-TTS
#   File path in repo: ckpts/F5TTS_v1_Base/model_1250000.safetensors
#
# Reference samples MUST include a .ref.txt transcript sidecar.
# F5-TTS synthesis is refused if the sidecar is missing — Whisper is never
# invoked at runtime because this server is designed for air-gapped deployment.
#
# Supported:
#   - Zero-shot voice cloning from reference audio clip (6-15 seconds)
#   - Strong English quality; multilingual expanding
#   - CPU (very slow ~60s/sentence) or CUDA/ROCm GPU (fast)

from __future__ import annotations

import asyncio
import io
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from ..cache import compute_file_hash
from ..samples import resolve_sample_for_provider
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)


class F5TtsBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._model_version = ""
        self._infer_lock    = asyncio.Lock()

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "f5tts"

    @property
    def display_name(self) -> str:
        return "F5-TTS"

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
        return ["en", "zh-cn"]

    @property
    def model_version(self) -> str:
        return self._model_version

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "F5-TTS loaded: model_version=%s device=%s",
            self._model_version, self._torch_device,
        )

    def _load_sync(self) -> None:
        try:
            from f5_tts.api import F5TTS
        except ImportError:
            raise RuntimeError(
                "f5-tts is not installed. Run: pip install f5-tts"
            )

        model_file = self._models_dir / "f5tts" / "F5TTS_v1_Base" / "model_1250000.safetensors"
        vocos_dir  = self._models_dir / "f5tts" / "vocos"

        if not model_file.exists():
            raise RuntimeError(
                f"F5-TTS model file not found: {model_file}\n"
                f"Download from: https://huggingface.co/SWivid/F5-TTS\n"
                f"  File path in repo: ckpts/F5TTS_v1_Base/model_1250000.safetensors\n"
                f"  Place at: {model_file}"
            )

        if not vocos_dir.exists() or not (vocos_dir / "pytorch_model.bin").exists():
            raise RuntimeError(
                f"F5-TTS Vocos vocoder not found: {vocos_dir}\n"
                f"Download from: https://huggingface.co/charactr/vocos-mel-24khz\n"
                f"  Files needed: config.yaml, pytorch_model.bin\n"
                f"  Place in: {vocos_dir}"
            )

        log.info("F5-TTS: loading from %s", model_file)
        log.info("F5-TTS: loading Vocos from %s", vocos_dir)
        self._model = F5TTS(
            model="F5TTS_v1_Base",
            ckpt_file=str(model_file),
            vocoder_local_path=str(vocos_dir),
            device=self._torch_device,
        )
        self._model_version = compute_file_hash(model_file)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []  # F5-TTS has no built-in named voices — reference clips only

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("F5-TTS backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "F5-TTS requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        if not request.ref_text:
            raise ValueError(
                f"F5-TTS requires a transcript of the reference audio clip. "
                f"Create a .ref.txt sidecar file alongside the sample containing "
                f"the verbatim spoken text. "
                f"The server does not use Whisper for auto-transcription."
            )

        if request.blend:
            raise ValueError("F5-TTS does not support voice blending.")

        loop = asyncio.get_event_loop()
        async with self._infer_lock:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        # Prefer the provider-specific extracted clip (<=10s, pre-validated for F5).
        # Falls back to the route-resolved sample_path if no -f5 clip exists yet.
        sample_path = request.sample_path
        ref_text    = request.ref_text

        if sample_path is not None and request.samples_dir is not None:
            provider_info = resolve_sample_for_provider(
                samples_dir=request.samples_dir,
                sample_id=request.sample_id or "",
                provider_id=self.provider_id,
            )
            if provider_info and provider_info.ref_text:
                from pathlib import Path as _Path
                candidate = request.samples_dir / provider_info.filename
                if candidate.exists():
                    sample_path = candidate
                    ref_text    = provider_info.ref_text
                    log.debug(
                        "F5-TTS: using provider clip '%s' for sample '%s'",
                        provider_info.filename, request.sample_id
                    )

        # nfe_step: ODE solver steps (default 32; 16=fast, 64=high quality)
        # cfg_strength: reference adherence (default 2.0; range 1.0-4.0)
        # cross_fade_duration: stitches F5's internal text chunks (default 0.15s)
        nfe_step            = request.nfe_step            if request.nfe_step is not None else 32
        cfg_strength_val    = request.cfg_strength        if request.cfg_strength is not None else 2.0
        cross_fade_duration = request.cross_fade_duration if request.cross_fade_duration is not None else 0.15

        log.debug(
            "F5-TTS synth: nfe=%d cfg=%.2f xfade=%.3f speed=%.2f sample='%s'",
            nfe_step, cfg_strength_val, cross_fade_duration,
            request.speech_rate, sample_path.name if sample_path else "none",
        )

        wav, sample_rate, _ = self._model.infer(
            ref_file=str(sample_path),
            ref_text=ref_text,
            # Trailing punctuation gives the model runway to complete the
            # final word without clipping the last syllable.
            gen_text=request.text.rstrip() + " ...  ",
            speed=request.speech_rate,
            nfe_step=nfe_step,
            cfg_strength=cfg_strength_val,
            cross_fade_duration=cross_fade_duration,
            remove_silence=False,
        )

        samples = np.array(wav, dtype=np.float32)
        if samples.ndim > 1:
            samples = samples.squeeze()

        return pcm_to_ogg(samples, sample_rate)


# ── Helpers ───────────────────────────────────────────────────────────────────

# OGG encoding — see backends/audio.py
