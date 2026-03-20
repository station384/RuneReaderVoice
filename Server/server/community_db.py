# SPDX-License-Identifier: GPL-3.0-or-later
# server/community_db.py
#
# Community database for crowd-sourced NPC voice override records.
# Stored in data/community.db — separate from the audio cache DB.
#
# Source hierarchy (highest priority wins on client):
#   Local > CrowdSourced > Confirmed
#
# This server only stores CrowdSourced and Confirmed records.
# Local records exist only on client machines and are never sent here.
#
# Endpoints:
#   GET  /api/v1/npc-overrides/since?t=   — poll for new/updated records
#   POST /api/v1/npc-overrides            — contribute a record (crowd-source)
#   PUT  /api/v1/npc-overrides/{npc_id}   — admin confirm/edit a record

from __future__ import annotations

import logging
import time
from pathlib import Path
from typing import Any

import aiosqlite

log = logging.getLogger(__name__)

_CREATE_TABLE = """
CREATE TABLE IF NOT EXISTS npc_overrides (
    npc_id               INTEGER PRIMARY KEY,
    race_id              INTEGER NOT NULL,
    notes                TEXT    DEFAULT '',
    bespoke_sample_id    TEXT    DEFAULT NULL,
    bespoke_exaggeration REAL    DEFAULT NULL,
    bespoke_cfg_weight   REAL    DEFAULT NULL,
    source               TEXT    NOT NULL DEFAULT 'crowdsourced',
    confidence           INTEGER NOT NULL DEFAULT 1,
    created_at           REAL    NOT NULL,
    updated_at           REAL    NOT NULL
)
"""

_CREATE_INDEX = """
CREATE INDEX IF NOT EXISTS idx_npc_overrides_updated_at
    ON npc_overrides (updated_at)
"""


class CommunityDb:
    """
    Async SQLite wrapper for community NPC override records.
    One instance shared for the server lifetime — opened in lifespan.
    """

    def __init__(self, db_path: Path) -> None:
        self._db_path = db_path
        self._conn: aiosqlite.Connection | None = None

    async def initialize(self) -> None:
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = await aiosqlite.connect(str(self._db_path))
        self._conn.row_factory = aiosqlite.Row
        await self._conn.execute(_CREATE_TABLE)
        await self._conn.execute(_CREATE_INDEX)
        await self._conn.commit()
        log.info("Community DB initialized: %s", self._db_path)

    async def close(self) -> None:
        if self._conn:
            await self._conn.close()
            self._conn = None

    # ── Queries ───────────────────────────────────────────────────────────────

    async def get_since(self, since_ts: float) -> list[dict[str, Any]]:
        """Return all records updated after since_ts (Unix timestamp)."""
        assert self._conn
        async with self._conn.execute(
            "SELECT * FROM npc_overrides WHERE updated_at > ? ORDER BY updated_at ASC",
            (since_ts,),
        ) as cursor:
            rows = await cursor.fetchall()
        return [dict(r) for r in rows]

    async def get_all(self) -> list[dict[str, Any]]:
        """Return all records — used for admin export."""
        assert self._conn
        async with self._conn.execute(
            "SELECT * FROM npc_overrides ORDER BY npc_id ASC"
        ) as cursor:
            rows = await cursor.fetchall()
        return [dict(r) for r in rows]

    async def upsert(
        self,
        npc_id: int,
        race_id: int,
        notes: str = "",
        bespoke_sample_id: str | None = None,
        bespoke_exaggeration: float | None = None,
        bespoke_cfg_weight: float | None = None,
        source: str = "crowdsourced",
        confidence_delta: int = 1,
    ) -> dict[str, Any]:
        """
        Insert or update a record.
        On conflict: increments confidence, updates fields, sets updated_at.
        Source is only upgraded (crowdsourced → confirmed), never downgraded.
        """
        assert self._conn
        now = time.time()

        # Check for existing record
        async with self._conn.execute(
            "SELECT * FROM npc_overrides WHERE npc_id = ?", (npc_id,)
        ) as cursor:
            existing = await cursor.fetchone()

        if existing:
            existing = dict(existing)
            # Never downgrade source
            new_source = existing["source"]
            if source == "confirmed":
                new_source = "confirmed"

            new_confidence = existing["confidence"] + confidence_delta

            await self._conn.execute(
                """
                UPDATE npc_overrides SET
                    race_id              = ?,
                    notes                = ?,
                    bespoke_sample_id    = ?,
                    bespoke_exaggeration = ?,
                    bespoke_cfg_weight   = ?,
                    source               = ?,
                    confidence           = ?,
                    updated_at           = ?
                WHERE npc_id = ?
                """,
                (
                    race_id,
                    notes or existing["notes"],
                    bespoke_sample_id,
                    bespoke_exaggeration,
                    bespoke_cfg_weight,
                    new_source,
                    new_confidence,
                    now,
                    npc_id,
                ),
            )
        else:
            await self._conn.execute(
                """
                INSERT INTO npc_overrides
                    (npc_id, race_id, notes, bespoke_sample_id,
                     bespoke_exaggeration, bespoke_cfg_weight,
                     source, confidence, created_at, updated_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    npc_id, race_id, notes or "",
                    bespoke_sample_id, bespoke_exaggeration, bespoke_cfg_weight,
                    source, confidence_delta, now, now,
                ),
            )

        await self._conn.commit()

        async with self._conn.execute(
            "SELECT * FROM npc_overrides WHERE npc_id = ?", (npc_id,)
        ) as cursor:
            row = await cursor.fetchone()
        return dict(row)

    async def confirm(self, npc_id: int, **fields) -> dict[str, Any] | None:
        """Promote a record to confirmed and optionally update fields."""
        return await self.upsert(npc_id, source="confirmed", **fields)
