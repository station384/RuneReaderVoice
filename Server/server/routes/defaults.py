# SPDX-License-Identifier: GPL-3.0-or-later
# server/routes/defaults.py
#
# Server-side default seed data for client first-load and manual pull.
# Stored as plain JSON files in data/defaults/.
#
# Supported types: voice-profiles, pronunciation, text-shaping, npc-overrides
#
#   GET /api/v1/defaults/{type}   — pull defaults (open)
#   PUT /api/v1/defaults/{type}   — push/replace defaults (admin token)

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Optional

from fastapi import APIRouter, HTTPException, Request, Header
from fastapi.responses import JSONResponse

log = logging.getLogger(__name__)

router = APIRouter()

VALID_TYPES = frozenset({"voice-profiles", "voice-sample-profiles", "pronunciation", "text-shaping", "npc-overrides"})


# ── Auth helper ───────────────────────────────────────────────────────────────

def _check_admin_token(authorization: str | None, settings) -> None:
    if not settings.admin_key:
        return
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Admin token required.")
    if authorization[len("Bearer "):] != settings.admin_key:
        raise HTTPException(status_code=403, detail="Invalid admin token.")


# ── Path helper ───────────────────────────────────────────────────────────────

def _defaults_path(settings, data_type: str) -> Path:
    return settings.defaults_dir / f"{data_type}.json"


# ── Endpoints ─────────────────────────────────────────────────────────────────

@router.get("/api/v1/defaults/{data_type}")
async def get_defaults(request: Request, data_type: str):
    """
    Returns the server default seed data for the given type.
    Returns empty payload if no defaults file exists yet.
    Open endpoint — no auth required.
    """
    if data_type not in VALID_TYPES:
        raise HTTPException(
            status_code=400,
            detail=f"Unknown defaults type {data_type!r}. Valid: {sorted(VALID_TYPES)}",
        )

    path = _defaults_path(request.app.state.settings, data_type)

    if not path.exists():
        # No defaults configured yet — return empty payload, not an error
        return {"data_type": data_type, "payload": None, "exists": False}

    try:
        content = json.loads(path.read_text(encoding="utf-8"))
        return {"data_type": data_type, "payload": content, "exists": True}
    except Exception as e:
        log.error("Failed to read defaults file %s: %s", path, e)
        raise HTTPException(status_code=500, detail="Failed to read defaults file.")


@router.put("/api/v1/defaults/{data_type}", status_code=200)
async def put_defaults(
    request: Request,
    data_type: str,
    authorization: Optional[str] = Header(default=None),
):
    """
    Replaces the server default seed data for the given type.
    Body must be valid JSON — the same format the client uses for export.
    Requires admin token if RRV_ADMIN_KEY is set.
    """
    if data_type not in VALID_TYPES:
        raise HTTPException(
            status_code=400,
            detail=f"Unknown defaults type {data_type!r}. Valid: {sorted(VALID_TYPES)}",
        )

    cfg = request.app.state.settings
    _check_admin_token(authorization, cfg)

    try:
        body = await request.json()
    except Exception:
        raise HTTPException(status_code=400, detail="Request body must be valid JSON.")

    path = _defaults_path(cfg, data_type)
    path.parent.mkdir(parents=True, exist_ok=True)

    try:
        path.write_text(
            json.dumps(body, indent=2, ensure_ascii=False),
            encoding="utf-8",
        )
    except Exception as e:
        log.error("Failed to write defaults file %s: %s", path, e)
        raise HTTPException(status_code=500, detail="Failed to write defaults file.")

    log.info("Defaults updated: type=%s size=%d bytes", data_type, path.stat().st_size)
    return {"data_type": data_type, "size_bytes": path.stat().st_size}
