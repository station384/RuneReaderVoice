# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/manager.py
#
# ResourceManager — unified load/unload/eviction for TTS backends and ASR providers.
#
# All GPU-resident workers (TTS backends and ASR providers) register with the
# manager. Before loading, a worker calls request_load() which evicts the
# least-recently-used eligible worker if needed to free resources.
#
# Eviction policy:
#   - Only GPU-requiring workers are candidates for eviction
#   - A worker is eligible if it was last used more than RECENT_USE_WINDOW seconds ago
#   - Eviction order: least-recently-used first
#   - Workers currently handling a request are never evicted (protected by lock)
#
# Usage tracking:
#   - on_used(worker_id) called after each synthesis/transcription
#   - Workers that haven't been used recently can be evicted
#
# On-demand reload:
#   - When a request arrives for an unloaded worker, request_load() is called
#   - Manager evicts if needed, then the worker reloads itself
#   - Reload adds startup latency (model load time) for cold workers

from __future__ import annotations

import asyncio
import logging
import time
from typing import Optional, Protocol, runtime_checkable

log = logging.getLogger(__name__)


@runtime_checkable
class ManagedResource(Protocol):
    """Interface that all managed GPU resources must implement."""

    @property
    def resource_id(self) -> str:
        """Unique identifier for this resource."""
        ...

    @property
    def requires_gpu(self) -> bool:
        """True if this resource uses GPU memory."""
        ...

    @property
    def is_loaded(self) -> bool:
        """True if currently loaded and consuming GPU memory."""
        ...

    @property
    def last_used(self) -> float:
        """monotonic timestamp of last use. 0.0 if never used."""
        ...

    @property
    def use_count(self) -> int:
        """Total number of requests served."""
        ...

    async def load(self) -> None:
        """Load the resource (spawn worker, load model, etc.)."""
        ...

    async def unload(self) -> None:
        """Unload the resource to free GPU memory."""
        ...


class ResourceManager:
    """
    Manages GPU resource contention between TTS backends and ASR providers.

    Workers register themselves on startup. Before loading, they call
    request_load() which evicts the least-recently-used eligible worker
    if VRAM is needed.

    Configuration (via environment variables, read at construction):
      RRV_BACKEND_RECENT_USE_WINDOW  — seconds a worker is immune to eviction
                                       after last use (default: 60)
    """

    def __init__(self, recent_use_window: float = 60.0) -> None:
        self._resources: dict[str, ManagedResource] = {}
        self._recent_use_window = recent_use_window
        self._lock = asyncio.Lock()

    def register(self, resource: ManagedResource) -> None:
        """Register a managed resource. Called at startup for each backend/provider."""
        self._resources[resource.resource_id] = resource
        log.debug(
            "ResourceManager: registered '%s' (requires_gpu=%s)",
            resource.resource_id, resource.requires_gpu
        )

    async def request_load(self, resource: ManagedResource) -> None:
        """
        Called before a resource loads itself. Evicts the least-recently-used
        eligible GPU resource if needed to make room.

        If the resource doesn't require GPU, returns immediately.
        If the resource is already loaded, returns immediately.
        """
        if not resource.requires_gpu:
            return
        if resource.is_loaded:
            return

        async with self._lock:
            await self._evict_if_needed(resource.resource_id)

    async def _evict_if_needed(self, requesting_id: str) -> None:
        """
        Evict all eligible idle GPU resources to make room for the requesting
        resource. Evicts in order: biggest VRAM consumer first, stopping only
        when no more eligible candidates remain.

        We evict all eligible rather than guessing how much VRAM is needed —
        the requesting backend will fail to load if there isn't enough room
        regardless, and keeping idle backends loaded serves no purpose.
        """
        now = time.monotonic()

        # Find all loaded GPU resources that are not the requester
        candidates = [
            r for r in self._resources.values()
            if r.resource_id != requesting_id
            and r.requires_gpu
            and r.is_loaded
        ]

        if not candidates:
            log.debug(
                "ResourceManager: no eviction candidates for '%s'", requesting_id
            )
            return

        # Filter: must not have been used within the recent-use window
        eligible = [
            r for r in candidates
            if (now - r.last_used) >= self._recent_use_window
        ]

        if not eligible:
            log.warning(
                "ResourceManager: '%s' needs to load but all %d loaded GPU resource(s) "
                "were used within the last %.0fs — cannot evict safely. "
                "Consider increasing RRV_BACKEND_RECENT_USE_WINDOW or "
                "reducing gpu_memory_utilization for the new backend.",
                requesting_id, len(candidates), self._recent_use_window
            )
            return

        # Sort: biggest VRAM consumer first so the most impactful eviction
        # happens first. Ties broken by least recently used.
        def _eviction_key(r):
            vram = getattr(r, "vram_used_mib", 0.0)
            return (-vram, r.last_used, r.use_count)
        eligible.sort(key=_eviction_key)

        log.info(
            "ResourceManager: evicting %d resource(s) to make room for '%s': %s",
            len(eligible),
            requesting_id,
            ", ".join(
                f"{r.resource_id}({getattr(r, 'vram_used_mib', 0.0):.0f}MiB)"
                for r in eligible
            ),
        )

        for victim in eligible:
            victim_vram = getattr(victim, "vram_used_mib", 0.0)
            idle_secs = now - victim.last_used
            log.info(
                "ResourceManager: evicting '%s' (idle=%.0fs, use_count=%d, vram=%.0fMiB)",
                victim.resource_id, idle_secs, victim.use_count, victim_vram,
            )
            try:
                await victim.unload()
                log.info("ResourceManager: '%s' evicted successfully", victim.resource_id)
            except Exception as e:
                log.error("ResourceManager: failed to evict '%s': %s", victim.resource_id, e)

    def on_used(self, resource_id: str) -> None:
        """
        Called after a resource successfully handles a request.
        Updates the last_used timestamp — done inside the resource itself,
        this method exists for any future manager-level tracking.
        """
        # Currently a no-op at manager level — resources track their own last_used.
        # Kept as a hook for future metrics/logging.
        pass

    def status(self) -> list[dict]:
        """Return current status of all registered resources."""
        now = time.monotonic()
        result = []
        for r in self._resources.values():
            idle = now - r.last_used if r.last_used > 0 else None
            result.append({
                "resource_id":  r.resource_id,
                "requires_gpu": r.requires_gpu,
                "is_loaded":    r.is_loaded,
                "use_count":    r.use_count,
                "idle_secs":    round(idle, 1) if idle is not None else None,
            })
        return sorted(result, key=lambda x: x["resource_id"])


def create_manager(settings=None) -> ResourceManager:
    """Create a ResourceManager from settings/env vars."""
    if settings is not None and hasattr(settings, "backend_recent_use_window"):
        recent_use_window = settings.backend_recent_use_window
    else:
        import os
        recent_use_window = float(os.environ.get("RRV_BACKEND_RECENT_USE_WINDOW", "60"))
    log.info("ResourceManager: recent_use_window=%.0fs", recent_use_window)
    return ResourceManager(recent_use_window=recent_use_window)
