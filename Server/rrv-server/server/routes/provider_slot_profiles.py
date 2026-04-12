# SPDX-License-Identifier: GPL-3.0-or-later
from __future__ import annotations

from typing import Any, Literal, Optional

from fastapi import APIRouter, Header, HTTPException, Query, Request
from pydantic import BaseModel

router = APIRouter()
_VALID_KINDS = {"voice_slot", "sample"}


class ProviderSlotProfileRecord(BaseModel):
    provider_id: str
    profile_kind: Literal["voice_slot", "sample"]
    profile_id: str
    profile_json: dict[str, Any]


class ProviderSlotProfileBatchRequest(BaseModel):
    records: list[ProviderSlotProfileRecord]


def _check_admin_token(authorization: str | None, settings) -> None:
    if not settings.admin_key:
        return
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Admin token required.")
    if authorization[len("Bearer "):] != settings.admin_key:
        raise HTTPException(status_code=403, detail="Invalid admin token.")


@router.get("/api/v1/provider-slot-profiles/since")
async def get_provider_slot_profiles_since(
    request: Request,
    t: float = 0.0,
    provider_id: str | None = None,
    kind: str | None = Query(default=None),
):
    if kind and kind not in _VALID_KINDS:
        raise HTTPException(status_code=400, detail="kind must be voice_slot or sample")
    db = request.app.state.sync_db
    records = await db.get_provider_slot_profiles_since(t, provider_id=provider_id, profile_kind=kind)
    return {"records": records, "count": len(records)}


@router.get("/api/v1/provider-slot-profiles")
async def get_all_provider_slot_profiles(
    request: Request,
    provider_id: str | None = None,
    kind: str | None = Query(default=None),
):
    if kind and kind not in _VALID_KINDS:
        raise HTTPException(status_code=400, detail="kind must be voice_slot or sample")
    db = request.app.state.sync_db
    records = await db.get_all_provider_slot_profiles(provider_id=provider_id, profile_kind=kind)
    return {"records": records, "count": len(records)}


@router.post("/api/v1/provider-slot-profiles/batch", status_code=200)
async def upsert_provider_slot_profiles_batch(
    request: Request,
    body: ProviderSlotProfileBatchRequest,
    authorization: Optional[str] = Header(default=None),
):
    cfg = request.app.state.settings
    _check_admin_token(authorization, cfg)
    if len(body.records) > 100:
        raise HTTPException(status_code=400, detail="Batch limit is 100 records.")
    db = request.app.state.sync_db
    count = await db.upsert_provider_slot_profiles_batch([r.model_dump() for r in body.records], source="sync")
    return {"upserted": count}
