# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/__init__.py
#
# AsrRegistry: holds the active ASR provider.
# load_asr_provider(): instantiates and loads the configured ASR provider.
#
# Supported providers:
#   whisper  — subprocess worker using rrv-whisper venv (default, recommended)

from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult

log = logging.getLogger(__name__)


class AsrRegistry:
    """
    Holds the active ASR provider. At most one provider is active at a time.
    """

    def __init__(self) -> None:
        self._provider: Optional[AbstractAsrProvider] = None

    def register(self, provider: AbstractAsrProvider) -> None:
        self._provider = provider
        log.info("ASR provider registered: %s (%s)", provider.provider_id, provider.display_name)

    @property
    def provider(self) -> Optional[AbstractAsrProvider]:
        return self._provider

    @property
    def available(self) -> bool:
        return self._provider is not None

    def shutdown(self) -> None:
        if self._provider is not None:
            try:
                self._provider.shutdown()
            except Exception as e:
                log.warning("Error shutting down ASR provider '%s': %s", self._provider.provider_id, e)
            self._provider = None


async def load_asr_provider(
    provider_name: str,
    models_dir: Path,
    whisper_model_dir: Path,
    gpu,
    settings=None,
) -> AsrRegistry:
    """
    Instantiate and load the configured ASR provider.
    Returns an AsrRegistry with the loaded provider (or empty if it fails).
    """
    registry = AsrRegistry()
    name = provider_name.lower().strip()

    try:
        provider = _create_asr_provider(name, models_dir, whisper_model_dir, gpu, settings)
        await provider.load()
        registry.register(provider)
    except Exception as e:
        log.error("ASR provider '%s' failed to load: %s", name, e)
        log.warning("Auto-transcription will be unavailable. Check model files and venv.")

    return registry


def _create_asr_provider(
    name: str,
    models_dir: Path,
    whisper_model_dir: Path,
    gpu,
    settings=None,
) -> AbstractAsrProvider:
    """Instantiate the ASR provider for the given name."""
    log_level = getattr(settings, "log_level", "info")
    gpu_str = getattr(gpu, "provider", "auto") if hasattr(gpu, "provider") else str(gpu)

    if name == "whisper":
        # Whisper as subprocess worker — load/transcribe/unload per batch
        # Uses rrv-whisper venv which has transformers + torch already installed
        venv_path = _resolve_venv(name, models_dir, settings, "rrv-whisper")
        from .worker_asr import WorkerAsr
        log.info("ASR provider 'whisper' configured as worker subprocess (venv: %s)", venv_path)
        return WorkerAsr(
            provider_name="whisper",
            venv_path=venv_path,
            models_dir=models_dir,
            gpu=gpu_str,
            log_level=log_level,
            extra_args=["--model-dir", str(whisper_model_dir)],
        )

    raise ValueError(
        f"Unknown ASR provider: {name!r}. "
        f"Currently supported: whisper"
    )


def _resolve_venv(name: str, models_dir: Path, settings, default_dir: str) -> Path:
    """Resolve venv path from settings or fall back to default relative path."""
    worker_venvs: dict = getattr(settings, "asr_worker_venvs", {})
    venv = worker_venvs.get(name)
    if venv:
        return Path(venv)
    # Default: sibling directory of rrv-server
    return models_dir.parent.parent / default_dir / ".venv"
