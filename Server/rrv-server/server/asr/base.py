# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr/base.py
#
# Abstract base class and data types for ASR (Automatic Speech Recognition)
# providers. ASR providers are the inverse of TTS providers: they take audio
# in and return text out. They participate in the same worker subprocess
# architecture as TTS backends.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


@dataclass
class TranscriptionRequest:
    """Request for an ASR provider to transcribe audio."""
    audio_path: Path
    language_hint: str = "en"
    return_timestamps: bool = False   # word-level timestamps in chunks


@dataclass
class TranscriptionChunk:
    """A single word or phrase chunk with optional timing."""
    text: str
    start: Optional[float] = None   # seconds from audio start
    end: Optional[float] = None     # seconds from audio start


@dataclass
class TranscriptionResult:
    """Result from an ASR provider."""
    text: str                                   # full transcript
    language: str = "en"                        # detected language
    chunks: list[TranscriptionChunk] = field(default_factory=list)

    def is_empty(self) -> bool:
        return not self.text or not self.text.strip()


class AbstractAsrProvider(ABC):
    """
    Base class for all ASR providers.

    ASR providers run as isolated worker subprocesses (like TTS backends)
    or in-process (like the built-in Whisper fallback). They receive an
    audio file path and return a TranscriptionResult.
    """

    @property
    @abstractmethod
    def provider_id(self) -> str:
        """Unique identifier for this ASR provider (e.g. 'whisper', 'qwen_asr')."""

    @property
    @abstractmethod
    def display_name(self) -> str:
        """Human-readable name shown in logs and health endpoints."""

    @property
    def requires_gpu(self) -> bool:
        """True if this provider benefits from / requires GPU. Used for resource management."""
        return False

    @property
    def is_loaded(self) -> bool:
        """True if the provider is currently ready to accept requests."""
        return True

    @abstractmethod
    async def load(self) -> None:
        """Load the provider. Called once at startup or on-demand reload."""

    @abstractmethod
    async def transcribe(self, request: TranscriptionRequest) -> TranscriptionResult:
        """Transcribe the audio file described by request."""

    def shutdown(self) -> None:
        """Shut down the provider. Default no-op for in-process providers."""
