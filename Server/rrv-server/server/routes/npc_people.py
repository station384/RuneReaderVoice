# SPDX-License-Identifier: GPL-3.0-or-later
from __future__ import annotations

from typing import Optional

from fastapi import APIRouter, Header, HTTPException, Request
from pydantic import BaseModel

router = APIRouter()


class NpcPersonRecord(BaseModel):
    npc_id: int
    base_name: str = ""
    sex: int | None = None
    race_id: int | None = None
    creature_type_id: int | None = None
    notes: str = ""


class NpcPeopleBatchRequest(BaseModel):
    records: list[NpcPersonRecord]


def _check_admin_token(authorization: str | None, settings) -> None:
    if not settings.admin_key:
        return
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Admin token required.")
    if authorization[len("Bearer "):] != settings.admin_key:
        raise HTTPException(status_code=403, detail="Invalid admin token.")


@router.get("/api/v1/npc-people/since")
async def get_npc_people_since(request: Request, t: float = 0.0):
    db = request.app.state.sync_db
    records = await db.get_npc_people_since(t)
    return {"records": records, "count": len(records)}


@router.get("/api/v1/npc-people")
async def get_all_npc_people(request: Request):
    db = request.app.state.sync_db
    records = await db.get_all_npc_people()
    return {"records": records, "count": len(records)}


@router.post("/api/v1/npc-people/batch", status_code=200)
async def upsert_npc_people_batch(
    request: Request,
    body: NpcPeopleBatchRequest,
    authorization: Optional[str] = Header(default=None),
):
    cfg = request.app.state.settings
    _check_admin_token(authorization, cfg)
    if len(body.records) > 100:
        raise HTTPException(status_code=400, detail="Batch limit is 100 records.")
    db = request.app.state.sync_db
    count = await db.upsert_npc_people_batch([r.model_dump() for r in body.records], source="sync")
    return {"upserted": count}
