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
# server/sync_db.py
#
# Shared sync database for DB-backed catalog/profile sync.
#
# Tables:
#   npc_people_catalog
#   provider_slot_profiles
#
# Design goals:
#   - non-destructive delta sync
#   - store exact client/server profile JSON payloads without over-normalizing
#   - bootstrap from defaults JSON only when tables are empty

from __future__ import annotations

import json
import logging
import time
from pathlib import Path
from typing import Any, Iterable

import aiosqlite

log = logging.getLogger(__name__)

_CREATE_NPC_PEOPLE = """
CREATE TABLE IF NOT EXISTS npc_people_catalog (
    npc_id            INTEGER PRIMARY KEY,
    base_name         TEXT    NOT NULL DEFAULT '',
    sex               INTEGER DEFAULT NULL,
    race_id           INTEGER DEFAULT NULL,
    creature_type_id  INTEGER DEFAULT NULL,
    notes             TEXT    NOT NULL DEFAULT '',
    source            TEXT    NOT NULL DEFAULT 'seed',
    created_at        REAL    NOT NULL,
    updated_at        REAL    NOT NULL
)
"""

_CREATE_NPC_PEOPLE_UPDATED = """
CREATE INDEX IF NOT EXISTS idx_npc_people_catalog_updated_at
    ON npc_people_catalog (updated_at)
"""

_CREATE_NPC_PEOPLE_NAME = """
CREATE INDEX IF NOT EXISTS idx_npc_people_catalog_base_name
    ON npc_people_catalog (base_name)
"""

_CREATE_PROVIDER_SLOT_PROFILES = """
CREATE TABLE IF NOT EXISTS provider_slot_profiles (
    provider_id    TEXT NOT NULL,
    profile_kind   TEXT NOT NULL,
    profile_id     TEXT NOT NULL,
    profile_json   TEXT NOT NULL,
    source         TEXT NOT NULL DEFAULT 'seed',
    created_at     REAL NOT NULL,
    updated_at     REAL NOT NULL,
    PRIMARY KEY (provider_id, profile_kind, profile_id)
)
"""

_CREATE_PROVIDER_SLOT_UPDATED = """
CREATE INDEX IF NOT EXISTS idx_provider_slot_profiles_updated_at
    ON provider_slot_profiles (updated_at)
"""

_CREATE_PROVIDER_SLOT_KIND = """
CREATE INDEX IF NOT EXISTS idx_provider_slot_profiles_provider_kind
    ON provider_slot_profiles (provider_id, profile_kind)
"""


class SyncDb:
    """Async SQLite wrapper for server-side sync entities."""

    def __init__(self, db_path: Path, defaults_dir: Path) -> None:
        self._db_path = db_path
        self._defaults_dir = defaults_dir
        self._conn: aiosqlite.Connection | None = None

    async def initialize(self) -> None:
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._defaults_dir.mkdir(parents=True, exist_ok=True)
        self._conn = await aiosqlite.connect(str(self._db_path))
        self._conn.row_factory = aiosqlite.Row
        await self._conn.execute(_CREATE_NPC_PEOPLE)
        await self._conn.execute(_CREATE_NPC_PEOPLE_UPDATED)
        await self._conn.execute(_CREATE_NPC_PEOPLE_NAME)
        await self._conn.execute(_CREATE_PROVIDER_SLOT_PROFILES)
        await self._conn.execute(_CREATE_PROVIDER_SLOT_UPDATED)
        await self._conn.execute(_CREATE_PROVIDER_SLOT_KIND)
        await self._conn.commit()
        log.info("Sync DB initialized: %s", self._db_path)
        await self.bootstrap_from_defaults_if_empty()

    async def close(self) -> None:
        if self._conn is not None:
            await self._conn.close()
            self._conn = None

    async def _table_count(self, table_name: str) -> int:
        assert self._conn
        async with self._conn.execute(f"SELECT COUNT(*) AS c FROM {table_name}") as cursor:
            row = await cursor.fetchone()
        return int(row["c"])

    async def bootstrap_from_defaults_if_empty(self) -> None:
        """Seed DB from defaults JSON only when target tables are empty."""
        npc_count = await self._table_count("npc_people_catalog")
        profile_count = await self._table_count("provider_slot_profiles")

        # npc_people_catalog intentionally starts empty for now.
        # There is no historical server-side defaults seed for this table yet.

        if profile_count == 0:
            await self._delete_provider_wrapper_junk_rows()
            voice_path = self._defaults_dir / "voice-profiles.json"
            sample_path = self._defaults_dir / "voice-sample-profiles.json"
            voice_rows = self._normalize_profile_seed(self._read_json_file(voice_path), profile_kind="voice_slot")
            sample_rows = self._normalize_profile_seed(self._read_json_file(sample_path), profile_kind="sample")
            rows = voice_rows + sample_rows
            if rows:
                await self.upsert_provider_slot_profiles_batch(rows, source="seed")
                log.info(
                    "Bootstrapped provider_slot_profiles from defaults (%d rows)",
                    len(rows),
                )
            else:
                log.warning("provider_slot_profiles bootstrap found no rows in defaults files")

    def _read_json_file(self, path: Path) -> Any | None:
        if not path.exists():
            return None
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except Exception as e:
            log.warning("Failed reading defaults seed file %s: %s", path, e)
            return None

    def _normalize_npc_people_seed(self, payload: Any | None) -> list[dict[str, Any]]:
        if not payload:
            return []
        if isinstance(payload, dict):
            payload = payload.get("records") or payload.get("items") or payload.get("payload") or payload
        if not isinstance(payload, list):
            return []
        rows: list[dict[str, Any]] = []
        for item in payload:
            if not isinstance(item, dict):
                continue
            try:
                npc_id = int(item.get("npc_id", item.get("NpcId")))
            except Exception:
                continue
            rows.append(
                {
                    "npc_id": npc_id,
                    "base_name": str(item.get("base_name", item.get("BaseName", "")) or ""),
                    "sex": self._to_optional_int(item.get("sex", item.get("Sex"))),
                    "race_id": self._to_optional_int(item.get("race_id", item.get("RaceId"))),
                    "creature_type_id": self._to_optional_int(item.get("creature_type_id", item.get("CreatureTypeId"))),
                    "notes": str(item.get("notes", item.get("Notes", "")) or ""),
                }
            )
        return rows

    def _extract_profile_providers_map(self, payload: Any | None) -> dict[str, Any]:
        """Return providerId -> {profileId -> profileObj} map from supported defaults shapes."""
        if not payload or not isinstance(payload, dict):
            return {}

        current = payload
        # Defensive unwrap: tolerate one or more accidental wrapper layers.
        while isinstance(current, dict) and isinstance(current.get("Providers"), dict):
            inner = current.get("Providers")
            if inner is current:
                break
            current = inner

        if not isinstance(current, dict):
            return {}
        return current

    def _normalize_profile_seed(self, payload: Any | None, profile_kind: str) -> list[dict[str, Any]]:
        providers_map = self._extract_profile_providers_map(payload)
        if not providers_map:
            return []

        rows: list[dict[str, Any]] = []
        for provider_id, provider_payload in providers_map.items():
            if str(provider_id) == "Providers":
                # Guard against accidentally storing wrapper layer as real provider rows.
                if isinstance(provider_payload, dict):
                    for nested_provider_id, nested_provider_payload in provider_payload.items():
                        if not isinstance(nested_provider_payload, dict):
                            continue
                        for profile_id, profile_obj in nested_provider_payload.items():
                            if profile_obj is None:
                                continue
                            rows.append(
                                {
                                    "provider_id": str(nested_provider_id),
                                    "profile_kind": profile_kind,
                                    "profile_id": str(profile_id),
                                    "profile_json": profile_obj,
                                }
                            )
                continue

            if not isinstance(provider_payload, dict):
                continue
            for profile_id, profile_obj in provider_payload.items():
                if profile_obj is None:
                    continue
                rows.append(
                    {
                        "provider_id": str(provider_id),
                        "profile_kind": profile_kind,
                        "profile_id": str(profile_id),
                        "profile_json": profile_obj,
                    }
                )

        log.info(
            "Normalized profile seed kind=%s providers=%d rows=%d",
            profile_kind,
            len(providers_map),
            len(rows),
        )
        return rows

    @staticmethod
    def _to_optional_int(value: Any) -> int | None:
        if value is None or value == "":
            return None
        try:
            return int(value)
        except Exception:
            return None


    async def _delete_provider_wrapper_junk_rows(self) -> None:
        assert self._conn
        await self._conn.execute("DELETE FROM provider_slot_profiles WHERE provider_id = ?", ("Providers",))
        await self._conn.commit()

    async def get_npc_people_since(self, since_ts: float) -> list[dict[str, Any]]:
        assert self._conn
        async with self._conn.execute(
            "SELECT * FROM npc_people_catalog WHERE updated_at > ? ORDER BY updated_at ASC, npc_id ASC",
            (since_ts,),
        ) as cursor:
            rows = await cursor.fetchall()
        return [dict(r) for r in rows]

    async def get_all_npc_people(self) -> list[dict[str, Any]]:
        assert self._conn
        async with self._conn.execute(
            "SELECT * FROM npc_people_catalog ORDER BY npc_id ASC"
        ) as cursor:
            rows = await cursor.fetchall()
        return [dict(r) for r in rows]

    async def upsert_npc_people_batch(
        self,
        rows: Iterable[dict[str, Any]],
        *,
        source: str = "sync",
    ) -> int:
        assert self._conn
        now = time.time()
        normalized: list[tuple[Any, ...]] = []
        for row in rows:
            npc_id = row.get("npc_id")
            if npc_id is None:
                continue
            normalized.append(
                (
                    int(npc_id),
                    str(row.get("base_name", "") or ""),
                    self._to_optional_int(row.get("sex")),
                    self._to_optional_int(row.get("race_id")),
                    self._to_optional_int(row.get("creature_type_id")),
                    str(row.get("notes", "") or ""),
                    str(row.get("source", source) or source),
                    now,
                    now,
                )
            )
        if not normalized:
            return 0
        await self._conn.executemany(
            """
            INSERT INTO npc_people_catalog
                (npc_id, base_name, sex, race_id, creature_type_id, notes, source, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(npc_id) DO UPDATE SET
                base_name = excluded.base_name,
                sex = excluded.sex,
                race_id = excluded.race_id,
                creature_type_id = excluded.creature_type_id,
                notes = excluded.notes,
                source = excluded.source,
                updated_at = excluded.updated_at
            """,
            normalized,
        )
        await self._conn.commit()
        return len(normalized)

    async def get_provider_slot_profiles_since(
        self,
        since_ts: float,
        *,
        provider_id: str | None = None,
        profile_kind: str | None = None,
    ) -> list[dict[str, Any]]:
        assert self._conn
        sql = (
            "SELECT provider_id, profile_kind, profile_id, profile_json, source, created_at, updated_at "
            "FROM provider_slot_profiles WHERE updated_at > ?"
        )
        args: list[Any] = [since_ts]
        if provider_id:
            sql += " AND provider_id = ?"
            args.append(provider_id)
        if profile_kind:
            sql += " AND profile_kind = ?"
            args.append(profile_kind)
        sql += " ORDER BY updated_at ASC, provider_id ASC, profile_kind ASC, profile_id ASC"
        async with self._conn.execute(sql, tuple(args)) as cursor:
            rows = await cursor.fetchall()
        result: list[dict[str, Any]] = []
        for row in rows:
            item = dict(row)
            try:
                item["profile_json"] = json.loads(item["profile_json"])
            except Exception:
                pass
            result.append(item)
        return result

    async def get_all_provider_slot_profiles(
        self,
        *,
        provider_id: str | None = None,
        profile_kind: str | None = None,
    ) -> list[dict[str, Any]]:
        return await self.get_provider_slot_profiles_since(-1.0, provider_id=provider_id, profile_kind=profile_kind)

    async def upsert_provider_slot_profiles_batch(
        self,
        rows: Iterable[dict[str, Any]],
        *,
        source: str = "sync",
    ) -> int:
        assert self._conn
        now = time.time()
        normalized: list[tuple[Any, ...]] = []
        for row in rows:
            provider_id = str(row.get("provider_id", "") or "").strip()
            profile_kind = str(row.get("profile_kind", "") or "").strip()
            profile_id = str(row.get("profile_id", "") or "").strip()
            if not provider_id or not profile_kind or not profile_id:
                continue
            profile_json = row.get("profile_json")
            normalized.append(
                (
                    provider_id,
                    profile_kind,
                    profile_id,
                    json.dumps(profile_json, ensure_ascii=False, sort_keys=True, separators=(",", ":")),
                    str(row.get("source", source) or source),
                    now,
                    now,
                )
            )
        if not normalized:
            return 0
        await self._conn.executemany(
            """
            INSERT INTO provider_slot_profiles
                (provider_id, profile_kind, profile_id, profile_json, source, created_at, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(provider_id, profile_kind, profile_id) DO UPDATE SET
                profile_json = excluded.profile_json,
                source = excluded.source,
                updated_at = excluded.updated_at
            """,
            normalized,
        )
        await self._conn.commit()
        return len(normalized)
