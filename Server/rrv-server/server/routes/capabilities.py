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
