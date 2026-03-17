# SPDX-License-Identifier: GPL-3.0-or-later
# server/routes/health.py

from __future__ import annotations

from fastapi import APIRouter
from pydantic import BaseModel

router = APIRouter()


class HealthResponse(BaseModel):
    status:   str
    version:  str = "0.1.0"


@router.get("/api/v1/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    return HealthResponse(status="ok")
