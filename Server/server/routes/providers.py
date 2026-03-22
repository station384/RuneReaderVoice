# SPDX-License-Identifier: GPL-3.0-or-later
# server/routes/providers.py

from __future__ import annotations

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

from ..samples import scan as scan_samples, scan_for_provider

router = APIRouter()


class VoiceResponse(BaseModel):
    voice_id:     str
    display_name: str
    language:     str
    gender:       str
    type:         str


class SampleResponse(BaseModel):
    sample_id:        str
    filename:         str
    duration_seconds: float
    description:      str


@router.get("/api/v1/providers")
async def list_providers(request: Request) -> list[dict]:
    registry = request.app.state.registry
    gpu      = request.app.state.gpu
    return [b.capability_dict(gpu.provider) for b in registry.all()]


@router.get("/api/v1/providers/{provider_id}")
async def get_provider(provider_id: str, request: Request) -> dict:
    registry = request.app.state.registry
    gpu      = request.app.state.gpu
    backend  = registry.get(provider_id)
    if backend is None:
        raise HTTPException(
            status_code=404,
            detail=f"Provider '{provider_id}' is not loaded. "
                   f"Loaded providers: {registry.provider_ids()}",
        )
    return backend.capability_dict(gpu.provider)


@router.get("/api/v1/providers/{provider_id}/voices",
            response_model=list[VoiceResponse])
async def get_voices(provider_id: str, request: Request) -> list[VoiceResponse]:
    registry = request.app.state.registry
    backend  = registry.get(provider_id)
    if backend is None:
        raise HTTPException(status_code=404,
                            detail=f"Provider '{provider_id}' is not loaded.")
    return [VoiceResponse(**v.__dict__) for v in backend.get_voices()]


@router.get("/api/v1/providers/{provider_id}/samples",
            response_model=list[SampleResponse])
async def get_samples(provider_id: str, request: Request) -> list[SampleResponse]:
    registry = request.app.state.registry
    settings = request.app.state.settings
    backend  = registry.get(provider_id)

    if backend is None:
        raise HTTPException(status_code=404,
                            detail=f"Provider '{provider_id}' is not loaded.")

    if not backend.supports_voice_matching:
        raise HTTPException(
            status_code=400,
            detail=f"Provider '{provider_id}' does not support voice matching.",
        )

    # Use provider-aware scan so reported duration reflects the provider-specific
    # extracted clip (e.g. Christopher_Walken-f5.wav = 6.5s) rather than the
    # master (59.9s). This ensures the client duration filter passes correctly.
    samples = scan_for_provider(settings.samples_dir, provider_id)
    return [
        SampleResponse(
            sample_id=s.sample_id,
            filename=s.filename,
            duration_seconds=s.duration_seconds,
            description=s.description,
        )
        for s in samples
    ]
