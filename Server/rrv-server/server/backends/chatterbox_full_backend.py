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
# server/backends/chatterbox_full_backend.py
#
# Chatterbox (full) backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Install (chatterbox-tts 0.1.7+, Python 3.11):
#   pip install chatterbox-tts
#   pip install --no-deps s3tokenizer
#   pip install onnx>=1.16.0
#
# Model files — place in data/models/chatterbox-hf/:
#   Download from: https://huggingface.co/ResembleAI/chatterbox-hf
#
# Supports:
#   - Zero-shot voice cloning from a reference audio clip
#   - 0.5B parameters — original Chatterbox model with CFG/exaggeration tuning
#   - CPU (slow) or CUDA/ROCm GPU
#
# Conditionals caching:
#   Chatterbox separates voice conditioning (prepare_conditionals) from text
#   generation. This backend caches the conditionals for the last-used sample
#   so that consecutive chunks for the same NPC voice reuse the identical voice
#   embedding rather than re-deriving it from the audio file each time. This
#   produces more consistent voice character across dialog chunks.
#
#   The voice slot serialization (same speaker runs concurrently, different
#   speakers are serialized) guarantees the cached conditionals always belong
#   to the speaker currently being synthesized — no cross-speaker contamination.
#
#   Cache invalidates when a different sample hash is seen. The PCM_16 temp WAV
#   is kept alive for the lifetime of the cache entry and deleted when evicted.

from __future__ import annotations

import asyncio
import logging
import os
import tempfile
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..utils import compute_file_hash

log = logging.getLogger(__name__)


class ChatterboxFullBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, max_concurrent: int = 2) -> None:
        self._models_dir     = models_dir
        self._torch_device   = torch_device
        self._model          = None
        self._model_version  = ""
        self._MAX_CONCURRENT = max_concurrent
        self._voice_cond     = asyncio.Condition()
        self._active_voice_key: str | None = None
        self._active_count   = 0

        # Conditionals cache — keyed by sample file hash.
        # Stores the hash of the last sample whose conditionals are loaded into
        # self._model.conds, plus the path of the PCM_16 temp WAV we wrote for
        # it. The temp WAV must stay on disk for the lifetime of the cache entry
        # because prepare_conditionals() may read it again if exaggeration changes.
        self._cond_sample_hash: str = ""
        self._cond_tmp_wav: str = ""       # path to the current PCM_16 temp WAV

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or ""
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
        return "chatterbox_full"

    @property
    def display_name(self) -> str:
        return "Chatterbox"

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
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "Chatterbox loaded: model_version=%s device=%s",
            self._model_version, self._torch_device,
        )

    def _load_sync(self) -> None:
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _original_load = librosa.load
            def _float32_load(path, *args, **kwargs):
                y, sr = _original_load(path, *args, **kwargs)
                return y.astype(np.float32), sr
            librosa.load = _float32_load
            librosa._rrv_patched = True
            log.info("Chatterbox: patched librosa.load -> float32")

        try:
            from chatterbox.tts import ChatterboxTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed. Run: pip install chatterbox-tts"
            )

        local_model_dir = self._models_dir / "chatterbox-hf"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox: loading from %s", local_model_dir)
            self._model = ChatterboxTTS.from_local(
                str(local_model_dir),
                self._torch_device,
            )
            self._patch_mel_filters()
            self._patch_t3_hidden_states()
            import hashlib
            files = sorted(str(p) for p in local_model_dir.rglob("*.safetensors"))
            self._model_version = (
                hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
                if files else "local"
            )
        else:
            raise RuntimeError(
                f"Chatterbox model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-hf/tree/main\n"
                f"Place all files in: {local_model_dir}"
            )

    # ── Patches ───────────────────────────────────────────────────────────────

    def _patch_mel_filters(self) -> None:
        """Force float32 through Chatterbox's pipeline. See chatterbox_backend.py for full explanation."""
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _orig = librosa.load
            def _f32(path, *a, **kw):
                y, sr = _orig(path, *a, **kw)
                return y.astype(np.float32), sr
            librosa.load = _f32
            librosa._rrv_patched = True

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
                log.debug("Chatterbox: patched S3Tokenizer.log_mel_spectrogram")
        except Exception as e:
            log.warning("Chatterbox: could not patch S3Tokenizer: %s", e)

        try:
            from chatterbox.models.voice_encoder.voice_encoder import VoiceEncoder
            import numpy as np

            if not getattr(VoiceEncoder, '_rrv_patched', False):
                _orig_embeds = VoiceEncoder.embeds_from_wavs

                def _patched_embeds(self_ve, wavs, *args, **kwargs):
                    wavs = [w.astype(np.float32) if hasattr(w, 'astype') else w for w in wavs]
                    return _orig_embeds(self_ve, wavs, *args, **kwargs)

                VoiceEncoder.embeds_from_wavs = _patched_embeds
                VoiceEncoder._rrv_patched = True
                log.debug("Chatterbox: patched VoiceEncoder.embeds_from_wavs -> float32")
        except Exception as e:
            log.warning("Chatterbox: could not patch VoiceEncoder: %s", e)

    def _patch_t3_hidden_states(self) -> None:
        """
        Patch T3HuggingfaceBackend.forward for transformers 4.57.x compatibility.

        transformers 4.57.x changed LlamaModel to return hidden_states=None in the
        output tuple even when output_hidden_states=True is passed — the final hidden
        state is now only accessible via last_hidden_state. Chatterbox's T3 backend
        indexes tfmr_out.hidden_states[-1] which raises TypeError: 'NoneType' is not
        subscriptable on 4.57.x.

        This patch wraps forward() to fall back to last_hidden_state when hidden_states
        is None, restoring compatibility without modifying the installed package.
        """
        try:
            from chatterbox.models.t3.inference.t3_hf_backend import T3HuggingfaceBackend
            from transformers.modeling_outputs import CausalLMOutputWithCrossAttentions

            if getattr(T3HuggingfaceBackend, '_rrv_hidden_states_patched', False):
                return

            _orig_forward = T3HuggingfaceBackend.forward

            def _patched_forward(self_t3, inputs_embeds, past_key_values=None,
                                 use_cache=True, output_attentions=False,
                                 output_hidden_states=True, return_dict=True):
                import torch
                tfmr_out = self_t3.model(
                    inputs_embeds=inputs_embeds,
                    past_key_values=past_key_values,
                    use_cache=use_cache,
                    output_attentions=output_attentions,
                    output_hidden_states=output_hidden_states,
                    return_dict=True,
                )
                # transformers 4.57.x: hidden_states may be None even when requested.
                # Fall back to last_hidden_state which is always populated.
                if tfmr_out.hidden_states is not None:
                    hidden_states = tfmr_out.hidden_states[-1]
                else:
                    hidden_states = tfmr_out.last_hidden_state
                    log.debug("Chatterbox T3: hidden_states was None, using last_hidden_state "
                              "(transformers 4.57.x compatibility)")

                logits = self_t3.speech_head(hidden_states)
                return CausalLMOutputWithCrossAttentions(
                    logits=logits,
                    past_key_values=tfmr_out.past_key_values,
                    hidden_states=tfmr_out.hidden_states,
                    attentions=tfmr_out.attentions,
                )

            T3HuggingfaceBackend.forward = _patched_forward
            T3HuggingfaceBackend._rrv_hidden_states_patched = True
            log.info("Chatterbox: patched T3HuggingfaceBackend.forward for transformers 4.57.x compatibility")

        except Exception as e:
            log.warning("Chatterbox: could not patch T3HuggingfaceBackend.forward: %s", e)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "Chatterbox requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        if request.blend:
            raise ValueError("Chatterbox does not support voice blending.")

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        finally:
            await self._release_voice_slot(voice_key)

        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _ensure_conditionals(self, sample_path: Path, sample_hash: str) -> None:
        """
        Prepare voice conditionals for the given sample if not already cached.

        On a cache hit (same sample hash as last call), the model's internal
        self.conds is already correct and we skip prepare_conditionals entirely.
        This is safe because the voice slot serialization guarantees only one
        speaker is active at a time — the cached conditionals always belong to
        the current speaker.

        On a cache miss (new speaker), we write a PCM_16 temp WAV (so librosa
        returns float32), call prepare_conditionals(), cache the new hash, and
        clean up the previous temp WAV.
        """
        if sample_hash == self._cond_sample_hash:
            log.debug(
                "Chatterbox: conditionals cache HIT — reusing voice embedding for hash=%s",
                sample_hash[:8],
            )
            return

        import soundfile as sf

        log.info(
            "Chatterbox: conditionals cache MISS — preparing new voice embedding "
            "hash=%s (was %s)",
            sample_hash[:8],
            self._cond_sample_hash[:8] if self._cond_sample_hash else "none",
        )

        # Validate minimum reference clip length
        info = sf.info(str(sample_path))
        if info.duration < 5.0:
            raise ValueError(
                f"Chatterbox requires a reference clip of at least 5 seconds. "
                f"'{sample_path.name}' is only {info.duration:.1f}s."
            )

        # Write a PCM_16 WAV — guarantees librosa returns float32 on load
        audio_data, sr = sf.read(str(sample_path), dtype='float32')
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            new_tmp_path = tmp.name
        sf.write(new_tmp_path, audio_data, sr, subtype='PCM_16')

        # Prepare conditionals into model.conds
        self._model.prepare_conditionals(new_tmp_path)

        # Evict previous temp WAV
        if self._cond_tmp_wav and os.path.exists(self._cond_tmp_wav):
            try:
                os.unlink(self._cond_tmp_wav)
            except Exception as e:
                log.warning("Chatterbox: failed to delete old temp WAV %s: %s",
                            self._cond_tmp_wav, e)

        self._cond_sample_hash = sample_hash
        self._cond_tmp_wav     = new_tmp_path
        log.debug("Chatterbox: new temp WAV at %s", new_tmp_path)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        sample_hash = compute_file_hash(request.sample_path)

        # Ensure the model's internal conditionals match this speaker.
        # On the first call for a sample this writes the temp WAV and calls
        # prepare_conditionals(). On subsequent calls for the same sample it
        # is a no-op — the identical voice embedding is already loaded.
        self._ensure_conditionals(request.sample_path, sample_hash)

        # Generate without audio_prompt_path — model reuses self.conds
        wav = self._model.generate(
            text=request.text,
            cfg_weight=request.cfg_weight   if request.cfg_weight   is not None else 0.5,
            exaggeration=request.exaggeration if request.exaggeration is not None else 0.5,
        )

        samples = wav.squeeze().numpy() if hasattr(wav, 'numpy') else np.array(wav).squeeze()
        return pcm_to_ogg(samples, self._model.sr)
