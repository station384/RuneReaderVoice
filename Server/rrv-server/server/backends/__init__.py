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
# server/backends/__init__.py
#
# BackendRegistry: holds all loaded backends and provides lookup by provider_id.
# load_backends(): instantiates and loads the backends listed in config.backends.

from __future__ import annotations

import logging
from typing import Optional

from .base import AbstractTtsBackend
from .worker_backend import WorkerBackend
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
    settings=None,
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
            backend = _create_backend(name, Path(models_dir), gpu, settings)
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


def _create_backend(name: str, models_dir, gpu: GpuInfo, settings=None) -> AbstractTtsBackend:
    """Instantiate the backend class for the given name.

    If settings.worker_venvs contains an entry for this backend name, a
    WorkerBackend proxy is returned instead of a direct class instance.
    The proxy spawns the backend as an isolated subprocess in its own venv.
    Fall-through: backends with no venv configured are loaded in-process as before.
    """
    # Worker venv check — highest priority
    worker_venvs: dict = getattr(settings, "worker_venvs", {})
    if name in worker_venvs:
        chatterbox_max_concurrent = getattr(settings, "chatterbox_max_concurrent", 2)
        log_level = getattr(settings, "log_level", "info")
        log.info("Backend '%s' configured as worker subprocess (venv: %s)", name, worker_venvs[name])
        samples_dir = getattr(settings, "samples_dir", models_dir.parent / "samples")
        # Qwen backends need to know which model size to load
        qwen_size = "large"
        if name == "qwen_natural":
            qwen_size = getattr(settings, "qwen_natural_size", "large")
        elif name == "qwen_custom":
            qwen_size = getattr(settings, "qwen_custom_size", "large")
        return WorkerBackend(
            backend_name=name,
            venv_path=worker_venvs[name],
            models_dir=models_dir,
            samples_dir=samples_dir,
            gpu=getattr(settings, "gpu", "auto"),
            max_concurrent=chatterbox_max_concurrent,
            log_level=log_level,
            qwen_size=qwen_size,
        )

    chatterbox_max_concurrent = getattr(settings, "chatterbox_max_concurrent", 2)

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
            max_concurrent=chatterbox_max_concurrent,
        )

    elif name == "chatterbox_full":
        from .chatterbox_full_backend import ChatterboxFullBackend
        return ChatterboxFullBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
            max_concurrent=chatterbox_max_concurrent,
        )

    elif name == "chatterbox_multilingual":
        from .chatterbox_multilingual_backend import ChatterboxMultilingualBackend
        return ChatterboxMultilingualBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
            # Multilingual model does not support concurrent synthesis safely —
            # always serial regardless of RRV_CHATTERBOX_MAX_CONCURRENT.
            max_concurrent=1,
        )

    elif name == "qwen_natural":
        from .qwen_backend import QwenNaturalBackend
        qwen_size = getattr(settings, "qwen_natural_size", "large")
        return QwenNaturalBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
            size=qwen_size,
        )

    elif name == "qwen_custom":
        from .qwen_backend import QwenCustomBackend
        qwen_size = getattr(settings, "qwen_custom_size", "large")
        return QwenCustomBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
            size=qwen_size,
        )

    elif name == "qwen_design":
        from .qwen_backend import QwenDesignBackend
        return QwenDesignBackend(
            models_dir=models_dir,
            torch_device=gpu.torch_device,
        )

    else:
        raise ValueError(f"Unknown backend name: {name!r}")
