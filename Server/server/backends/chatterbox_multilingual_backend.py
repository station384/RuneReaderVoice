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
#
# server/backends/chatterbox_multilingual_backend.py
#
# Chatterbox Multilingual backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Same 0.5B architecture as the original Chatterbox full model, retrained
# for 23-language support. Uses ChatterboxMultilingualTTS from chatterbox.mtl_tts.
#
# Install (chatterbox-tts 0.1.7+, Python 3.11):
#   pip install chatterbox-tts
#   pip install --no-deps s3tokenizer
#   pip install onnx>=1.16.0
#
# Model files — place in data/models/chatterbox-ml/:
#   Download from: https://huggingface.co/ResembleAI/chatterbox-multilingual
#
# Supported languages (pass as lang_code in the synthesis request):
#   ar da de el en es fi fr he hi it ja ko ms nl no pl pt ru sv sw tr zh
#
# Notes:
#   - 10-step diffusion decoder (same as chatterbox_full, slower than Turbo)
#   - No paralinguistic tags ([laugh] etc.) — use chatterbox (Turbo) for those
#   - cfg_weight and exaggeration supported same as other Chatterbox backends
#   - lang_code defaults to "en" if not provided

from __future__ import annotations

import asyncio
import logging
import os
import tempfile
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..cache import compute_file_hash

log = logging.getLogger(__name__)

# ISO 639-1 codes supported by ChatterboxMultilingualTTS
SUPPORTED_LANGUAGES = [
    "ar", "da", "de", "el", "en", "es", "fi", "fr",
    "he", "hi", "it", "ja", "ko", "ms", "nl", "no",
    "pl", "pt", "ru", "sv", "sw", "tr", "zh",
]


class ChatterboxMultilingualBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, max_concurrent: int = 1) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._model_version = ""
        self._MAX_CONCURRENT = max_concurrent
        self._voice_cond    = asyncio.Condition()
        self._active_voice_key: str | None = None
        self._active_count  = 0

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or "en"
        return f"{sample_key}|{lang_key}"

    async def _acquire_voice_slot(self, voice_key: str) -> None:
        async with self._voice_cond:
            while True:
                if self._active_voice_key is None:
                    self._active_voice_key = voice_key
                    self._active_count = 1
                    return
                if self._active_voice_key == voice_key and self._active_count < self._MAX_CONCURRENT:
                    self._active_count += 1
                    return
                await self._voice_cond.wait()

    async def _release_voice_slot(self, voice_key: str) -> None:
        async with self._voice_cond:
            if self._active_voice_key == voice_key and self._active_count > 0:
                self._active_count -= 1
                if self._active_count == 0:
                    self._active_voice_key = None
            self._voice_cond.notify_all()

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "chatterbox_multilingual"

    @property
    def display_name(self) -> str:
        return "Chatterbox Multilingual"

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
        return SUPPORTED_LANGUAGES

    @property
    def model_version(self) -> str:
        return self._model_version

    def extra_controls(self) -> dict:
        return {
            "cfg_weight": {
                "type":        "float",
                "default":     0.5,
                "min":         0.0,
                "max":         3.0,
                "description": "Classifier-free guidance weight for prompt adherence.",
            },
            "exaggeration": {
                "type":        "float",
                "default":     0.5,
                "min":         0.0,
                "max":         3.0,
                "description": "Emotion and expressiveness control. 0.0=monotone, 0.5=natural, 1.0+=dramatic.",
            },
            "language_id": {
                "type":        "string",
                "default":     "en",
                "options":     SUPPORTED_LANGUAGES,
                "description": (
                    "Language code for synthesis. Standard ISO 639-1 codes (e.g. 'en', 'fr', 'de') "
                    "are the documented set. Extended variants such as 'en-gb', 'en-us', 'en-us-ny' "
                    "are passed through verbatim — the model may or may not respond to them."
                ),
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "Chatterbox Multilingual loaded: model_version=%s device=%s languages=%d",
            self._model_version, self._torch_device, len(SUPPORTED_LANGUAGES),
        )

    def _load_sync(self) -> None:
        self._apply_float32_patches()

        try:
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed or does not include mtl_tts. "
                "Run: pip install chatterbox-tts>=0.1.7"
            )

        local_model_dir = self._models_dir / "chatterbox-ml"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox Multilingual: loading from %s", local_model_dir)
            self._model = ChatterboxMultilingualTTS.from_local(
                str(local_model_dir),
                self._torch_device,
            )
            import hashlib
            files = sorted(str(p) for p in local_model_dir.rglob("*.safetensors"))
            self._model_version = (
                hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
                if files else "local"
            )
        else:
            raise RuntimeError(
                f"Chatterbox Multilingual model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-multilingual\n"
                f"Place all files in: {local_model_dir}"
            )

    # ── Patches ───────────────────────────────────────────────────────────────

    def _apply_float32_patches(self) -> None:
        """
        Force float32 throughout Chatterbox's pipeline.

        librosa.load() returns float64 on CPU. Chatterbox was only tested on
        CUDA where the driver enforces float32. On CPU the float64 propagates
        through torch.stft() and hits dtype assertion failures. These patches
        intercept at the three points where float64 can enter the pipeline.

        Uses module-level sentinels (_rrv_patched) so patches are applied
        exactly once even when multiple Chatterbox backends are loaded.
        """
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _orig_load = librosa.load
            def _float32_load(path, *args, **kwargs):
                y, sr = _orig_load(path, *args, **kwargs)
                return y.astype(np.float32), sr
            librosa.load = _float32_load
            librosa._rrv_patched = True
            log.info("Chatterbox Multilingual: patched librosa.load -> float32")

        try:
            import torch
            import torch.nn.functional as F
            from chatterbox.models.s3tokenizer.s3tokenizer import S3Tokenizer

            if not getattr(S3Tokenizer, '_rrv_patched', False):
                orig_log_mel = S3Tokenizer.log_mel_spectrogram

                def _patched_log_mel(self_t, audio, padding=0):
                    if not torch.is_tensor(audio):
                        audio = torch.from_numpy(audio)
                    audio = audio.to(self_t.device)
                    if padding > 0:
                        audio = F.pad(audio, (0, padding))
                    stft = torch.stft(
                        audio, self_t.n_fft,
                        orig_log_mel.__globals__.get('S3_HOP', 160),
                        window=self_t.window.to(self_t.device),
                        return_complex=True,
                    )
                    magnitudes = stft[..., :-1].abs() ** 2
                    mel_filters = self_t._mel_filters.to(self_t.device)
                    magnitudes = magnitudes.to(dtype=mel_filters.dtype)
                    mel_spec = mel_filters @ magnitudes
                    log_spec = torch.clamp(mel_spec, min=1e-10).log10()
                    log_spec = torch.maximum(log_spec, log_spec.max() - 8.0)
                    log_spec = (log_spec + 4.0) / 4.0
                    return log_spec

                S3Tokenizer.log_mel_spectrogram = _patched_log_mel
                S3Tokenizer._rrv_patched = True
                log.debug("Chatterbox Multilingual: patched S3Tokenizer.log_mel_spectrogram")
        except Exception as e:
            log.warning("Chatterbox Multilingual: could not patch S3Tokenizer: %s", e)

        try:
            from chatterbox.models.voice_encoder.voice_encoder import VoiceEncoder

            if not getattr(VoiceEncoder, '_rrv_patched', False):
                _orig_embeds = VoiceEncoder.embeds_from_wavs

                def _patched_embeds(self_ve, wavs, *args, **kwargs):
                    wavs = [w.astype(np.float32) if hasattr(w, 'astype') else w for w in wavs]
                    return _orig_embeds(self_ve, wavs, *args, **kwargs)

                VoiceEncoder.embeds_from_wavs = _patched_embeds
                VoiceEncoder._rrv_patched = True
                log.debug("Chatterbox Multilingual: patched VoiceEncoder.embeds_from_wavs -> float32")
        except Exception as e:
            log.warning("Chatterbox Multilingual: could not patch VoiceEncoder: %s", e)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []  # Reference clip required — no built-in named voices

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox Multilingual backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "Chatterbox Multilingual requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        if request.blend:
            raise ValueError("Chatterbox Multilingual does not support voice blending.")

        # Normalize lang_code: strip sub-locale (e.g. "en-gb" -> "en", "zh-cn" -> "zh")
        # then validate against supported languages, falling back to "en" if unknown.
        raw_lang = (request.lang_code or "en").lower().strip()
        base_lang = raw_lang.split("-")[0]

        if raw_lang in SUPPORTED_LANGUAGES:
            lang = raw_lang
        elif base_lang in SUPPORTED_LANGUAGES:
            lang = base_lang
            if raw_lang != base_lang:
                log.debug(
                    "Chatterbox Multilingual: lang_code='%s' normalized to '%s'",
                    raw_lang, lang,
                )
        else:
            log.warning(
                "Chatterbox Multilingual: unsupported lang_code='%s' (base='%s') — falling back to 'en'.",
                raw_lang, base_lang,
            )
            lang = "en"

        log.info("Chatterbox Multilingual: lang_code='%s' -> lang='%s'", raw_lang, lang)

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            ogg_bytes = await loop.run_in_executor(
                None, self._synthesize_sync, request, lang
            )
        finally:
            await self._release_voice_slot(voice_key)

        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest, lang: str) -> bytes:
        import numpy as np
        import soundfile as sf

        info = sf.info(str(request.sample_path))
        if info.duration < 5.0:
            raise ValueError(
                f"Chatterbox Multilingual requires a reference clip of at least 5 seconds. "
                f"'{request.sample_path.name}' is only {info.duration:.1f}s."
            )

        # Write reference as PCM_16 WAV — forces librosa to return float32 on load,
        # preventing dtype propagation errors through the model pipeline.
        audio_data, sr = sf.read(str(request.sample_path), dtype='float32')
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            tmp_path = tmp.name
        try:
            sf.write(tmp_path, audio_data, sr, subtype='PCM_16')
            log.info(
                "Chatterbox Multilingual: synthesizing lang=%s text=%.60r",
                lang, request.text,
            )
            wav = self._model.generate(
                text=request.text,
                audio_prompt_path=tmp_path,
                language_id=lang,
                cfg_weight=request.cfg_weight   if request.cfg_weight   is not None else 0.5,
                exaggeration=request.exaggeration if request.exaggeration is not None else 0.5,
            )
        finally:
            os.unlink(tmp_path)

        samples = wav.squeeze().numpy() if hasattr(wav, 'numpy') else np.array(wav).squeeze()
        return pcm_to_ogg(samples, self._model.sr)
