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
# server/cache.py
#
# Server-side audio cache.
#
# Storage model:
#   - OGG files stored under RRV_CACHE_DIR as <32-hex-key>.ogg
#   - SQLite manifest in RRV_DB_PATH tracks key → file metadata
#   - Atomic writes: synthesize → write <key>.ogg.tmp → rename to <key>.ogg → insert DB row
#   - LRU eviction: remove least-recently-accessed files when cache_max_bytes exceeded
#
# Startup integrity:
#   1. Delete any leftover .tmp files from a previous interrupted write
#   2. Remove DB rows whose OGG file no longer exists (will be regenerated on next request)
#
# Cache timestamps are server-generated only. No client-supplied timestamps accepted.
#
# Per-key asyncio lock prevents synthesis stampede when two clients request
# the same uncached key simultaneously.

from __future__ import annotations

import asyncio
import hashlib
import logging
import time
from pathlib import Path
from typing import Optional

import aiosqlite

log = logging.getLogger(__name__)

_CREATE_TABLE = """
CREATE TABLE IF NOT EXISTS cache_manifest (
    key             TEXT PRIMARY KEY,
    filename        TEXT NOT NULL,
    provider_id     TEXT NOT NULL,
    size_bytes      INTEGER NOT NULL,
    duration_sec    REAL NOT NULL,
    last_accessed   INTEGER NOT NULL,   -- Unix timestamp, server clock only
    created         INTEGER NOT NULL
)
"""

_CREATE_INDEX = """
CREATE INDEX IF NOT EXISTS idx_last_accessed ON cache_manifest (last_accessed)
"""


class AudioCache:
    """
    Manages the server-side OGG cache directory and SQLite manifest.
    Call initialize() once before use.
    """

    def __init__(self, cache_dir: Path, db_path: Path, max_bytes: int) -> None:
        self._cache_dir = cache_dir
        self._db_path   = db_path
        self._max_bytes = max_bytes
        self._db: Optional[aiosqlite.Connection] = None

        # Per-key locks: prevents duplicate synthesis for the same cache key
        self._key_locks: dict[str, asyncio.Lock] = {}
        self._key_locks_mutex = asyncio.Lock()

    # ── Lifecycle ─────────────────────────────────────────────────────────────

    async def initialize(self) -> None:
        """
        Open the DB, create tables, run startup integrity checks.
        Must be called once before any other method.
        """
        self._cache_dir.mkdir(parents=True, exist_ok=True)

        self._db = await aiosqlite.connect(str(self._db_path))
        self._db.row_factory = aiosqlite.Row
        await self._db.execute("PRAGMA journal_mode=WAL")
        await self._db.execute("PRAGMA synchronous=NORMAL")
        await self._db.execute(_CREATE_TABLE)
        await self._db.execute(_CREATE_INDEX)
        await self._db.commit()

        await self._startup_integrity_check()
        log.info("Cache initialized: dir=%s db=%s max=%d MB",
                 self._cache_dir, self._db_path, self._max_bytes // (1024 * 1024))

    async def close(self) -> None:
        if self._db:
            await self._db.close()
            self._db = None

    # ── Public API ────────────────────────────────────────────────────────────

    async def get(self, key: str) -> Optional[bytes]:
        """
        Return the cached OGG bytes for key, or None on miss.
        Updates last_accessed on hit.
        If the DB row exists but the file is missing, the row is deleted
        and None is returned — the caller will re-synthesize.
        """
        assert self._db is not None

        async with self._db.execute(
            "SELECT filename FROM cache_manifest WHERE key = ?", (key,)
        ) as cur:
            row = await cur.fetchone()

        if row is None:
            return None

        path = self._cache_dir / row["filename"]
        if not path.exists():
            log.warning("Cache manifest row exists but file missing: %s — removing row", path)
            await self._db.execute("DELETE FROM cache_manifest WHERE key = ?", (key,))
            await self._db.commit()
            return None

        # Update last_accessed — server clock only
        now = int(time.time())
        await self._db.execute(
            "UPDATE cache_manifest SET last_accessed = ? WHERE key = ?", (now, key)
        )
        await self._db.commit()

        return path.read_bytes()

    async def store(self, key: str, provider_id: str, ogg_bytes: bytes,
                    duration_sec: float) -> None:
        """
        Write OGG bytes to the cache atomically.
        Uses .tmp → rename pattern to prevent partially-written files.
        Triggers LRU eviction after storing if the cache is over the size limit.
        """
        assert self._db is not None

        filename = f"{key}.ogg"
        final_path = self._cache_dir / filename
        tmp_path   = self._cache_dir / f"{key}.ogg.tmp"

        # Write to .tmp first
        tmp_path.write_bytes(ogg_bytes)
        # Atomic rename
        tmp_path.rename(final_path)

        now = int(time.time())
        await self._db.execute(
            """
            INSERT OR REPLACE INTO cache_manifest
                (key, filename, provider_id, size_bytes, duration_sec, last_accessed, created)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (key, filename, provider_id, len(ogg_bytes), duration_sec, now, now),
        )
        await self._db.commit()

        log.debug("Cached %s: %d bytes, %.2fs", key, len(ogg_bytes), duration_sec)
        await self._evict_if_needed()

    def key_lock(self, key: str) -> asyncio.Lock:
        """
        Returns a per-key asyncio.Lock. Acquiring this before synthesis ensures
        that only one coroutine synthesizes a given key at a time; subsequent
        callers waiting on the lock will find a cache hit when they proceed.
        Locks are created lazily and never explicitly cleaned up — they are small
        and the number of distinct keys in a session is bounded.
        """
        # Note: This is called from async context but the dict access is sync.
        # The _key_locks_mutex guards concurrent creation; however since we're
        # in a single-threaded asyncio event loop the dict access itself is safe.
        # We still check-and-create to avoid overwriting an existing lock.
        if key not in self._key_locks:
            self._key_locks[key] = asyncio.Lock()
        return self._key_locks[key]

    async def stats(self) -> dict:
        """Return cache statistics for the health/diagnostics endpoint."""
        assert self._db is not None

        async with self._db.execute(
            "SELECT COUNT(*) as cnt, SUM(size_bytes) as total FROM cache_manifest"
        ) as cur:
            row = await cur.fetchone()

        count = row["cnt"] or 0
        total = row["total"] or 0
        return {
            "entry_count":  count,
            "total_bytes":  total,
            "total_mb":     round(total / (1024 * 1024), 2),
            "max_mb":       self._max_bytes // (1024 * 1024),
        }

    # ── Startup integrity ─────────────────────────────────────────────────────

    async def _startup_integrity_check(self) -> None:
        assert self._db is not None

        # 1. Delete leftover .tmp files from interrupted writes
        tmp_files = list(self._cache_dir.glob("*.ogg.tmp"))
        for f in tmp_files:
            log.warning("Startup: removing leftover temp file: %s", f)
            f.unlink(missing_ok=True)
        if tmp_files:
            log.info("Startup: removed %d leftover temp file(s)", len(tmp_files))

        # 2. Find DB rows whose OGG file is missing and delete them
        async with self._db.execute(
            "SELECT key, filename FROM cache_manifest"
        ) as cur:
            rows = await cur.fetchall()

        stale_keys = []
        for row in rows:
            path = self._cache_dir / row["filename"]
            if not path.exists():
                stale_keys.append(row["key"])

        if stale_keys:
            log.warning(
                "Startup: %d manifest row(s) have no corresponding file — "
                "removing rows (will regenerate on next request)",
                len(stale_keys),
            )
            await self._db.executemany(
                "DELETE FROM cache_manifest WHERE key = ?",
                [(k,) for k in stale_keys],
            )
            await self._db.commit()

        log.info("Startup integrity check complete")

    # ── LRU eviction ─────────────────────────────────────────────────────────

    async def _evict_if_needed(self) -> None:
        assert self._db is not None

        async with self._db.execute(
            "SELECT SUM(size_bytes) as total FROM cache_manifest"
        ) as cur:
            row = await cur.fetchone()

        total = row["total"] or 0
        if total <= self._max_bytes:
            return

        log.info(
            "Cache eviction triggered: current=%d MB limit=%d MB",
            total // (1024 * 1024),
            self._max_bytes // (1024 * 1024),
        )

        # Fetch LRU candidates ordered by last_accessed ascending (oldest first)
        async with self._db.execute(
            "SELECT key, filename, size_bytes FROM cache_manifest ORDER BY last_accessed ASC"
        ) as cur:
            candidates = await cur.fetchall()

        evicted = 0
        for row in candidates:
            if total <= self._max_bytes:
                break
            path = self._cache_dir / row["filename"]
            try:
                path.unlink(missing_ok=True)
            except OSError as e:
                log.warning("Eviction: failed to delete %s: %s", path, e)
                continue

            await self._db.execute("DELETE FROM cache_manifest WHERE key = ?", (row["key"],))
            total -= row["size_bytes"]
            evicted += 1

        await self._db.commit()
        log.info("Cache eviction complete: removed %d file(s)", evicted)


# ── Cache key ─────────────────────────────────────────────────────────────────

def compute_cache_key(
    text: str,
    provider_id: str,
    model_version: str,
    voice_identity: str,
    lang_code: str,
    speech_rate: float,
    cfg_weight:          float | None = None,
    exaggeration:        float | None = None,
    cfg_strength:        float | None = None,
    nfe_step:            int   | None = None,
    cross_fade_duration: float | None = None,
    sway_sampling_coef:  float | None = None,
    voice_context:       str   | None = None,
) -> str:
    """
    Compute the 32-char hex server cache key.

    Fields are joined with null-byte separators to prevent collisions
    from adjacent field concatenation. This must match the algorithm
    documented in the design spec (Section 21.9).

    voice_identity:
      - base voice:   the voice_id string
      - reference:    SHA-256 of the sample file contents (8 hex chars)
      - blend:        canonical sorted "voice_id:weight" pairs joined by "|"
    """
    # Normalize control values for consistent hashing
    rate_str  = f"{speech_rate:.2f}"
    cfg_str   = "" if cfg_weight          is None else f"{cfg_weight:.3f}"
    exag_str  = "" if exaggeration        is None else f"{exaggeration:.3f}"
    cfs_str   = "" if cfg_strength        is None else f"{cfg_strength:.3f}"
    nfe_str   = "" if nfe_step            is None else str(nfe_step)
    xfade_str = "" if cross_fade_duration is None else f"{cross_fade_duration:.3f}"
    sway_str  = "" if sway_sampling_coef  is None else f"{sway_sampling_coef:.3f}"

    ctx_str = voice_context or ""

    parts = [
        text, provider_id, model_version, voice_identity,
        lang_code, rate_str, cfg_str, exag_str,
        cfs_str, nfe_str, xfade_str, sway_str,
        ctx_str,  # slot identity — prevents narrator/NPC cache collisions on same sample+text
    ]
    joined = "\x00".join(parts)
    digest = hashlib.sha256(joined.encode("utf-8")).hexdigest()
    return digest[:32]


def compute_file_hash(path: Path) -> str:
    """
    SHA-256 hash of a file's contents, truncated to 8 hex characters.
    Used as the voice_identity component for reference-based synthesis.
    Replacing the file changes the hash, automatically invalidating cache entries.
    """
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()[:8]


def blend_voice_identity(blend: list[dict]) -> str:
    """
    Canonical voice identity string for a blend request.
    Sorts by voice_id for stability, normalizes weights to 2 decimal places.
    Example: [{"voice_id": "bm_lewis", "weight": 0.6}, {"voice_id": "am_adam", "weight": 0.4}]
             → "am_adam:0.40|bm_lewis:0.60"
    """
    pairs = sorted(
        (f"{entry['voice_id']}:{entry['weight']:.2f}" for entry in blend)
    )
    return "|".join(pairs)
