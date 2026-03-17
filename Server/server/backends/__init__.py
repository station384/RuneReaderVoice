# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/__init__.py
#
# BackendRegistry: holds all loaded backends and provides lookup by provider_id.
# load_backends(): instantiates and loads the backends listed in config.backends.

from __future__ import annotations

import logging
from typing import Optional

from .base import AbstractTtsBackend
from ..gpu_detect import GpuInfo

log = logging.getLogger(__name__)


class BackendRegistry:
    """
    Holds all successfully loaded TTS backends.
    Backends that fail to load are logged and excluded — the server
    starts with whatever backends did load rather than refusing to start.
    """

    def __init__(self) -> None:
        self._backends: dict[str, AbstractTtsBackend] = {}

    def register(self, backend: AbstractTtsBackend) -> None:
        self._backends[backend.provider_id] = backend
        log.info("Backend registered: %s (%s)", backend.provider_id, backend.display_name)

    def get(self, provider_id: str) -> Optional[AbstractTtsBackend]:
        return self._backends.get(provider_id)

    def all(self) -> list[AbstractTtsBackend]:
        return list(self._backends.values())

    def provider_ids(self) -> list[str]:
        return list(self._backends.keys())

    def __len__(self) -> int:
        return len(self._backends)


async def load_backends(
    backend_names: frozenset[str],
    models_dir,
    gpu: GpuInfo,
) -> BackendRegistry:
    """
    Instantiate and load each requested backend.
    Backends that fail to load are logged as errors but do not prevent
    other backends from loading or the server from starting.
    Returns a BackendRegistry containing only the successfully loaded backends.
    """
    from pathlib import Path

    registry = BackendRegistry()

    for name in sorted(backend_names):
        try:
            backend = _create_backend(name, Path(models_dir), gpu)
            await backend.load()
            registry.register(backend)
        except RuntimeError as e:
            log.error("Backend '%s' failed to load: %s", name, e)
        except Exception as e:
            log.exception("Backend '%s' raised unexpected error during load: %s", name, e)

    if len(registry) == 0:
        log.warning(
            "No backends loaded successfully. "
            "The server will start but cannot synthesize audio. "
            "Check model files and dependencies."
        )
    else:
        log.info(
            "Loaded %d backend(s): %s",
            len(registry),
            ", ".join(registry.provider_ids()),
        )

    return registry


def _create_backend(name: str, models_dir, gpu: GpuInfo) -> AbstractTtsBackend:
    """Instantiate the backend class for the given name."""
    if name == "kokoro":
        from .kokoro_backend import KokoroBackend
        return KokoroBackend(
            models_dir=models_dir,
            ort_providers=gpu.ort_providers,
        )

    elif name == "f5tts":
        from .f5tts_backend import F5TtsBackend
        return F5TtsBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
        )

    elif name == "chatterbox":
        from .chatterbox_backend import ChatterboxBackend
        return ChatterboxBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
        )

    elif name == "chatterbox_full":
        from .chatterbox_full_backend import ChatterboxFullBackend
        return ChatterboxFullBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
        )

    else:
        raise ValueError(f"Unknown backend name: {name!r}")
