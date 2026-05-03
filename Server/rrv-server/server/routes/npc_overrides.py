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
# server/routes/npc_overrides.py
#
# NPC voice override community endpoints.
#
#   GET  /api/v1/npc-overrides/since?t={unix_ts}   — poll for updates (open)
#   GET  /api/v1/npc-overrides                     — get all records (open)
#   POST /api/v1/npc-overrides                     — contribute a record (token)
#   PUT  /api/v1/npc-overrides/{npc_id}            — admin confirm/edit (admin token)

from __future__ import annotations

import logging
from typing import Optional

from fastapi import APIRouter, HTTPException, Request, Header
from pydantic import BaseModel, Field

log = logging.getLogger(__name__)

router = APIRouter()


# ── Request / Response models ─────────────────────────────────────────────────

class NpcOverrideContributeRequest(BaseModel):
    npc_id:               int
    catalog_id:           Optional[str]   = None
    race_id:              int
    notes:                str   = ""
    bespoke_sample_id:    Optional[str]   = None
    bespoke_exaggeration: Optional[float] = None
    bespoke_cfg_weight:   Optional[float] = None
    gender_override:      Optional[str]   = None


class NpcOverrideAdminRequest(BaseModel):
    catalog_id:           Optional[str]   = None
    race_id:              int
    notes:                str   = ""
    bespoke_sample_id:    Optional[str]   = None
    bespoke_exaggeration: Optional[float] = None
    bespoke_cfg_weight:   Optional[float] = None
    gender_override:      Optional[str]   = None
    source:               str   = "confirmed"



class NpcOverrideBatchRequest(BaseModel):
    records: list[NpcOverrideContributeRequest] = Field(default_factory=list, max_length=100)


# ── Auth helpers ──────────────────────────────────────────────────────────────

def _check_contribute_token(authorization: str | None, settings) -> None:
    """
    Validates the contribute token (Bearer).
    Uses RRV_CONTRIBUTE_KEY — a low-friction shared secret.
    Good-citizen gate: requires at least minimal effort to bypass.
    """
    if not settings.contribute_key:
        return  # No key configured — open contribution (LAN/trusted mode)
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Contribute token required.")
    if authorization[len("Bearer "):] != settings.contribute_key:
        raise HTTPException(status_code=403, detail="Invalid contribute token.")


def _check_admin_token(authorization: str | None, settings) -> None:
    """
    Validates the admin token (Bearer).
    Uses RRV_ADMIN_KEY — gates destructive/confirm operations.
    """
    if not settings.admin_key:
        return  # No key configured — open admin (LAN/trusted mode)
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Admin token required.")
    if authorization[len("Bearer "):] != settings.admin_key:
        raise HTTPException(status_code=403, detail="Invalid admin token.")


# ── Endpoints ─────────────────────────────────────────────────────────────────

@router.get("/api/v1/npc-overrides/since")
async def get_npc_overrides_since(
    request: Request,
    t: float = 0.0,
):
    """
    Returns all NPC override records updated after Unix timestamp t.
    t=0 returns all records (used for initial full pull).
    Open endpoint — no auth required.
    """
    db = request.app.state.community_db
    records = await db.get_since(t)
    return {"records": records, "count": len(records)}


@router.get("/api/v1/npc-overrides")
async def get_all_npc_overrides(request: Request):
    """Returns all NPC override records. Open endpoint."""
    db = request.app.state.community_db
    records = await db.get_all()
    return {"records": records, "count": len(records)}


@router.post("/api/v1/npc-overrides", status_code=201)
async def contribute_npc_override(
    request: Request,
    body: NpcOverrideContributeRequest,
    authorization: Optional[str] = Header(default=None),
):
    """
    Contribute a crowd-sourced NPC override record.
    Requires contribute token if RRV_CONTRIBUTE_KEY is set.
    On conflict: increments confidence, updates fields.
    """
    cfg = request.app.state.settings
    _check_contribute_token(authorization, cfg)

    db = request.app.state.community_db
    record = await db.upsert(
        npc_id               = body.npc_id,
        catalog_id           = body.catalog_id,
        race_id              = body.race_id,
        notes                = body.notes,
        bespoke_sample_id    = body.bespoke_sample_id,
        bespoke_exaggeration = body.bespoke_exaggeration,
        bespoke_cfg_weight   = body.bespoke_cfg_weight,
        gender_override      = body.gender_override,
        source               = "crowdsourced",
        confidence_delta     = 1,
    )
    log.info(
        "NPC override contributed: npc_id=%d catalog_id=%s race_id=%d confidence=%d",
        body.npc_id, body.catalog_id, body.race_id, record["confidence"],
    )
    return {"record": record}


@router.post("/api/v1/npc-overrides/batch", status_code=201)
async def contribute_npc_override_batch(
    request: Request,
    body: NpcOverrideBatchRequest,
    authorization: Optional[str] = Header(default=None),
):
    """
    Batch contribute crowd-sourced NPC override records.
    Requires contribute token if configured.
    """
    cfg = request.app.state.settings
    _check_contribute_token(authorization, cfg)

    db = request.app.state.community_db
    payload = [
        {
            "npc_id": r.npc_id,
            "catalog_id": r.catalog_id,
            "race_id": r.race_id,
            "notes": r.notes,
            "bespoke_sample_id": r.bespoke_sample_id,
            "bespoke_exaggeration": r.bespoke_exaggeration,
            "bespoke_cfg_weight": r.bespoke_cfg_weight,
            "gender_override": r.gender_override,
        }
        for r in body.records
    ]
    upserted, records = await db.upsert_many(
        payload,
        source="crowdsourced",
        confidence_delta=1,
    )
    log.info("NPC override batch contributed: %d row(s)", upserted)
    return {"upserted": upserted, "count": upserted, "records": records}


@router.put("/api/v1/npc-overrides/{npc_id}", status_code=200)
async def admin_confirm_npc_override(
    request: Request,
    npc_id: int,
    body: NpcOverrideAdminRequest,
    authorization: Optional[str] = Header(default=None),
):
    """
    Admin endpoint — confirm or edit an NPC override record.
    Sets source to 'confirmed' by default.
    Requires admin token if RRV_ADMIN_KEY is set.
    """
    cfg = request.app.state.settings
    _check_admin_token(authorization, cfg)

    db = request.app.state.community_db
    record = await db.upsert(
        npc_id               = npc_id,
        catalog_id           = body.catalog_id,
        race_id              = body.race_id,
        notes                = body.notes,
        bespoke_sample_id    = body.bespoke_sample_id,
        bespoke_exaggeration = body.bespoke_exaggeration,
        bespoke_cfg_weight   = body.bespoke_cfg_weight,
        gender_override      = body.gender_override,
        source               = body.source,
        confidence_delta     = 0,
    )
    log.info(
        "NPC override confirmed by admin: npc_id=%d catalog_id=%s race_id=%d source=%s",
        npc_id, body.catalog_id, body.race_id, body.source,
    )
    return {"record": record}
