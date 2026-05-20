# SPDX-License-Identifier: GPL-3.0-or-later
"""DB-backed sample index for provider sample lists.

Rules:
- GET /providers/{id}/samples must read SQLite only.
- Filesystem reconciliation runs in background and incrementally scans changed
  sample directories.
- Provider IDs are normalized to provider clip keys: f5, chatterbox,
  cosyvoice, lux, longcat. Providers without a provider-specific clip key use
  base rows.
"""

from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional

import aiosqlite

from .samples import (
    VALID_EXTENSIONS,
    SampleInfo,
    VARIANT_SUFFIXES,
    _INTERNAL_SUFFIX_RE,
    _VALID_STEM_RE,
    _base_stem,
    _load_sidecar,
    _provider_clip_search_paths,
    _provider_suffix,
    _read_duration,
    _scan_directory,
)

log = logging.getLogger(__name__)

_PROVIDER_KEYS = ("base", "f5", "chatterbox", "cosyvoice", "lux", "longcat")
_ROOT_KEY = "__root__"

_SCHEMA = """
CREATE TABLE IF NOT EXISTS sample_index (
    sample_id            TEXT NOT NULL,
    provider_key         TEXT NOT NULL,
    filename             TEXT NOT NULL DEFAULT '',
    sample_dir           TEXT NOT NULL DEFAULT '',
    audio_path           TEXT NOT NULL DEFAULT '',
    provider_audio_path  TEXT NOT NULL DEFAULT '',
    description          TEXT NOT NULL DEFAULT '',
    ref_text             TEXT NOT NULL DEFAULT '',
    duration_seconds     REAL NOT NULL DEFAULT 0,
    file_mtime           REAL NOT NULL DEFAULT 0,
    file_size            INTEGER NOT NULL DEFAULT 0,
    indexed_at           REAL NOT NULL,
    PRIMARY KEY (sample_id, provider_key)
);

CREATE TABLE IF NOT EXISTS sample_index_dirs (
    dir_key              TEXT PRIMARY KEY,
    signature            TEXT NOT NULL,
    indexed_at           REAL NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_sample_index_provider
    ON sample_index(provider_key, sample_id);
CREATE INDEX IF NOT EXISTS idx_sample_index_sample_dir
    ON sample_index(sample_dir);
"""


@dataclass(frozen=True)
class SampleIndexRow:
    sample_id: str
    filename: str
    duration_seconds: float
    description: str = ""
    ref_text: str = ""


class SampleIndex:
    def __init__(self, db_path: Path, samples_dir: Path) -> None:
        self._db_path = db_path
        self._samples_dir = samples_dir
        self._conn: Optional[aiosqlite.Connection] = None
        self._lock = asyncio.Lock()
        self._refresh_task: Optional[asyncio.Task] = None

    async def initialize(self) -> None:
        self._db_path.parent.mkdir(parents=True, exist_ok=True)
        self._conn = await aiosqlite.connect(str(self._db_path))
        self._conn.row_factory = aiosqlite.Row
        await self._conn.executescript(_SCHEMA)
        await self._conn.commit()

    async def close(self) -> None:
        if self._refresh_task and not self._refresh_task.done():
            self._refresh_task.cancel()
            try:
                await self._refresh_task
            except asyncio.CancelledError:
                pass
        if self._conn is not None:
            await self._conn.close()
            self._conn = None

    def schedule_reconcile(self) -> None:
        """Start one background reconcile if one is not already running."""
        if self._refresh_task and not self._refresh_task.done():
            return
        self._refresh_task = asyncio.create_task(self.reconcile_changed())

    async def reconcile_changed(self) -> int:
        """Incrementally refresh changed sample dirs. Returns changed dir count."""
        async with self._lock:
            loop = asyncio.get_event_loop()
            dirs = await loop.run_in_executor(None, self._collect_dir_signatures_sync)
            return await self._apply_dir_signatures(dirs)

    async def list_for_provider(self, provider_id: str) -> list[SampleIndexRow]:
        conn = self._require_conn()
        provider_key = _provider_suffix(provider_id) or "base"
        rows = await conn.execute_fetchall(
            """
            SELECT sample_id, filename, duration_seconds, description, ref_text
            FROM sample_index
            WHERE provider_key = ?
            ORDER BY sample_id COLLATE NOCASE
            """,
            (provider_key,),
        )
        if rows or provider_key == "base":
            return [self._row_to_sample(r) for r in rows]

        # Provider-specific rows absent (first boot or provider added). Fall back
        # to base indexed rows, still DB-only, never filesystem scan in request path.
        base_rows = await conn.execute_fetchall(
            """
            SELECT sample_id, filename, duration_seconds, description, ref_text
            FROM sample_index
            WHERE provider_key = 'base'
            ORDER BY sample_id COLLATE NOCASE
            """
        )
        return [self._row_to_sample(r) for r in base_rows]

    async def count_rows(self) -> int:
        conn = self._require_conn()
        async with conn.execute("SELECT COUNT(*) AS c FROM sample_index") as cur:
            row = await cur.fetchone()
        return int(row["c"] if row else 0)

    def _require_conn(self) -> aiosqlite.Connection:
        if self._conn is None:
            raise RuntimeError("SampleIndex is not initialized")
        return self._conn

    @staticmethod
    def _row_to_sample(row: aiosqlite.Row) -> SampleIndexRow:
        return SampleIndexRow(
            sample_id=str(row["sample_id"]),
            filename=str(row["filename"] or ""),
            duration_seconds=float(row["duration_seconds"] or 0),
            description=str(row["description"] or ""),
            ref_text=str(row["ref_text"] or ""),
        )

    def _collect_dir_signatures_sync(self) -> dict[str, str]:
        """Cheap stat-only pass over top-level sample dirs and root files."""
        if not self._samples_dir.exists():
            return {}

        result: dict[str, str] = {}
        root_parts: list[str] = []
        try:
            entries = sorted(self._samples_dir.iterdir(), key=lambda p: p.name.lower())
        except Exception as exc:
            log.warning("sample_index: cannot list samples dir %s: %s", self._samples_dir, exc)
            return {}

        for entry in entries:
            if entry.name == "originals":
                continue
            try:
                if entry.is_dir():
                    result[entry.name] = self._signature_for_dir(entry)
                elif entry.is_file():
                    st = entry.stat()
                    root_parts.append(f"{entry.name}:{st.st_mtime_ns}:{st.st_size}")
            except FileNotFoundError:
                continue
            except Exception as exc:
                log.debug("sample_index: stat failed for %s: %s", entry, exc)

        if root_parts:
            result[_ROOT_KEY] = "|".join(root_parts)
        return result

    @staticmethod
    def _signature_for_dir(directory: Path) -> str:
        parts: list[str] = []
        try:
            entries = sorted(directory.iterdir(), key=lambda p: p.name.lower())
        except FileNotFoundError:
            return "missing"
        for path in entries:
            try:
                if not path.is_file():
                    continue
                st = path.stat()
                parts.append(f"{path.name}:{st.st_mtime_ns}:{st.st_size}")
            except FileNotFoundError:
                continue
        return "|".join(parts)

    async def _apply_dir_signatures(self, current: dict[str, str]) -> int:
        conn = self._require_conn()
        existing_rows = await conn.execute_fetchall("SELECT dir_key, signature FROM sample_index_dirs")
        existing = {str(r["dir_key"]): str(r["signature"] or "") for r in existing_rows}

        changed = [k for k, sig in current.items() if existing.get(k) != sig]
        removed = [k for k in existing.keys() if k not in current]

        if not changed and not removed:
            return 0

        now = time.time()
        for dir_key in removed:
            await conn.execute("DELETE FROM sample_index WHERE sample_dir = ?", (dir_key,))
            await conn.execute("DELETE FROM sample_index_dirs WHERE dir_key = ?", (dir_key,))

        loop = asyncio.get_event_loop()
        for dir_key in changed:
            rows = await loop.run_in_executor(None, self._build_rows_for_dir_sync, dir_key)
            await conn.execute("DELETE FROM sample_index WHERE sample_dir = ?", (dir_key,))
            for row in rows:
                await conn.execute(
                    """
                    INSERT OR REPLACE INTO sample_index
                        (sample_id, provider_key, filename, sample_dir,
                         audio_path, provider_audio_path, description, ref_text,
                         duration_seconds, file_mtime, file_size, indexed_at)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    """,
                    row,
                )
            await conn.execute(
                """
                INSERT OR REPLACE INTO sample_index_dirs(dir_key, signature, indexed_at)
                VALUES (?, ?, ?)
                """,
                (dir_key, current[dir_key], now),
            )

        await conn.commit()
        log.info(
            "sample_index: reconciled changed=%d removed=%d total_dirs=%d",
            len(changed), len(removed), len(current),
        )
        return len(changed) + len(removed)

    def _build_rows_for_dir_sync(self, dir_key: str) -> list[tuple]:
        if dir_key == _ROOT_KEY:
            infos = self._scan_flat_root_sync()
        else:
            directory = self._samples_dir / dir_key
            if not directory.exists() or not directory.is_dir():
                return []
            infos = _scan_directory(self._samples_dir, directory)

        rows: list[tuple] = []
        now = time.time()
        for info in infos:
            for provider_key in _PROVIDER_KEYS:
                adjusted = self._adjust_for_provider(info, provider_key)
                if adjusted is None:
                    continue
                audio_path, provider_audio_path, duration, description, ref_text = adjusted
                try:
                    st = audio_path.stat()
                    mtime = float(st.st_mtime)
                    size = int(st.st_size)
                except Exception:
                    mtime = 0.0
                    size = 0
                rows.append((
                    info.sample_id,
                    provider_key,
                    info.filename,
                    dir_key,
                    self._rel(audio_path),
                    self._rel(provider_audio_path) if provider_audio_path else "",
                    description,
                    ref_text,
                    float(round(duration, 2)),
                    mtime,
                    size,
                    now,
                ))
        return rows

    def _scan_flat_root_sync(self) -> list[SampleInfo]:
        results: list[SampleInfo] = []
        if not self._samples_dir.exists():
            return results
        for entry in sorted(self._samples_dir.iterdir(), key=lambda p: p.name.lower()):
            if not entry.is_file():
                continue
            if entry.suffix.lower() not in VALID_EXTENSIONS:
                continue
            stem = entry.stem
            if not _VALID_STEM_RE.match(stem):
                continue
            if _INTERNAL_SUFFIX_RE.search(stem):
                continue
            duration = _read_duration(entry)
            if duration is None:
                continue
            ref_text = _load_sidecar(entry, ".ref.txt")
            if not ref_text:
                continue
            results.append(SampleInfo(
                sample_id=stem,
                filename=entry.name,
                duration_seconds=round(duration, 2),
                description=_load_sidecar(entry, ".txt"),
                ref_text=ref_text,
            ))
        return results

    def _adjust_for_provider(self, info: SampleInfo, provider_key: str):
        audio_path = self._resolve_index_audio_path(info)
        if audio_path is None:
            return None

        duration = float(info.duration_seconds or 0)
        description = info.description or ""
        ref_text = info.ref_text or ""
        provider_audio_path: Optional[Path] = None

        if provider_key != "base":
            for candidate in _provider_clip_search_paths(self._samples_dir, info.sample_id, provider_key):
                if not candidate.exists():
                    continue
                clip_duration = _read_duration(candidate)
                if clip_duration is None:
                    continue
                provider_audio_path = candidate
                audio_path = candidate
                duration = float(clip_duration)
                description = _load_sidecar(candidate, ".txt") or description
                ref_text = _load_sidecar(candidate, ".ref.txt") or ref_text
                break

        if not ref_text:
            return None
        return audio_path, provider_audio_path, duration, description, ref_text

    def _resolve_index_audio_path(self, info: SampleInfo) -> Optional[Path]:
        filename = info.filename
        base = _base_stem(info.sample_id)
        candidates = [
            self._samples_dir / base / filename,
            self._samples_dir / filename,
        ]
        for c in candidates:
            if c.exists():
                return c
        return None

    def _rel(self, path: Path) -> str:
        try:
            return str(path.relative_to(self._samples_dir))
        except Exception:
            return str(path)
