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
# server/routes/synthesize_v2.py
#
# POST /api/v1/synthesize/v2          — async synthesis, returns progress_key immediately
# GET  /api/v1/synthesize/v2/{key}/progress — SSE stream of progress events
# GET  /api/v1/synthesize/v2/{key}/result   — OGG bytes when complete, 202 if pending
#
# v1 endpoint (/api/v1/synthesize) is unchanged — simple synchronous POST returning OGG.
# v2 is for clients that want progress feedback during synthesis.
#
# Flow:
#   1. Client POSTs to /v2 with same body as /v1
#   2. Server registers job, starts synthesis as background task
#   3. Server returns 202 {"progress_key": "...", "cache_key": "..."}
#   4. Client opens SSE to /v2/{key}/progress
#   5. Server pushes status events as synthesis progresses
#   6. Client GETs /v2/{key}/result when SSE reports "complete"
#   7. Job expires after JOB_TTL_SEC whether fetched or not

from __future__ import annotations

import asyncio
import json
import logging
import time
import uuid
from dataclasses import dataclass, field
from typing import Optional

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import Response, StreamingResponse

from .synthesize import SynthesizeRequest as _SynthesizeRequestBase, _validate_voice_type
from pydantic import BaseModel
from typing import Optional as _Opt

class SynthesizeRequest(_SynthesizeRequestBase):
    """v2 extends the base request with batch tracking fields."""
    batch_id:    _Opt[str] = None   # client-generated UUID grouping related segments
    batch_total: _Opt[int] = None   # total segments in this batch (required if batch_id set)
from ..backends.base import SynthesisRequest
from ..text_normalize import normalize as normalize_text
from ..cache import compute_cache_key, blend_voice_identity
from ..utils import compute_file_hash
from ..samples import (resolve_sample_path, resolve_sample, _base_stem,
                        resolve_sample_path_for_provider, resolve_sample_for_provider)

log = logging.getLogger(__name__)
router = APIRouter()

# Job TTL — jobs are removed this many seconds after completion (or failure)
JOB_TTL_SEC = 300.0


# ── Job state ─────────────────────────────────────────────────────────────────

@dataclass
class JobState:
    progress_key: str
    cache_key:    str
    provider_id:  str          = ""
    batch_id:     str          = ""
    status:       str          = "queued"       # queued|preprocessing|generating|complete|error
    chunk:        int          = 0
    total:        int          = 0
    error:        str          = ""
    duration_sec: float        = 0.0
    synth_time:   float        = 0.0
    realtime_factor: float     = 0.0
    input_chars:  int          = 0
    input_words:  int          = 0
    cache_hit:    bool         = False
    result:       Optional[bytes] = None        # OGG bytes when complete
    created_at:   float        = field(default_factory=time.monotonic)
    completed_at: float        = 0.0
    _waiters:     list         = field(default_factory=list)  # SSE subscriber queues

    def push_event(self, event: dict) -> None:
        """Push an event to all SSE subscribers."""
        for q in self._waiters:
            try:
                q.put_nowait(event)
            except asyncio.QueueFull:
                pass

    def is_expired(self) -> bool:
        if self.completed_at == 0.0:
            return False
        return time.monotonic() - self.completed_at > JOB_TTL_SEC


# ── In-memory stores ─────────────────────────────────────────────────────────

_jobs: dict[str, JobState] = {}
_jobs_lock = asyncio.Lock()


@dataclass
class BatchState:
    batch_id:    str
    total:       int
    completed:   int           = 0
    failed:      int           = 0
    job_keys:    list          = field(default_factory=list)   # progress_keys
    created_at:  float         = field(default_factory=time.monotonic)
    completed_at: float        = 0.0
    _waiters:    list          = field(default_factory=list)

    def push_event(self, event: dict) -> None:
        for q in self._waiters:
            try:
                q.put_nowait(event)
            except asyncio.QueueFull:
                pass

    def is_done(self) -> bool:
        return self.completed + self.failed >= self.total

    def is_expired(self) -> bool:
        if self.completed_at == 0.0:
            return False
        return time.monotonic() - self.completed_at > JOB_TTL_SEC


_batches: dict[str, BatchState] = {}


async def _get_job(progress_key: str) -> JobState:
    async with _jobs_lock:
        job = _jobs.get(progress_key)
        if job is None:
            # Fallback: client may have passed cache_key instead of progress_key
            job = next((j for j in _jobs.values() if j.cache_key == progress_key), None)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job '{progress_key}' not found or expired")
    return job


async def _cleanup_expired() -> None:
    """Remove expired jobs and batches. Called periodically."""
    async with _jobs_lock:
        expired = [k for k, j in _jobs.items() if j.is_expired()]
        for k in expired:
            del _jobs[k]
            log.debug("synthesize_v2: expired job %s", k)
        expired_b = [k for k, b in _batches.items() if b.is_expired()]
        for k in expired_b:
            del _batches[k]
            log.debug("synthesize_v2: expired batch %s", k)


# ── POST /api/v1/synthesize/v2 ────────────────────────────────────────────────

@router.post("/api/v1/synthesize/v2", status_code=202)
async def synthesize_v2(body: SynthesizeRequest, request: Request) -> dict:
    """
    Start an async synthesis job. Returns immediately with a progress_key.
    Poll /progress for SSE events, fetch /result when complete.
    """
    registry = request.app.state.registry
    cache    = request.app.state.cache
    settings = request.app.state.settings

    # Cleanup expired jobs opportunistically
    asyncio.create_task(_cleanup_expired())

    # 1. Resolve backend
    backend = registry.get(body.provider_id)
    if backend is None:
        raise HTTPException(
            status_code=404,
            detail=f"Provider '{body.provider_id}' not loaded. "
                   f"Loaded: {registry.provider_ids()}"
        )

    _validate_voice_type(body.voice, backend)

    # 2. Resolve sample
    sample_path = None
    sample_file_hash = ""
    ref_text = ""

    if body.voice.type == "reference":
        sample_id = body.voice.sample_id
        if not sample_id:
            raise HTTPException(status_code=400, detail="voice.sample_id required for type='reference'")
        # Use provider-specific clip (e.g. M_WWZ_10-f5.wav) when available.
        # Falls back to master sample if no provider clip exists.
        sample_path = resolve_sample_path_for_provider(
            settings.samples_dir, sample_id, body.provider_id)
        if sample_path is None:
            raise HTTPException(status_code=404, detail=f"Sample '{sample_id}' not found")
        sample_file_hash = compute_file_hash(sample_path)
        # Use provider-aware ref_text (from -f5.ref.txt sidecar when available)
        sample_info = resolve_sample_for_provider(
            settings.samples_dir, sample_id, body.provider_id)
        ref_text = sample_info.ref_text if sample_info else ""

    # 3. Compute cache key
    if body.voice.type == "base":
        voice_identity = body.voice.voice_id or ""
    elif body.voice.type == "reference":
        voice_identity = sample_file_hash
    elif body.voice.type == "description":
        import hashlib
        voice_identity = hashlib.sha256(
            (body.voice.voice_description or "").encode("utf-8")
        ).hexdigest()[:16]
    else:
        # For sample blends, resolve each sample to get a stable content-hash identity
        _blend_entries = []
        for e in body.voice.blend:
            if e.sample_id:
                _sp = resolve_sample_path_for_provider(
                    settings.samples_dir, e.sample_id, body.provider_id)
                _hash = compute_file_hash(_sp) if _sp else e.sample_id
                _blend_entries.append({"voice_id": _hash, "weight": e.weight})
            else:
                _blend_entries.append({"voice_id": e.voice_id, "weight": e.weight})
        voice_identity = blend_voice_identity(_blend_entries)

    # Resolve synthesis seed: client value → server default → None (random)
    resolved_seed = body.synthesis_seed
    if resolved_seed is None:
        resolved_seed = settings.default_synthesis_seed

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

    # Normalize text for TTS (WoW-specific + wetext English TN)
    normalized_text = normalize_text(body.text)

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

    # 4. Check cache — if hit, job completes immediately
    progress_key = str(uuid.uuid4()).replace("-", "")
    job = JobState(progress_key=progress_key, cache_key=cache_key, provider_id=body.provider_id, batch_id=body.batch_id or "")

    # Stamp input metrics onto the job now — available at result-fetch time
    # regardless of cache hit/miss and without the caller needing to re-send them.
    job.input_chars = len(normalized_text)
    job.input_words = len(normalized_text.split())

    async with _jobs_lock:
        _jobs[progress_key] = job

    # Register with batch if batch_id provided
    if body.batch_id and body.batch_total:
        async with _jobs_lock:
            if body.batch_id not in _batches:
                _batches[body.batch_id] = BatchState(
                    batch_id=body.batch_id,
                    total=body.batch_total,
                )
            else:
                # Update total if client sends a higher count as more segments arrive
                batch = _batches[body.batch_id]
                if body.batch_total > batch.total:
                    batch.total = body.batch_total
            _batches[body.batch_id].job_keys.append(progress_key)

    cached = await cache.get(cache_key)
    if cached is not None:
        job.status       = "complete"
        job.result       = cached
        job.cache_hit    = True
        job.completed_at = time.monotonic()
        log.debug("synthesize_v2: cache hit for key=%s", cache_key)
        # cached=true signals the client to skip SSE and fetch result directly
        return {
            "progress_key": progress_key,
            "cache_key":    cache_key,
            "cached":       True,
            "input_chars":  job.input_chars,
            "input_words":  job.input_words,
        }

    # 5. Build synthesis request
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
                "voice_id":   e.voice_id,
                "sample_id":  e.sample_id,
                "sample_path": str(resolve_sample_path_for_provider(
                    settings.samples_dir, e.sample_id, body.provider_id))
                    if e.sample_id else None,
                "weight":     e.weight,
            }
            for e in body.voice.blend
        ],
        cfg_weight=body.cfg_weight,
        exaggeration=body.exaggeration,
        cfg_strength=body.cfg_strength,
        nfe_step=body.nfe_step,
        cross_fade_duration=body.cross_fade_duration,
        sway_sampling_coef=body.sway_sampling_coef,
        voice_instruct=body.voice_instruct,
        voice_description=body.voice.voice_description,
        lux_num_steps=body.lux_num_steps,
        lux_t_shift=body.lux_t_shift,
        lux_return_smooth=body.lux_return_smooth,
        cosy_instruct=body.cosy_instruct,
        longcat_steps=body.longcat_steps,
        longcat_cfg_strength=body.longcat_cfg_strength,
        longcat_guidance=body.longcat_guidance,
        cb_temperature=body.cb_temperature,
        cb_top_p=body.cb_top_p,
        cb_repetition_penalty=body.cb_repetition_penalty,
        synthesis_seed=resolved_seed,
        cache_key=cache_key,
        cache_dir=str(settings.cache_dir),
    )

    # 6. Start background synthesis task
    asyncio.create_task(_run_synthesis(job, backend, synth_request, cache, cache_key))

    return {
        "progress_key": progress_key,
        "cache_key":    cache_key,
        "input_chars":  job.input_chars,
        "input_words":  job.input_words,
    }


# ── Batch submit endpoint ──────────────────────────────────────────────────────

class BatchSegmentRequest(_SynthesizeRequestBase):
    """One segment in a batch submit. Identical fields to SynthesizeRequest."""
    segment_id: str = ""   # client-assigned ID echoed back in response


class BatchSubmitRequest(BaseModel):
    segments: list[BatchSegmentRequest]


async def _process_one_segment(
    seg: BatchSegmentRequest,
    batch_id: str,
    registry,
    cache,
    settings,
) -> dict:
    """
    Process a single segment from a batch submit.
    Mirrors synthesize_v2() logic: resolve backend, compute cache key,
    check cache, dispatch synthesis job.
    Returns the per-segment response dict.
    """
    backend = registry.get(seg.provider_id)
    if backend is None:
        return {
            "segment_id":   seg.segment_id,
            "progress_key": None,
            "cache_key":    None,
            "cached":       False,
            "error":        f"Provider '{seg.provider_id}' not loaded",
        }

    try:
        _validate_voice_type(seg.voice, backend)
    except HTTPException as e:
        return {"segment_id": seg.segment_id, "error": e.detail,
                "progress_key": None, "cache_key": None, "cached": False}

    # Resolve sample
    sample_path = None
    sample_file_hash = ""
    ref_text = ""
    if seg.voice.type == "reference":
        sample_id = seg.voice.sample_id
        if not sample_id:
            return {"segment_id": seg.segment_id, "error": "voice.sample_id required",
                    "progress_key": None, "cache_key": None, "cached": False}
        sample_path = resolve_sample_path_for_provider(
            settings.samples_dir, sample_id, seg.provider_id)
        if sample_path is None:
            return {"segment_id": seg.segment_id, "error": f"Sample '{sample_id}' not found",
                    "progress_key": None, "cache_key": None, "cached": False}
        sample_file_hash = compute_file_hash(sample_path)
        sample_info = resolve_sample_for_provider(
            settings.samples_dir, sample_id, seg.provider_id)
        ref_text = sample_info.ref_text if sample_info else ""

    # Voice identity
    if seg.voice.type == "base":
        voice_identity = seg.voice.voice_id or ""
    elif seg.voice.type == "reference":
        voice_identity = sample_file_hash
    elif seg.voice.type == "description":
        import hashlib as _hl
        voice_identity = _hl.sha256(
            (seg.voice.voice_description or "").encode()).hexdigest()[:16]
    else:
        _blend_entries = []
        for e in seg.voice.blend:
            if e.sample_id:
                _sp = resolve_sample_path_for_provider(
                    settings.samples_dir, e.sample_id, seg.provider_id)
                _hash = compute_file_hash(_sp) if _sp else e.sample_id
                _blend_entries.append({"voice_id": _hash, "weight": e.weight})
            else:
                _blend_entries.append({"voice_id": e.voice_id, "weight": e.weight})
        voice_identity = blend_voice_identity(_blend_entries)

    # Seed resolution
    resolved_seed = seg.synthesis_seed
    if resolved_seed is None:
        resolved_seed = settings.default_synthesis_seed

    # Effective voice context (for cache key discrimination + prior token gating)
    effective_voice_context = seg.voice_context or ""
    if seg.voice_instruct:
        effective_voice_context = f"{effective_voice_context}|instruct:{seg.voice_instruct}"
    if seg.cb_temperature is not None:
        effective_voice_context = f"{effective_voice_context}|cb_temp:{seg.cb_temperature:.2f}"
    if seg.cb_top_p is not None:
        effective_voice_context = f"{effective_voice_context}|cb_top_p:{seg.cb_top_p:.2f}"
    if seg.cb_repetition_penalty is not None:
        effective_voice_context = f"{effective_voice_context}|cb_rep:{seg.cb_repetition_penalty:.2f}"
    if resolved_seed is not None:
        effective_voice_context = f"{effective_voice_context}|seed:{resolved_seed}"

    normalized_text = normalize_text(seg.text)

    cache_key = compute_cache_key(
        text=normalized_text,
        provider_id=seg.provider_id,
        voice_identity=voice_identity,
        lang_code=seg.lang_code,
        speech_rate=seg.speech_rate,
        cfg_weight=seg.cfg_weight,
        exaggeration=seg.exaggeration,
        cfg_strength=seg.cfg_strength,
        nfe_step=seg.nfe_step,
        cross_fade_duration=seg.cross_fade_duration,
        sway_sampling_coef=seg.sway_sampling_coef,
        voice_context=effective_voice_context,
    )

    progress_key = str(uuid.uuid4()).replace("-", "")
    job = JobState(
        progress_key=progress_key,
        cache_key=cache_key,
        provider_id=seg.provider_id,
        batch_id=batch_id,
    )
    job.input_chars = len(normalized_text)
    job.input_words = len(normalized_text.split())

    async with _jobs_lock:
        _jobs[progress_key] = job

    # Cache hit — complete immediately
    cached_ogg = await cache.get(cache_key)
    if cached_ogg is not None:
        job.status       = "complete"
        job.result       = cached_ogg
        job.cache_hit    = True
        job.completed_at = time.monotonic()
        return {
            "segment_id":   seg.segment_id,
            "progress_key": progress_key,
            "cache_key":    cache_key,
            "cached":       True,
            "input_chars":  job.input_chars,
            "input_words":  job.input_words,
        }

    # Cache miss — build SynthesisRequest and dispatch
    synth_request = SynthesisRequest(
        text=normalized_text,
        lang_code=seg.lang_code,
        speech_rate=seg.speech_rate,
        voice_id=seg.voice.voice_id,
        sample_path=sample_path,
        sample_id=seg.voice.sample_id if seg.voice.type == "reference" else None,
        samples_dir=settings.samples_dir,
        ref_text=ref_text,
        blend=[
            {
                "voice_id":    e.voice_id,
                "sample_id":   e.sample_id,
                "sample_path": str(resolve_sample_path_for_provider(
                    settings.samples_dir, e.sample_id, seg.provider_id))
                    if e.sample_id else None,
                "weight":      e.weight,
            }
            for e in seg.voice.blend
        ],
        cfg_weight=seg.cfg_weight,
        exaggeration=seg.exaggeration,
        cfg_strength=seg.cfg_strength,
        nfe_step=seg.nfe_step,
        cross_fade_duration=seg.cross_fade_duration,
        sway_sampling_coef=seg.sway_sampling_coef,
        voice_instruct=seg.voice_instruct,
        voice_description=seg.voice.voice_description,
        voice_context=seg.voice_context,
        lux_num_steps=seg.lux_num_steps,
        lux_t_shift=seg.lux_t_shift,
        lux_return_smooth=seg.lux_return_smooth,
        cosy_instruct=seg.cosy_instruct,
        longcat_steps=seg.longcat_steps,
        longcat_cfg_strength=seg.longcat_cfg_strength,
        longcat_guidance=seg.longcat_guidance,
        cb_temperature=seg.cb_temperature,
        cb_top_p=seg.cb_top_p,
        cb_repetition_penalty=seg.cb_repetition_penalty,
        synthesis_seed=resolved_seed,
        cache_key=cache_key,
        cache_dir=str(settings.cache_dir),
    )

    asyncio.create_task(_run_synthesis(job, backend, synth_request, cache, cache_key))

    return {
        "segment_id":   seg.segment_id,
        "progress_key": progress_key,
        "cache_key":    cache_key,
        "cached":       False,
        "input_chars":  job.input_chars,
        "input_words":  job.input_words,
    }


@router.post("/api/v1/synthesize/v2/batch", status_code=202)
async def synthesize_v2_batch(body: BatchSubmitRequest, request: Request) -> dict:
    """
    Submit multiple synthesis segments in a single request.

    All segments are checked against the OGG cache immediately.
    Cache hits complete instantly. Cache misses are dispatched as background jobs.

    Returns a batch_id and per-segment progress_key / cache_key / cached status.
    The client polls GET /api/v1/synthesize/v2/{progress_key}/progress per pending
    segment and fetches audio via GET /api/v1/synthesize/v2/{progress_key}/result.
    The overall batch progress is available at
    GET /api/v1/synthesize/v2/batch/{batch_id}/progress.
    """
    if not body.segments:
        raise HTTPException(status_code=400, detail="segments array must not be empty")

    registry = request.app.state.registry
    cache    = request.app.state.cache
    settings = request.app.state.settings

    asyncio.create_task(_cleanup_expired())

    batch_id = str(uuid.uuid4()).replace("-", "")
    total    = len(body.segments)

    # Register batch up front so progress polling works immediately
    async with _jobs_lock:
        _batches[batch_id] = BatchState(batch_id=batch_id, total=total)

    # Process all segments — cache checks run concurrently, synthesis dispatched async
    results = []
    for seg in body.segments:
        result = await _process_one_segment(seg, batch_id, registry, cache, settings)
        results.append(result)
        # Register each job's progress_key with the batch
        if result.get("progress_key"):
            async with _jobs_lock:
                _batches[batch_id].job_keys.append(result["progress_key"])
                # If this segment was a cache hit, update batch counters now
                job = _jobs.get(result["progress_key"])
                if job and job.cache_hit:
                    _batches[batch_id].completed += 1

    # If all segments were cache hits, mark batch complete
    async with _jobs_lock:
        batch = _batches.get(batch_id)
        if batch and batch.is_done():
            batch.completed_at = time.monotonic()

    return {
        "batch_id":  batch_id,
        "total":     total,
        "segments":  results,
    }


async def _run_synthesis(
    job: JobState,
    backend,
    synth_request: SynthesisRequest,
    cache,
    cache_key: str,
) -> None:
    """Background task — runs synthesis and updates job state."""
    import time as _time

    job.status = "preprocessing"
    job.push_event({"status": "preprocessing"})

    t0 = _time.monotonic()
    try:
        # Inject progress callback into synth_request so the backend
        # can report per-chunk progress.
        def on_progress(chunk: int, total: int) -> None:
            job.status = "generating"
            job.chunk  = chunk
            job.total  = total
            job.push_event({"status": "generating", "chunk": chunk, "total": total})

        synth_request.progress_callback = on_progress

        result = await backend.synthesize(synth_request)

        # Store in cache
        key_lock = cache.key_lock(cache_key)
        async with key_lock:
            await cache.store(cache_key, job.provider_id, result.ogg_bytes, result.duration_sec)

        elapsed = _time.monotonic() - t0
        realtime_factor = (result.duration_sec / elapsed) if elapsed > 0 else 0.0
        job.status          = "complete"
        job.result          = result.ogg_bytes
        job.duration_sec    = result.duration_sec
        job.synth_time      = elapsed
        job.realtime_factor = realtime_factor
        job.completed_at    = _time.monotonic()
        job.push_event({
            "status":           "complete",
            "duration_sec":     round(result.duration_sec, 3),
            "synth_time":       round(elapsed, 3),
            "realtime_factor":  round(realtime_factor, 3),
            "input_chars":      job.input_chars,
            "input_words":      job.input_words,
            "cache_hit":        False,
        })
        log.info(
            "synthesize_v2: complete key=%s duration=%.2fs synth_time=%.2fs rtf=%.2fx chars=%d words=%d",
            cache_key, result.duration_sec, elapsed, realtime_factor,
            job.input_chars, job.input_words,
        )
        _update_batch(job.batch_id, success=True)

    except Exception as e:
        job.status       = "error"
        job.error        = str(e)
        job.completed_at = _time.monotonic()
        job.push_event({"status": "error", "message": str(e)})
        log.exception("synthesize_v2: synthesis failed key=%s", cache_key)
        _update_batch(job.batch_id, success=False)

    finally:
        # Signal all SSE subscribers to close
        for q in job._waiters:
            try:
                q.put_nowait(None)  # None = sentinel, close stream
            except asyncio.QueueFull:
                pass


# ── Batch update helper ──────────────────────────────────────────────────────

def _update_batch(batch_id: str, success: bool) -> None:
    """Update batch progress when a job completes or fails."""
    if not batch_id:
        return
    batch = _batches.get(batch_id)
    if batch is None:
        return
    if success:
        batch.completed += 1
    else:
        batch.failed += 1

    event = {
        "status":    "complete" if batch.is_done() else "progress",
        "completed": batch.completed,
        "failed":    batch.failed,
        "total":     batch.total,
    }
    batch.push_event(event)

    if batch.is_done():
        batch.completed_at = time.monotonic()
        # Signal SSE subscribers to close
        for q in batch._waiters:
            try:
                q.put_nowait(None)
            except asyncio.QueueFull:
                pass
        log.info(
            "synthesize_v2: batch %s complete — %d/%d succeeded, %d failed",
            batch_id, batch.completed, batch.total, batch.failed
        )


# ── GET /api/v1/synthesize/v2/batch/{batch_id}/progress (SSE) ────────────────

@router.get("/api/v1/synthesize/v2/batch/{batch_id}/progress")
async def batch_progress(batch_id: str, request: Request) -> StreamingResponse:
    """
    SSE stream of batch-level progress.
    Emits an event each time any job in the batch completes.
    Event shape: {"status": "progress"|"complete", "completed": N, "failed": N, "total": N}
    Closes when all jobs in the batch have finished.
    """
    batch = _batches.get(batch_id)
    if batch is None:
        raise HTTPException(status_code=404, detail=f"Batch '{batch_id}' not found")

    async def event_stream():
        # If already done emit final state and close
        if batch.is_done():
            yield _sse({
                "status":    "complete",
                "completed": batch.completed,
                "failed":    batch.failed,
                "total":     batch.total,
            })
            return

        # Emit current state immediately
        yield _sse({
            "status":    "progress",
            "completed": batch.completed,
            "failed":    batch.failed,
            "total":     batch.total,
        })

        q: asyncio.Queue = asyncio.Queue(maxsize=64)
        batch._waiters.append(q)
        try:
            while True:
                if await request.is_disconnected():
                    break
                try:
                    event = await asyncio.wait_for(q.get(), timeout=1.0)
                except asyncio.TimeoutError:
                    yield ": ping\n\n"
                    continue

                if event is None:
                    break

                yield _sse(event)

                if event.get("status") == "complete":
                    break
        finally:
            try:
                batch._waiters.remove(q)
            except ValueError:
                pass

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"},
    )


# ── GET /api/v1/synthesize/v2/{key}/progress (SSE) ───────────────────────────

@router.get("/api/v1/synthesize/v2/{progress_key}/progress")
async def synthesize_v2_progress(progress_key: str, request: Request) -> StreamingResponse:
    """
    SSE stream of progress events for a synthesis job.
    Emits JSON events until synthesis completes or client disconnects.
    """
    job = await _get_job(progress_key)

    async def event_stream():
        # If already complete, emit final state immediately and close
        if job.status in ("complete", "error"):
            if job.status == "complete":
                yield _sse({"status": "complete", "duration_sec": job.duration_sec,
                             "cache_hit": job.cache_hit})
            else:
                yield _sse({"status": "error", "message": job.error})
            return

        # Subscribe to future events
        q: asyncio.Queue = asyncio.Queue(maxsize=32)
        job._waiters.append(q)
        try:
            # Emit current state immediately
            yield _sse({"status": job.status, "chunk": job.chunk, "total": job.total})

            while True:
                # Check client disconnect
                if await request.is_disconnected():
                    break

                try:
                    event = await asyncio.wait_for(q.get(), timeout=1.0)
                except asyncio.TimeoutError:
                    # Keepalive ping
                    yield ": ping\n\n"
                    continue

                if event is None:
                    # Synthesis complete — final event already pushed by _run_synthesis
                    break

                yield _sse(event)

                if event.get("status") in ("complete", "error"):
                    break

        finally:
            try:
                job._waiters.remove(q)
            except ValueError:
                pass

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",   # disable nginx buffering
        },
    )


def _sse(data: dict) -> str:
    return f"data: {json.dumps(data)}\n\n"


# ── GET /api/v1/synthesize/v2/{key}/result ────────────────────────────────────

@router.get("/api/v1/synthesize/v2/{progress_key}/result")
async def synthesize_v2_result(progress_key: str) -> Response:
    """
    Fetch the OGG result for a completed synthesis job.
    Returns 202 if still in progress, 200 with OGG bytes if complete.
    """
    job = await _get_job(progress_key)

    if job.status == "error":
        raise HTTPException(status_code=500, detail=job.error)

    if job.status != "complete" or job.result is None:
        return Response(
            content=json.dumps({"status": job.status, "chunk": job.chunk, "total": job.total}),
            status_code=202,
            media_type="application/json",
        )

    return Response(
        content=job.result,
        media_type="audio/ogg",
        headers={
            "X-Cache":           "HIT" if job.cache_hit else "MISS",
            "X-Cache-Key":       job.cache_key,
            "X-Input-Chars":     str(job.input_chars),
            "X-Input-Words":     str(job.input_words),
            "X-Duration":        f"{job.duration_sec:.3f}",
            "X-Synth-Time":      f"{job.synth_time:.3f}"      if not job.cache_hit else "",
            "X-Realtime-Factor": f"{job.realtime_factor:.3f}" if not job.cache_hit else "",
        },
    )
