# SPDX-License-Identifier: GPL-3.0-or-later
# server/routes/capabilities.py

from __future__ import annotations

from fastapi import APIRouter, Request
from pydantic import BaseModel

router = APIRouter()


class CapabilitiesResponse(BaseModel):
    api_version:       str
    server_version:    str
    auth_required:     bool
    execution_provider: str
    loaded_backends:   list[str]


@router.get("/api/v1/capabilities", response_model=CapabilitiesResponse)
async def capabilities(request: Request) -> CapabilitiesResponse:
    registry = request.app.state.registry
    gpu      = request.app.state.gpu
    settings = request.app.state.settings

    return CapabilitiesResponse(
        api_version="1",
        server_version="0.1.0",
        auth_required=settings.auth_enabled,
        execution_provider=gpu.provider,
        loaded_backends=registry.provider_ids(),
    )
