# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/__init__.py
#
# AsrRegistry: holds the active ASR provider.
# load_asr_provider(): instantiates and loads the configured ASR provider.

from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

from .base import AbstractAsrProvider, TranscriptionRequest, TranscriptionResult

log = logging.getLogger(__name__)


class AsrRegistry:
    """
    Holds the active ASR provider. At most one provider is active at a time.
    Falls back gracefully if the configured provider fails to load.
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
    Falls back to the built-in Whisper provider if the configured one fails.
    Returns an AsrRegistry with the loaded provider (or empty if all fail).
    """
    registry = AsrRegistry()

    # Try the configured provider first
    try:
        provider = _create_asr_provider(
            provider_name, models_dir, whisper_model_dir, gpu, settings
        )
        await provider.load()
        registry.register(provider)
        return registry
    except Exception as e:
        log.error(
            "ASR provider '%s' failed to load: %s", provider_name, e
        )

    # Fall back to whisper if the configured provider wasn't already whisper
    if provider_name != "whisper":
        log.warning("Falling back to built-in Whisper ASR provider")
        try:
            provider = _create_asr_provider(
                "whisper", models_dir, whisper_model_dir, gpu, settings
            )
            await provider.load()
            registry.register(provider)
            return registry
        except Exception as e:
            log.error("Whisper fallback also failed: %s", e)

    log.warning(
        "No ASR provider loaded — auto-transcription will be unavailable. "
        "Check model files and venv dependencies."
    )
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
        from .whisper_asr import WhisperAsrProvider
        return WhisperAsrProvider(whisper_model_dir=whisper_model_dir)

    # Worker-subprocess providers
    worker_venvs: dict = getattr(settings, "asr_worker_venvs", {})

    if name == "qwen_asr":
        venv = worker_venvs.get("qwen_asr")
        if not venv:
            # Try default path
            venv = models_dir.parent.parent / "rrv-qwen-asr" / ".venv"
        from .worker_asr import WorkerAsr
        log.info("ASR provider 'qwen_asr' configured as worker subprocess (venv: %s)", venv)
        return WorkerAsr(
            provider_name="qwen_asr",
            venv_path=Path(venv),
            models_dir=models_dir,
            gpu=gpu_str,
            log_level=log_level,
        )

    elif name == "crisper_whisper":
        venv = worker_venvs.get("crisper_whisper")
        if not venv:
            venv = models_dir.parent.parent / "rrv-crisper-whisper" / ".venv"
        from .worker_asr import WorkerAsr
        log.info("ASR provider 'crisper_whisper' configured as worker subprocess (venv: %s)", venv)
        return WorkerAsr(
            provider_name="crisper_whisper",
            venv_path=Path(venv),
            models_dir=models_dir,
            gpu=gpu_str,
            log_level=log_level,
        )

    elif name == "cohere_transcribe":
        venv = worker_venvs.get("cohere_transcribe")
        if not venv:
            venv = models_dir.parent.parent / "rrv-cohere-transcribe" / ".venv"
        from .worker_asr import WorkerAsr
        log.info("ASR provider 'cohere_transcribe' configured as worker subprocess (venv: %s)", venv)
        return WorkerAsr(
            provider_name="cohere_transcribe",
            venv_path=Path(venv),
            models_dir=models_dir,
            gpu=gpu_str,
            log_level=log_level,
        )

    else:
        raise ValueError(f"Unknown ASR provider name: {name!r}")
