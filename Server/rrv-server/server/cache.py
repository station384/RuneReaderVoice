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
#   - OGG files stored under RRV_CACHE_DIR/{provider_id}/{key}.ogg
#   - One subdirectory per provider — allows clearing a single provider's
#     cache without affecting others (rm -rf data/cache/kokoro/ etc.)
#   - SQLite manifest in RRV_DB_PATH tracks key -> file metadata
#   - Atomic writes: synthesize -> write {key}.ogg.tmp -> rename to {key}.ogg -> insert DB row
#   - LRU eviction: removes least-recently-accessed files when cache_max_bytes exceeded
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
    filename        TEXT NOT NULL,      -- relative path: {provider_id}/{key}.ogg
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

_CREATE_PROVIDER_INDEX = """
CREATE INDEX IF NOT EXISTS idx_provider ON cache_manifest (provider_id)
"""


class AudioCache:
    """
    Manages the server-side OGG cache directory and SQLite manifest.

    OGG files are stored in per-provider subdirectories:
        {cache_dir}/{provider_id}/{key}.ogg

    This lets you wipe a single provider's cache without touching others:
        rm -rf data/cache/kokoro/

    Call initialize() once before use.
    """

    def __init__(self, cache_dir: Path, db_path: Path, max_bytes: int) -> None:
        self._cache_dir = cache_dir
        self._db_path   = db_path
        self._max_bytes = max_bytes
        self._db: Optional[aiosqlite.Connection] = None

        # Per-key locks: prevents duplicate synthesis for the same cache key
        self._key_locks: dict[str, asyncio.Lock] = {}

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
        await self._db.execute(_CREATE_PROVIDER_INDEX)
        await self._db.commit()

        await self._startup_integrity_check()
        log.info(
            "Cache initialized: dir=%s db=%s max=%d MB",
            self._cache_dir, self._db_path, self._max_bytes // (1024 * 1024),
        )

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
        Write OGG bytes to the cache atomically under {cache_dir}/{provider_id}/.
        Uses .tmp -> rename pattern to prevent partially-written files.
        Triggers LRU eviction after storing if the cache is over the size limit.
        """
        assert self._db is not None

        # Ensure provider subdir exists
        provider_dir = self._cache_dir / provider_id
        provider_dir.mkdir(parents=True, exist_ok=True)

        filename   = f"{provider_id}/{key}.ogg"
        final_path = self._cache_dir / filename
        tmp_path   = provider_dir / f"{key}.ogg.tmp"

        # Write to .tmp first, then atomic rename
        tmp_path.write_bytes(ogg_bytes)
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

        log.debug("Cached %s/%s: %d bytes, %.2fs", provider_id, key, len(ogg_bytes), duration_sec)
        await self._evict_if_needed()

    def key_lock(self, key: str) -> asyncio.Lock:
        """
        Returns a per-key asyncio.Lock. Acquiring this before synthesis ensures
        that only one coroutine synthesizes a given key at a time; subsequent
        callers waiting on the lock will find a cache hit when they proceed.
        """
        if key not in self._key_locks:
            self._key_locks[key] = asyncio.Lock()
        return self._key_locks[key]

    # ── Tail token sidecar ────────────────────────────────────────────────────

    def _tail_token_path(self, key: str, provider_id: str) -> Path:
        """Path for the tail token sidecar: {cache_dir}/{provider_id}/{key}.tokens.pt"""
        return self._cache_dir / provider_id / f"{key}.tokens.pt"

    def store_tail_tokens(self, key: str, provider_id: str,
                          tokens: "torch.Tensor") -> None:
        """
        Persist the last N speech tokens from a synthesis alongside its OGG.
        Stored as a CPU tensor in a .tokens.pt sidecar file.
        Called synchronously from the worker thread after synthesis completes.
        Silent on failure — tail tokens are best-effort.
        """
        import torch
        try:
            p = self._tail_token_path(key, provider_id)
            if not p.parent.exists():
                p.parent.mkdir(parents=True, exist_ok=True)
            tmp = p.with_suffix(".pt.tmp")
            torch.save(tokens.detach().cpu(), tmp)
            tmp.rename(p)
            log.debug("Tail tokens stored: %s/%s (%d tokens)", provider_id, key[:12], tokens.shape[-1])
        except Exception as e:
            log.debug("Tail token store failed (non-fatal): %s", e)

    def load_tail_tokens(self, key: str, provider_id: str,
                         device: str = "cpu") -> "Optional[torch.Tensor]":
        """
        Load tail tokens for a cached segment. Returns tensor on `device` or None.
        Called synchronously from the worker thread during primed synthesis.
        """
        import torch
        p = self._tail_token_path(key, provider_id)
        if not p.exists():
            return None
        try:
            tokens = torch.load(p, map_location=device, weights_only=True)
            log.debug("Tail tokens loaded: %s/%s (%d tokens)", provider_id, key[:12], tokens.shape[-1])
            return tokens
        except Exception as e:
            log.debug("Tail token load failed (non-fatal): %s", e)
            return None

    async def stats(self) -> dict:
        """Return cache statistics for the health/diagnostics endpoint."""
        assert self._db is not None

        async with self._db.execute(
            "SELECT COUNT(*) as cnt, SUM(size_bytes) as total FROM cache_manifest"
        ) as cur:
            row = await cur.fetchone()

        count = row["cnt"] or 0
        total = row["total"] or 0

        # Per-provider breakdown
        provider_stats = {}
        async with self._db.execute(
            """
            SELECT provider_id, COUNT(*) as cnt, SUM(size_bytes) as total
            FROM cache_manifest
            GROUP BY provider_id
            """
        ) as cur:
            async for prow in cur:
                provider_stats[prow["provider_id"]] = {
                    "entry_count": prow["cnt"],
                    "total_mb":    round((prow["total"] or 0) / (1024 * 1024), 2),
                }

        return {
            "entry_count":    count,
            "total_bytes":    total,
            "total_mb":       round(total / (1024 * 1024), 2),
            "max_mb":         self._max_bytes // (1024 * 1024),
            "by_provider":    provider_stats,
        }

    async def clear_provider(self, provider_id: str) -> int:
        """
        Delete all cached OGG files and manifest rows for a single provider.
        Returns the number of entries cleared.
        Equivalent to: rm -rf {cache_dir}/{provider_id}/
        """
        assert self._db is not None

        async with self._db.execute(
            "SELECT key, filename FROM cache_manifest WHERE provider_id = ?", (provider_id,)
        ) as cur:
            rows = await cur.fetchall()

        count = 0
        for row in rows:
            path = self._cache_dir / row["filename"]
            path.unlink(missing_ok=True)
            count += 1

        await self._db.execute(
            "DELETE FROM cache_manifest WHERE provider_id = ?", (provider_id,)
        )
        await self._db.commit()

        # Remove the now-empty provider subdir if it exists
        provider_dir = self._cache_dir / provider_id
        try:
            provider_dir.rmdir()
        except OSError:
            pass  # not empty or doesn't exist — fine either way

        log.info("Cleared %d cache entries for provider '%s'", count, provider_id)
        return count

    # ── Startup integrity ─────────────────────────────────────────────────────

    async def _startup_integrity_check(self) -> None:
        assert self._db is not None

        # 1. Delete leftover .tmp files from interrupted writes (all subdirs)
        tmp_files = list(self._cache_dir.rglob("*.ogg.tmp"))
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

CACHE_KEY_SCHEMA_VERSION = "L2V1"


def _norm_text(value: str | None) -> str:
    return value or ""


def _norm_id(value: str | None) -> str:
    return (value or "").strip().lower()


def _norm_str(value: str | None, *, lower: bool = False) -> str:
    if value is None:
        return ""
    normalized = value.strip()
    if not normalized:
        return ""
    return normalized.lower() if lower else normalized


def _norm_float(value: float | None, decimals: int) -> str:
    if value is None:
        return ""
    return f"{value:.{decimals}f}"


def _norm_int(value: int | None) -> str:
    return "" if value is None else str(value)


def _norm_bool(value: bool | None) -> str:
    return "" if value is None else ("1" if value else "0")


def compute_cache_key(
    text: str,
    provider_id: str,
    voice_identity: str,
    lang_code: str,
    speech_rate: float,
    cfg_weight: float | None = None,
    exaggeration: float | None = None,
    cfg_strength: float | None = None,
    nfe_step: int | None = None,
    cross_fade_duration: float | None = None,
    sway_sampling_coef: float | None = None,
    voice_context: str | None = None,
    voice_instruct: str | None = None,
    cosy_instruct: str | None = None,
    synthesis_seed: int | None = None,
    cb_temperature: float | None = None,
    cb_top_p: float | None = None,
    cb_repetition_penalty: float | None = None,
    longcat_steps: int | None = None,
    longcat_cfg_strength: float | None = None,
    longcat_guidance: str | None = None,
    lux_num_steps: int | None = None,
    lux_t_shift: float | None = None,
    lux_return_smooth: bool | None = None,
) -> str:
    """Compute the 32-char hex server cache key from canonicalized render identity."""
    parts = [
        CACHE_KEY_SCHEMA_VERSION,
        _norm_text(text),
        _norm_id(provider_id),
        _norm_id(voice_identity),
        _norm_id(lang_code),
        _norm_float(speech_rate, 2),
        _norm_float(cfg_weight, 2),
        _norm_float(exaggeration, 2),
        _norm_float(cfg_strength, 2),
        _norm_int(nfe_step),
        _norm_float(cross_fade_duration, 3),
        _norm_float(sway_sampling_coef, 3),
        _norm_str(voice_context, lower=True),
        _norm_str(voice_instruct),
        _norm_str(cosy_instruct),
        _norm_int(synthesis_seed),
        _norm_float(cb_temperature, 2),
        _norm_float(cb_top_p, 2),
        _norm_float(cb_repetition_penalty, 2),
        _norm_int(longcat_steps),
        _norm_float(longcat_cfg_strength, 2),
        _norm_str(longcat_guidance, lower=True),
        _norm_int(lux_num_steps),
        _norm_float(lux_t_shift, 2),
        _norm_bool(lux_return_smooth),
    ]
    joined = "\x00".join(parts)
    digest = hashlib.sha256(joined.encode("utf-8")).hexdigest()
    return digest[:32]



def compose_server_cache_key(client_cache_key: str, asset_fingerprint: str | None) -> str:
    suffix_raw = (asset_fingerprint or "nosample").strip().lower() or "nosample"
    if suffix_raw != "nosample" and not all(ch in "0123456789abcdef" for ch in suffix_raw):
        suffix = hashlib.sha256(suffix_raw.encode("utf-8")).hexdigest()[:16]
    else:
        suffix = suffix_raw
    return f"{client_cache_key.strip().lower()}.{suffix}"

def blend_voice_identity(blend: list[dict]) -> str:
    """
    Canonical voice identity string for a blend request.
    Supports both base-voice blends (voice_id key) and sample blends (sample_id key).
    Sorts by id for stability, normalizes weights to 2 decimal places.
    Example voice blend: "am_adam:0.40|bm_lewis:0.60"
    Example sample blend: "M_Dwarf_1:0.30|M_Narrator:0.70"
    """
    pairs = sorted(
        f"{(entry.get('voice_id') or entry.get('sample_id', 'unknown')).strip().lower()}:{entry['weight']:.2f}"
        for entry in blend
    )
    return "|".join(pairs)
