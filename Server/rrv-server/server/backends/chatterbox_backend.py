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
# server/backends/chatterbox_backend.py
#
# Chatterbox Turbo backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Install (chatterbox-tts 0.1.7+, Python 3.11):
#   pip install chatterbox-tts
#   pip install --no-deps s3tokenizer
#   pip install onnx>=1.16.0
#
# Model files — place in data/models/chatterbox/:
#   Download from: https://huggingface.co/ResembleAI/chatterbox-turbo
#
# Supports:
#   - Zero-shot voice cloning from a reference audio clip
#   - Paralinguistic tags: [laugh], [chuckle], [cough], [sigh] etc.
#   - 350M parameters — 1-step diffusion, faster than full model
#   - CPU (slow) or CUDA/ROCm GPU
#
# Conditionals caching (Turbo-specific):
#   Turbo's exaggeration parameter is applied during prepare_conditionals(),
#   NOT during generate(). This means the cache key must include both the
#   sample file hash AND the exaggeration value — if exaggeration changes
#   between chunks, the conditionals must be re-prepared.
#
#   For consistent voice across a dialog batch, keep exaggeration constant
#   for all chunks of the same NPC. The cache ensures the identical voice
#   embedding is reused for every chunk without re-reading the audio file.

from __future__ import annotations

import asyncio
import logging
import os
import re
import tempfile
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..utils import compute_file_hash

log = logging.getLogger(__name__)

# ── Transparent sentence-level chunking ───────────────────────────────────────
# Chatterbox has a practical ceiling of ~400 chars / ~65 words before truncation
# and hallucination become likely (benchmark data, April 2026).
# For longer inputs the backend splits at sentence boundaries, synthesizes each
# chunk independently against the same reference sample, and concatenates.
# Conditionals are cached after the first chunk so subsequent chunks are cheap.
# This is transparent to the client — full text in, single OGG out.

_CB_CHUNK_TARGET_CHARS = int(os.environ.get("RRV_CB_CHUNK_TARGET_CHARS", "380"))
_CB_CHUNK_HARD_CHARS   = int(os.environ.get("RRV_CB_CHUNK_HARD_CHARS",   "480"))

# Sentence-ending punctuation — split here preferentially
_SENT_END = re.compile(r'(?<=[.!?])\s+')
# Clause boundary — fallback if sentence split produces oversized chunks
_CLAUSE   = re.compile(r'(?<=[,;:])\s+')


def _split_into_chunks(text: str,
                       target: int = _CB_CHUNK_TARGET_CHARS,
                       hard: int   = _CB_CHUNK_HARD_CHARS) -> list[str]:
    """
    Split text into chunks at sentence boundaries, targeting target chars each.
    Falls back to clause boundaries when a sentence is itself oversized.
    Never splits mid-word. Returns a list of non-empty strings.

    target / hard are the soft and hard character limits per chunk.
    If a single sentence exceeds hard, it is split at the nearest clause
    boundary under hard, or at the hard limit as a last resort.
    """
    import re as _re

    # First pass: split at sentence endings
    sentences = [s.strip() for s in _SENT_END.split(text) if s.strip()]

    chunks: list[str] = []
    current = ""

    for sent in sentences:
        # If the sentence itself exceeds hard cap, split it further
        if len(sent) > hard:
            # Try clause boundaries first
            clauses = [c.strip() for c in _CLAUSE.split(sent) if c.strip()]
            for clause in clauses:
                if len(clause) > hard:
                    # Last resort: hard split at word boundary under hard cap
                    words = clause.split()
                    part = ""
                    for w in words:
                        candidate = (part + " " + w).strip()
                        if len(candidate) > hard and part:
                            if current:
                                chunks.append(current.strip())
                                current = ""
                            chunks.append(part.strip())
                            part = w
                        else:
                            part = candidate
                    if part:
                        candidate = (current + " " + part).strip() if current else part
                        if len(candidate) <= hard:
                            current = candidate
                        else:
                            if current:
                                chunks.append(current.strip())
                            current = part
                else:
                    candidate = (current + " " + clause).strip() if current else clause
                    if len(candidate) <= target:
                        current = candidate
                    else:
                        if current:
                            chunks.append(current.strip())
                        current = clause
        else:
            candidate = (current + " " + sent).strip() if current else sent
            if len(candidate) <= target:
                current = candidate
            else:
                if current:
                    chunks.append(current.strip())
                current = sent

    if current.strip():
        chunks.append(current.strip())

    return [c for c in chunks if c]



class ChatterboxBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, max_concurrent: int = 2) -> None:
        self._models_dir     = models_dir
        self._torch_device   = torch_device
        self._model          = None
        self._model_version  = ""
        self._MAX_CONCURRENT = max_concurrent
        # asyncio.Condition must be created inside a running event loop.
        # Initialized lazily in load() which runs inside asyncio.run().
        self._voice_cond: asyncio.Condition | None = None
        self._active_voice_key: str | None = None
        self._active_count   = 0

        # Conditionals cache.
        # Turbo bakes exaggeration into the conditionals during prepare_conditionals(),
        # so the cache key includes both sample hash and exaggeration value.
        # The temp WAV stays alive for the cache entry lifetime.
        self._cond_cache_key: str = ""     # "{sample_hash}|{exaggeration}"
        self._cond_tmp_wav: str   = ""     # path to current PCM_16 temp WAV

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or ""
        return f"{sample_key}|{lang_key}"

    async def _acquire_voice_slot(self, voice_key: str) -> None:
        if self._voice_cond is None:
            self._voice_cond = asyncio.Condition()
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
        return "chatterbox"

    @property
    def display_name(self) -> str:
        return "Chatterbox Turbo"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return True

    @property
    def supports_voice_blending(self) -> bool:
        return True

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
                "default":     0.0,
                "min":         0.0,
                "max":         3.0,
                "description": "Classifier-free guidance weight for prompt adherence.",
            },
            "exaggeration": {
                "type":        "float",
                "default":     0.0,
                "min":         0.0,
                "max":         3.0,
                "description": "Emotion/expressiveness control. Not supported by Turbo — ignored.",
            },
            "cb_temperature": {
                "type": "float", "default": 0.8, "min": 0.1, "max": 2.0,
                "description": "T3 token sampling temperature. Lower=stable/consistent, higher=expressive/variable.",
            },
            "cb_top_p": {
                "type": "float", "default": 0.95, "min": 0.01, "max": 1.0,
                "description": "Nucleus sampling cutoff. Lower=more conservative token selection.",
            },
            "cb_repetition_penalty": {
                "type": "float", "default": 1.2, "min": 1.0, "max": 3.0,
                "description": "Penalizes repeated tokens. Raise to 1.5-2.0 if model loops or hallucinates.",
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        # Create asyncio primitives here — inside the running event loop
        if self._voice_cond is None:
            self._voice_cond = asyncio.Condition()
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "Chatterbox Turbo loaded: model_version=%s device=%s",
            self._model_version, self._torch_device,
        )

    def _load_sync(self) -> None:
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _orig = librosa.load
            def _f32(path, *a, **kw):
                y, sr = _orig(path, *a, **kw)
                return y.astype(np.float32), sr
            librosa.load = _f32
            librosa._rrv_patched = True
            log.info("Chatterbox Turbo: patched librosa.load -> float32")

        try:
            from chatterbox.tts_turbo import ChatterboxTurboTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed. Run: pip install chatterbox-tts"
            )

        local_model_dir = self._models_dir / "chatterbox"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox Turbo: loading from %s", local_model_dir)
            self._model = ChatterboxTurboTTS.from_local(
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
                f"Chatterbox Turbo model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-turbo\n"
                f"Place all files in: {local_model_dir}"
            )

    # ── Patches ───────────────────────────────────────────────────────────────

    def _patch_mel_filters(self) -> None:
        """Force float32 through Chatterbox's pipeline. See chatterbox_full_backend.py for explanation."""
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
                log.debug("Chatterbox Turbo: patched S3Tokenizer.log_mel_spectrogram")
        except Exception as e:
            log.warning("Chatterbox Turbo: could not patch S3Tokenizer: %s", e)

        try:
            from chatterbox.models.voice_encoder.voice_encoder import VoiceEncoder

            if not getattr(VoiceEncoder, '_rrv_patched', False):
                _orig_embeds = VoiceEncoder.embeds_from_wavs

                def _patched_embeds(self_ve, wavs, *args, **kwargs):
                    wavs = [w.astype(np.float32) if hasattr(w, 'astype') else w for w in wavs]
                    return _orig_embeds(self_ve, wavs, *args, **kwargs)

                VoiceEncoder.embeds_from_wavs = _patched_embeds
                VoiceEncoder._rrv_patched = True
                log.debug("Chatterbox Turbo: patched VoiceEncoder.embeds_from_wavs -> float32")
        except Exception as e:
            log.warning("Chatterbox Turbo: could not patch VoiceEncoder: %s", e)

    def _patch_t3_hidden_states(self) -> None:
        """
        Patch T3HuggingfaceBackend.forward for transformers 4.57.x compatibility.
        See chatterbox_full_backend.py for full explanation.
        Uses a class-level sentinel so the patch is applied exactly once even
        when both Turbo and Full backends are loaded simultaneously.
        """
        try:
            from chatterbox.models.t3.inference.t3_hf_backend import T3HuggingfaceBackend
            from transformers.modeling_outputs import CausalLMOutputWithCrossAttentions

            if getattr(T3HuggingfaceBackend, '_rrv_hidden_states_patched', False):
                log.debug("Chatterbox Turbo: T3HuggingfaceBackend already patched, skipping")
                return

            def _patched_forward(self_t3, inputs_embeds, past_key_values=None,
                                 use_cache=True, output_attentions=False,
                                 output_hidden_states=True, return_dict=True):
                tfmr_out = self_t3.model(
                    inputs_embeds=inputs_embeds,
                    past_key_values=past_key_values,
                    use_cache=use_cache,
                    output_attentions=output_attentions,
                    output_hidden_states=output_hidden_states,
                    return_dict=True,
                )
                if tfmr_out.hidden_states is not None:
                    hidden_states = tfmr_out.hidden_states[-1]
                else:
                    hidden_states = tfmr_out.last_hidden_state
                    log.debug("Chatterbox Turbo T3: hidden_states was None, using last_hidden_state "
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
            log.info("Chatterbox Turbo: patched T3HuggingfaceBackend.forward for transformers 4.57.x compatibility")

        except Exception as e:
            log.warning("Chatterbox Turbo: could not patch T3HuggingfaceBackend.forward: %s", e)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox Turbo backend is not loaded")

        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        if not blend_entries and request.sample_path is None:
            raise ValueError(
                "Chatterbox Turbo requires a reference audio clip. "
                "Provide sample_id in the request, or use voice.type='blend'."
            )

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        finally:
            await self._release_voice_slot(voice_key)

        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _blend_conditionals(self, blend: list[dict]) -> None:
        """
        Blend voice conditionals from multiple reference samples.

        Two independent speaker embeddings must both be blended:

          conds.t3.speaker_emb   — 256-dim VE embedding. Projected by T3CondEnc.spkr_enc
                                   (nn.Linear) into T3's hidden dim. Conditions token generation.

          conds.gen["embedding"] — x-vector from S3Gen's CAMPPlus speaker_encoder.
                                   Conditions S3Gen vocoder — the actual waveform timbre.
                                   THIS is why blending only speaker_emb had no audible effect:
                                   the vocoder was still rendering in 100% primary's voice.

        Everything else (prompt_feat, prompt_token, cond_prompt_speech_tokens) comes
        from the primary sample untouched — blending discrete/spectrogram data causes mush.

        generate() bypass is required: its emotion_adv branch (float != tensor scalar)
        always evaluates truthy and recreates T3Cond from scratch, discarding our blend.
        """
        import torch, tempfile, os, types
        import soundfile as sf

        sample_entries = [e for e in blend if e.get("sample_path")]
        if not sample_entries:
            raise ValueError("_blend_conditionals: no sample_path entries in blend")

        total_w = sum(e["weight"] for e in sample_entries)
        entries = [(e["sample_path"], e["weight"] / total_w) for e in sample_entries]
        primary_path = max(entries, key=lambda x: x[1])[0]
        # Primary runs last — its full conds are in model.conds when loop ends
        entries_sorted = sorted(entries, key=lambda x: x[0] == primary_path)

        t3_speaker_embs = []   # (tensor, weight) — T3 VE embeddings
        gen_embeddings  = []   # (tensor, weight) — S3Gen x-vectors
        tmp_wavs = []

        try:
            for sample_path_str, weight in entries_sorted:
                audio_data, sr = sf.read(str(sample_path_str), dtype="float32")
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
                    tmp_path = tmp.name
                sf.write(tmp_path, audio_data, sr, subtype="PCM_16")
                tmp_wavs.append(tmp_path)

                self._model.prepare_conditionals(tmp_path)

                t3_speaker_embs.append((
                    self._model.conds.t3.speaker_emb.detach().clone(),
                    weight
                ))
                if "embedding" in self._model.conds.gen:
                    gen_embeddings.append((
                        self._model.conds.gen["embedding"].detach().clone(),
                        weight
                    ))

            # ── Blend T3 speaker_emb — weighted average, preserve magnitude ──────
            blended_t3_spk = sum(emb * w for emb, w in t3_speaker_embs)
            mean_mag = sum(emb.norm() * w for emb, w in t3_speaker_embs)
            b_norm = blended_t3_spk.norm()
            if b_norm > 1e-8:
                blended_t3_spk = blended_t3_spk / b_norm * mean_mag

            # ── Blend S3Gen x-vector — same approach ─────────────────────────────
            blended_gen_emb = None
            if gen_embeddings:
                blended_gen_emb = sum(emb * w for emb, w in gen_embeddings)
                mean_mag_gen = sum(emb.norm() * w for emb, w in gen_embeddings)
                g_norm = blended_gen_emb.norm()
                if g_norm > 1e-8:
                    blended_gen_emb = blended_gen_emb / g_norm * mean_mag_gen

            # ── Build blended T3Cond — primary tokens, blended speaker_emb ───────
            from chatterbox.models.t3.modules.cond_enc import T3Cond
            _primary_t3 = self._model.conds.t3
            blended_t3 = T3Cond(
                speaker_emb=blended_t3_spk,
                cond_prompt_speech_tokens=_primary_t3.cond_prompt_speech_tokens,
                emotion_adv=torch.zeros(1, 1, 1).to(dtype=blended_t3_spk.dtype, device=blended_t3_spk.device),
            ).to(device=self._torch_device)

            # ── Patch gen dict — replace embedding only, keep all other fields ───
            blended_gen = dict(self._model.conds.gen)  # shallow copy — primary's fields
            if blended_gen_emb is not None:
                blended_gen["embedding"] = blended_gen_emb

            self._model.conds.t3  = blended_t3
            self._model.conds.gen = blended_gen

            # ── Bypass generate() — emotion_adv branch always fires, discards blend
            _t3_ref  = blended_t3
            _gen_ref = blended_gen

            def _patched_generate(self_m, text, repetition_penalty=1.2, min_p=0.05,
                                   top_p=1.0, audio_prompt_path=None, exaggeration=0.5,
                                   cfg_weight=0.5, temperature=0.8):
                import torch as _torch
                import torch.nn.functional as F
                from chatterbox.models.s3tokenizer import drop_invalid_tokens
                self_m.conds.t3  = _t3_ref
                self_m.conds.gen = _gen_ref
                text_proc = self_m.tokenizer.text_to_tokens(text).to(self_m.device)
                if cfg_weight > 0.0:
                    text_proc = _torch.cat([text_proc, text_proc], dim=0)
                sot = self_m.t3.hp.start_text_token
                eot = self_m.t3.hp.stop_text_token
                text_proc = F.pad(text_proc, (1, 0), value=sot)
                text_proc = F.pad(text_proc, (0, 1), value=eot)
                with _torch.inference_mode():
                    speech_tokens = self_m.t3.inference(
                        t3_cond=self_m.conds.t3,
                        text_tokens=text_proc,
                        max_new_tokens=1000,
                        temperature=temperature,
                        cfg_weight=cfg_weight,
                        repetition_penalty=repetition_penalty,
                        min_p=min_p,
                        top_p=top_p,
                    )
                    speech_tokens = drop_invalid_tokens(speech_tokens[0])
                    speech_tokens = speech_tokens[speech_tokens < 6561].to(self_m.device)
                    wav, _ = self_m.s3gen.inference(
                        speech_tokens=speech_tokens,
                        ref_dict=self_m.conds.gen,
                    )
                    wav = wav.squeeze(0).detach().cpu().numpy()
                    watermarked = self_m.watermarker.apply_watermark(wav, sample_rate=self_m.sr)
                return _torch.from_numpy(watermarked).unsqueeze(0)

            import types as _types
            self._model._rrv_blend_generate = _types.MethodType(_patched_generate, self._model)
            self._is_blend_active = True

            log.debug(
                "Chatterbox blend: t3_speaker_emb + gen[embedding] blended (%d samples)",
                len(t3_speaker_embs)
            )

        finally:
            for tmp_path in tmp_wavs:
                try:
                    os.unlink(tmp_path)
                except Exception:
                    pass

        self._cond_cache_key = ""

    def _make_cond_cache_key(self, sample_hash: str, exaggeration: float) -> str:
        # Turbo does not accept exaggeration in prepare_conditionals() —
        # the cache key is just the sample hash. The exaggeration parameter
        # is kept in the signature for symmetry with the full model but unused.
        return sample_hash

    def _ensure_conditionals(self, sample_path: Path, sample_hash: str, exaggeration: float) -> None:
        """
        Prepare voice conditionals for the given sample if not already cached.

        Cache hit: same sample hash as last call — model's internal conds is
        already correct, skip prepare_conditionals entirely.

        Cache miss: new sample — write PCM_16 temp WAV, call prepare_conditionals(),
        update cache, delete old temp WAV.

        Note: Turbo does NOT accept exaggeration in prepare_conditionals() —
        that parameter only applies to the full and multilingual models.
        """
        cache_key = self._make_cond_cache_key(sample_hash, exaggeration)

        if cache_key == self._cond_cache_key:
            log.debug(
                "Chatterbox Turbo: conditionals cache HIT — reusing voice embedding hash=%s",
                sample_hash[:8],
            )
            return

        import soundfile as sf

        log.info(
            "Chatterbox Turbo: conditionals cache MISS — preparing new voice embedding "
            "hash=%s (was %s)",
            sample_hash[:8],
            self._cond_cache_key[:8] if self._cond_cache_key else "none",
        )

        info = sf.info(str(sample_path))
        if info.duration < 5.0:
            raise ValueError(
                f"Chatterbox Turbo requires a reference clip of at least 5 seconds. "
                f"'{sample_path.name}' is only {info.duration:.1f}s."
            )

        # Write PCM_16 WAV — guarantees librosa returns float32 on load
        audio_data, sr = sf.read(str(sample_path), dtype='float32')
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            new_tmp_path = tmp.name
        sf.write(new_tmp_path, audio_data, sr, subtype='PCM_16')

        # Turbo's prepare_conditionals does not accept exaggeration
        self._model.prepare_conditionals(new_tmp_path)

        # Evict previous temp WAV
        if self._cond_tmp_wav and os.path.exists(self._cond_tmp_wav):
            try:
                os.unlink(self._cond_tmp_wav)
            except Exception as e:
                log.warning("Chatterbox Turbo: failed to delete old temp WAV %s: %s",
                            self._cond_tmp_wav, e)

        self._cond_cache_key = cache_key
        self._cond_tmp_wav   = new_tmp_path

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        exaggeration        = request.exaggeration        if request.exaggeration        is not None else 0.0
        cfg_weight          = request.cfg_weight          if request.cfg_weight          is not None else 0.0
        temperature         = request.cb_temperature        if request.cb_temperature        is not None else 0.8
        top_p               = request.cb_top_p              if request.cb_top_p              is not None else 0.95
        repetition_penalty  = request.cb_repetition_penalty if request.cb_repetition_penalty is not None else 1.2

        # Route: blend vs single reference
        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        if blend_entries:
            log.debug("Chatterbox Turbo: blend mode — %d samples", len(blend_entries))
            self._blend_conditionals(blend_entries)
        else:
            if request.sample_path is None:
                raise ValueError("Chatterbox Turbo requires either a reference sample or a blend.")
            sample_hash = compute_file_hash(request.sample_path)
            self._ensure_conditionals(request.sample_path, sample_hash, exaggeration)

        # Set deterministic seed if requested
        if request.synthesis_seed is not None:
            import torch as _torch
            _torch.manual_seed(request.synthesis_seed)
            _torch.cuda.manual_seed_all(request.synthesis_seed)

        # Split into sentence-boundary chunks — transparent to the client.
        chunks = _split_into_chunks(request.text)
        total  = len(chunks)
        _progress_cb = request.progress_callback
        _generate = getattr(self._model, '_rrv_blend_generate', None) \
                    if getattr(self, '_is_blend_active', False) else None
        if _generate is None:
            _generate = self._model.generate

        all_samples: list[np.ndarray] = []

        try:
            for i, chunk_text in enumerate(chunks):
                if not chunk_text.strip():
                    continue
                # exaggeration applied during prepare_conditionals, not generate()
                wav = _generate(
                    text=chunk_text,
                    cfg_weight=cfg_weight,
                    temperature=temperature,
                    top_p=top_p,
                    repetition_penalty=repetition_penalty,
                )
                samples = wav.squeeze().numpy() if hasattr(wav, 'numpy') else np.array(wav).squeeze()
                all_samples.append(samples.astype(np.float32))
                log.debug("Chatterbox Turbo: chunk %d/%d synthesized (%d chars)",
                          i + 1, total, len(chunk_text))
                if _progress_cb is not None:
                    try:
                        _progress_cb(i + 1, total)
                    except Exception:
                        pass
        finally:
            if getattr(self, '_is_blend_active', False):
                self._is_blend_active = False
                if hasattr(self._model, '_rrv_blend_generate'):
                    del self._model._rrv_blend_generate

        if not all_samples:
            return pcm_to_ogg(np.zeros(self._model.sr, dtype=np.float32), self._model.sr)

        combined = np.concatenate(all_samples)
        return pcm_to_ogg(combined, self._model.sr)
