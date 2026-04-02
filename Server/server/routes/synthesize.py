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
from ..cache import compute_cache_key, compute_file_hash, blend_voice_identity
from ..samples import (resolve_sample_path, resolve_sample,
                        resolve_sample_path_for_provider, resolve_sample_for_provider)

log = logging.getLogger(__name__)
router = APIRouter()


# ── Request / Response models ─────────────────────────────────────────────────

class BlendEntry(BaseModel):
    voice_id: str
    weight:   float = Field(gt=0.0, le=1.0)


class VoiceSpec(BaseModel):
    type:      str                    # "base" | "reference" | "blend"
    voice_id:  Optional[str] = None   # for type="base"
    sample_id: Optional[str] = None   # for type="reference"
    blend:     list[BlendEntry] = []  # for type="blend"

    @field_validator("type")
    @classmethod
    def validate_type(cls, v: str) -> str:
        valid = {"base", "reference", "blend"}
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
    else:  # blend
        voice_identity = blend_voice_identity(
            [{"voice_id": e.voice_id, "weight": e.weight} for e in body.voice.blend]
        )

    # 5. Compute cache key
    cache_key = compute_cache_key(
        text=body.text,
        provider_id=body.provider_id,
        model_version=backend.model_version,
        voice_identity=voice_identity,
        lang_code=body.lang_code,
        speech_rate=body.speech_rate,
        cfg_weight=body.cfg_weight,
        exaggeration=body.exaggeration,
        cfg_strength=body.cfg_strength,
        nfe_step=body.nfe_step,
        cross_fade_duration=body.cross_fade_duration,
        sway_sampling_coef=body.sway_sampling_coef,
        voice_context=body.voice_context,
    )

    # 6. Cache hit check (no lock needed for reads)
    cached = await cache.get(cache_key)
    if cached is not None:
        log.debug("Cache HIT: key=%s provider=%s", cache_key, body.provider_id)
        return Response(
            content=cached,
            media_type="audio/ogg",
            headers={"X-Cache": "HIT", "X-Cache-Key": cache_key},
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
                headers={"X-Cache": "HIT", "X-Cache-Key": cache_key},
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
            text=body.text,
            lang_code=body.lang_code,
            speech_rate=body.speech_rate,
            voice_id=body.voice.voice_id,
            sample_path=sample_path,
            sample_id=body.voice.sample_id if body.voice.type == "reference" else None,
            samples_dir=settings.samples_dir,
            ref_text=ref_text,
            blend=[{"voice_id": e.voice_id, "weight": e.weight}
                   for e in body.voice.blend],
            cfg_weight=body.cfg_weight,
            exaggeration=body.exaggeration,
            cfg_strength=body.cfg_strength,
            nfe_step=body.nfe_step,
            cross_fade_duration=body.cross_fade_duration,
            sway_sampling_coef=body.sway_sampling_coef,
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
        log.info(
            "Synthesis complete: provider=%s key=%s duration=%.2fs "
            "synth_time=%.2fs size=%d bytes",
            body.provider_id, cache_key, result.duration_sec,
            elapsed, len(result.ogg_bytes),
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
                "X-Cache":         "MISS",
                "X-Cache-Key":     cache_key,
                "X-Synth-Time":    f"{elapsed:.3f}",
                "X-Duration":      f"{result.duration_sec:.3f}",
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
