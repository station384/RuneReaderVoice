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
# server/routes/synthesize.py
#
# POST /api/v1/synthesize
#
# Flow:
#   1. Validate request
#   2. Resolve voice identity (base voice_id / reference file hash / blend string)
#   3. Compute cache key
#   4. Cache hit → return OGG immediately
#   5. Acquire per-key lock (prevents stampede)
#   6. Re-check cache (another coroutine may have synthesized while we waited)
#   7. Synthesize → store → return OGG

from __future__ import annotations

import logging
import time
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import Response
from pydantic import BaseModel, Field, field_validator

from ..backends.base import SynthesisRequest
from ..text_normalize import normalize as normalize_text
from ..cache import compute_cache_key, blend_voice_identity
from ..utils import compute_file_hash
from ..samples import (resolve_sample_path, resolve_sample,
                        resolve_sample_path_for_provider, resolve_sample_for_provider)

log = logging.getLogger(__name__)
router = APIRouter()


# ── Request / Response models ─────────────────────────────────────────────────

class BlendEntry(BaseModel):
    voice_id:  Optional[str] = None   # for base-voice blends (Kokoro)
    sample_id: Optional[str] = None   # for reference-sample blends (Chatterbox)
    weight:    float = Field(gt=0.0, le=1.0)

    @field_validator("voice_id", "sample_id", mode="before")
    @classmethod
    def _at_least_one_id(cls, v, info):
        # Cross-field validation happens at model level; per-field just pass through
        return v

    def model_post_init(self, __context):
        if not self.voice_id and not self.sample_id:
            raise ValueError("BlendEntry requires either voice_id or sample_id")


class VoiceSpec(BaseModel):
    type:             str                    # "base" | "reference" | "blend" | "description"
    voice_id:         Optional[str] = None   # for type="base"
    sample_id:        Optional[str] = None   # for type="reference"
    blend:            list[BlendEntry] = []  # for type="blend"
    voice_description: Optional[str] = None  # for type="description" (qwen_design)

    @field_validator("type")
    @classmethod
    def validate_type(cls, v: str) -> str:
        valid = {"base", "reference", "blend", "description"}
        if v not in valid:
            raise ValueError(f"voice.type must be one of {sorted(valid)}, got: {v!r}")
        return v


class SynthesizeRequest(BaseModel):
    provider_id:  str
    text:         str        = Field(min_length=1, max_length=8000)
    voice:        VoiceSpec
    lang_code:    str        = "en"
    speech_rate:  float      = Field(default=1.0, ge=0.5, le=2.0)
    cfg_weight:   float | None = Field(default=None, ge=0.0, le=3.0)
    exaggeration: float | None = Field(default=None, ge=0.0, le=3.0)
    # F5-TTS specific controls
    cfg_strength:        float | None = Field(default=None, ge=0.5, le=3.0)
    nfe_step:            int   | None = Field(default=None, ge=8,   le=64)
    cross_fade_duration: float | None = Field(default=None, ge=0.0, le=1.0)
    sway_sampling_coef:  float | None = Field(default=None, ge=-1.0, le=1.0)
    voice_context:       str   | None = None   # slot identity (e.g. "NightElf/Female") for cache discrimination
    # Qwen-specific
    voice_instruct:      str   | None = None   # qwen_custom: style instruction e.g. "speak angrily"
    # LuxTTS controls
    lux_num_steps:       int   | None = Field(default=None, ge=4, le=32)
    lux_t_shift:         float | None = Field(default=None, ge=0.1, le=1.0)
    lux_return_smooth:   bool  | None = None
    # CosyVoice3 controls
    cosy_instruct:       str   | None = None
    # Chatterbox/T3 sampling controls
    cb_temperature:        float | None = Field(default=None, ge=0.1, le=2.0)
    cb_top_p:              float | None = Field(default=None, ge=0.01, le=1.0)
    cb_repetition_penalty: float | None = Field(default=None, ge=1.0, le=3.0)
    # LongCat-AudioDiT controls
    longcat_steps:        int   | None = Field(default=None, ge=4, le=64)
    longcat_cfg_strength: float | None = Field(default=None, ge=1.0, le=10.0)
    longcat_guidance:     str   | None = None
    # Reproducibility — None = non-deterministic, integer = fixed seed
    synthesis_seed:      int   | None = Field(default=None, ge=0)


# ── Endpoint ──────────────────────────────────────────────────────────────────

@router.post("/api/v1/synthesize")
async def synthesize(body: SynthesizeRequest, request: Request) -> Response:
    registry = request.app.state.registry
    cache    = request.app.state.cache
    settings = request.app.state.settings

    # 1. Resolve backend
    backend = registry.get(body.provider_id)
    if backend is None:
        raise HTTPException(
            status_code=404,
            detail=f"Provider '{body.provider_id}' is not loaded. "
                   f"Loaded: {registry.provider_ids()}",
        )

    # 2. Validate voice type against backend capabilities
    _validate_voice_type(body.voice, backend)

    # 3. Resolve reference sample path (if reference type)
    sample_path: Optional[Path] = None
    sample_file_hash = ""
    ref_text = ""

    if body.voice.type == "reference":
        sample_id = body.voice.sample_id
        if not sample_id:
            raise HTTPException(status_code=400,
                                detail="voice.sample_id is required for type='reference'")
        sample_path = resolve_sample_path_for_provider(settings.samples_dir, sample_id, body.provider_id)
        if sample_path is None:
            raise HTTPException(
                status_code=404,
                detail=f"Sample '{sample_id}' not found in samples directory. "
                       f"Check GET /api/v1/providers/{body.provider_id}/samples",
            )
        sample_file_hash = compute_file_hash(sample_path)
        log.info(
            "synthesize: resolved reference audio sample_id='%s' provider='%s' audio='%s'",
            sample_id, body.provider_id, sample_path
        )

        # ref_text is loaded from the .ref.txt sidecar — REQUIRED for F5-TTS.
        # The server never invokes Whisper or any auto-transcription at runtime.
        sample_info = resolve_sample_for_provider(settings.samples_dir, sample_id, body.provider_id)
        ref_text = sample_info.ref_text if sample_info else ""
        if sample_info is not None:
            log.info(
                "synthesize: resolved reference text sample_id='%s' provider='%s' ref_source='%s' chars=%d",
                sample_id, body.provider_id, sample_info.filename, len(ref_text)
            )
        if not ref_text:
            log.warning("No .ref.txt sidecar for sample '%s'", sample_id)

    # 4. Build voice identity string for cache key
    if body.voice.type == "base":
        voice_identity = body.voice.voice_id or ""
    elif body.voice.type == "reference":
        voice_identity = sample_file_hash
    elif body.voice.type == "description":
        # Voice identity is the description text itself — different descriptions = different cache entries
        import hashlib
        voice_identity = hashlib.sha256(
            (body.voice.voice_description or "").encode("utf-8")
        ).hexdigest()[:16]
    else:  # blend
        _blend_id_entries = []
        for e in body.voice.blend:
            if e.sample_id:
                _sp = resolve_sample_path_for_provider(
                    settings.samples_dir, e.sample_id, body.provider_id)
                _hash = compute_file_hash(_sp) if _sp else e.sample_id
                _blend_id_entries.append({"voice_id": _hash, "weight": e.weight})
            else:
                _blend_id_entries.append({"voice_id": e.voice_id, "weight": e.weight})
        voice_identity = blend_voice_identity(_blend_id_entries)

    # Resolve synthesis seed: client value → server default → None (random)
    resolved_seed = body.synthesis_seed
    if resolved_seed is None:
        resolved_seed = settings.default_synthesis_seed

    # 4b. Normalize text for TTS (WoW-specific + wetext English TN)
    normalized_text = normalize_text(body.text)
    if normalized_text != body.text:
        log.debug("text_normalize: %r -> %r", body.text[:80], normalized_text[:80])

    # 5. Compute cache key
    # voice_instruct affects output — include in cache key via voice_context
    effective_voice_context = body.voice_context or ""
    if body.voice_instruct:
        effective_voice_context = f"{effective_voice_context}|instruct:{body.voice_instruct}"
    if body.lux_num_steps is not None:
        effective_voice_context = f"{effective_voice_context}|lux_steps:{body.lux_num_steps}"
    if body.lux_t_shift is not None:
        effective_voice_context = f"{effective_voice_context}|lux_t:{body.lux_t_shift:.2f}"
    if body.lux_return_smooth is not None:
        effective_voice_context = f"{effective_voice_context}|lux_smooth:{int(body.lux_return_smooth)}"
    if body.cosy_instruct:
        effective_voice_context = f"{effective_voice_context}|cosy_instruct:{body.cosy_instruct}"
    if body.longcat_steps is not None:
        effective_voice_context = f"{effective_voice_context}|lc_steps:{body.longcat_steps}"
    if body.longcat_cfg_strength is not None:
        effective_voice_context = f"{effective_voice_context}|lc_cfg:{body.longcat_cfg_strength:.2f}"
    if body.longcat_guidance is not None:
        effective_voice_context = f"{effective_voice_context}|lc_guide:{body.longcat_guidance}"
    if body.cb_temperature is not None:
        effective_voice_context = f"{effective_voice_context}|cb_temp:{body.cb_temperature:.2f}"
    if body.cb_top_p is not None:
        effective_voice_context = f"{effective_voice_context}|cb_top_p:{body.cb_top_p:.2f}"
    if body.cb_repetition_penalty is not None:
        effective_voice_context = f"{effective_voice_context}|cb_rep:{body.cb_repetition_penalty:.2f}"
    if resolved_seed is not None:
        effective_voice_context = f"{effective_voice_context}|seed:{resolved_seed}" 

    cache_key = compute_cache_key(
        text=normalized_text,
        provider_id=body.provider_id,
        voice_identity=voice_identity,
        lang_code=body.lang_code,
        speech_rate=body.speech_rate,
        cfg_weight=body.cfg_weight,
        exaggeration=body.exaggeration,
        cfg_strength=body.cfg_strength,
        nfe_step=body.nfe_step,
        cross_fade_duration=body.cross_fade_duration,
        sway_sampling_coef=body.sway_sampling_coef,
        voice_context=effective_voice_context,
    )

    # ── Input metrics — computed once from the normalized text ────────────────
    # These are included in all responses (HIT and MISS) so the benchmark script
    # gets consistent headers regardless of cache state.
    input_chars = len(normalized_text)
    input_words = len(normalized_text.split())

    # 6. Cache hit check (no lock needed for reads)
    cached = await cache.get(cache_key)
    if cached is not None:
        log.debug("Cache HIT: key=%s provider=%s", cache_key, body.provider_id)
        return Response(
            content=cached,
            media_type="audio/ogg",
            headers={
                "X-Cache":       "HIT",
                "X-Cache-Key":   cache_key,
                "X-Input-Chars": str(input_chars),
                "X-Input-Words": str(input_words),
            },
        )

    # 7. Acquire per-key lock — only one coroutine synthesizes a given key at a time.
    # Others waiting on the lock will find a cache hit after the first completes.
    key_lock = cache.key_lock(cache_key)
    async with key_lock:
        # Re-check cache after acquiring lock (another caller may have just stored it)
        cached = await cache.get(cache_key)
        if cached is not None:
            log.debug("Cache HIT (post-lock): key=%s provider=%s",
                      cache_key, body.provider_id)
            return Response(
                content=cached,
                media_type="audio/ogg",
                headers={
                    "X-Cache":       "HIT",
                    "X-Cache-Key":   cache_key,
                    "X-Input-Chars": str(input_chars),
                    "X-Input-Words": str(input_words),
                },
            )

        # 8. Synthesize
        t0 = time.monotonic()
        log.info(
            "Synthesizing: provider=%s voice_type=%s lang=%s rate=%.2f "
            "text_len=%d key=%s",
            body.provider_id, body.voice.type, body.lang_code,
            body.speech_rate, len(body.text), cache_key,
        )

        synth_request = SynthesisRequest(
            text=normalized_text,
            lang_code=body.lang_code,
            speech_rate=body.speech_rate,
            voice_id=body.voice.voice_id,
            sample_path=sample_path,
            sample_id=body.voice.sample_id if body.voice.type == "reference" else None,
            samples_dir=settings.samples_dir,
            ref_text=ref_text,
            blend=[
                {
                    "voice_id":    e.voice_id,
                    "sample_id":   e.sample_id,
                    "sample_path": str(resolve_sample_path_for_provider(
                        settings.samples_dir, e.sample_id, body.provider_id))
                        if e.sample_id else None,
                    "weight":      e.weight,
                }
                   for e in body.voice.blend],
            cfg_weight=body.cfg_weight,
            exaggeration=body.exaggeration,
            cfg_strength=body.cfg_strength,
            nfe_step=body.nfe_step,
            cross_fade_duration=body.cross_fade_duration,
            sway_sampling_coef=body.sway_sampling_coef,
            voice_instruct=body.voice_instruct,
            lux_num_steps=body.lux_num_steps,
            lux_t_shift=body.lux_t_shift,
            lux_return_smooth=body.lux_return_smooth,
            cosy_instruct=body.cosy_instruct,
            voice_description=body.voice.voice_description,
            longcat_steps=body.longcat_steps,
            longcat_cfg_strength=body.longcat_cfg_strength,
            longcat_guidance=body.longcat_guidance,
            cb_temperature=body.cb_temperature,
            cb_top_p=body.cb_top_p,
            cb_repetition_penalty=body.cb_repetition_penalty,
            synthesis_seed=resolved_seed,
        )

        try:
            result = await backend.synthesize(synth_request)
        except ValueError as e:
            log.error("Synthesis ValueError: provider=%s key=%s error=%s",
                      body.provider_id, cache_key, e, exc_info=True)
            raise HTTPException(status_code=400, detail=str(e))
        except Exception as e:
            log.exception("Synthesis failed: provider=%s key=%s error=%s",
                          body.provider_id, cache_key, e)
            raise HTTPException(status_code=500,
                                detail=f"Synthesis failed: {e}")

        elapsed = time.monotonic() - t0
        realtime_factor = (result.duration_sec / elapsed) if elapsed > 0 else 0.0
        log.info(
            "Synthesis complete: provider=%s key=%s duration=%.2fs "
            "synth_time=%.2fs rtf=%.2fx size=%d bytes chars=%d words=%d",
            body.provider_id, cache_key, result.duration_sec,
            elapsed, realtime_factor, len(result.ogg_bytes),
            input_chars, input_words,
        )

        # 9. Store in cache
        await cache.store(
            key=cache_key,
            provider_id=body.provider_id,
            ogg_bytes=result.ogg_bytes,
            duration_sec=result.duration_sec,
        )

        return Response(
            content=result.ogg_bytes,
            media_type="audio/ogg",
            headers={
                "X-Cache":          "MISS",
                "X-Cache-Key":      cache_key,
                "X-Input-Chars":    str(input_chars),
                "X-Input-Words":    str(input_words),
                "X-Synth-Time":     f"{elapsed:.3f}",
                "X-Duration":       f"{result.duration_sec:.3f}",
                "X-Realtime-Factor": f"{realtime_factor:.3f}",
            },
        )


# ── Validation helpers ────────────────────────────────────────────────────────

def _validate_voice_type(voice: VoiceSpec, backend) -> None:
    if voice.type == "base" and not backend.supports_base_voices:
        raise HTTPException(
            status_code=400,
            detail=f"Provider '{backend.provider_id}' does not support base voices.",
        )
    if voice.type == "reference" and not backend.supports_voice_matching:
        raise HTTPException(
            status_code=400,
            detail=f"Provider '{backend.provider_id}' does not support voice matching.",
        )
    if voice.type == "description" and not backend.supports_voice_design:
        raise HTTPException(
            status_code=400,
            detail=f"Provider '{backend.provider_id}' does not support voice design.",
        )
    if voice.type == "description" and not voice.voice_description:
        raise HTTPException(
            status_code=400,
            detail="voice.voice_description is required for type='description'.",
        )
    if voice.type == "blend" and not backend.supports_voice_blending:
        raise HTTPException(
            status_code=400,
            detail=f"Provider '{backend.provider_id}' does not support voice blending.",
        )
    if voice.type == "blend" and len(voice.blend) < 2:
        raise HTTPException(
            status_code=400,
            detail="Blend requires at least 2 entries.",
        )
    if voice.type == "blend":
        total = sum(e.weight for e in voice.blend)
        if not (0.98 <= total <= 1.02):
            raise HTTPException(
                status_code=400,
                detail=f"Blend weights must sum to approximately 1.0, got {total:.3f}.",
            )
