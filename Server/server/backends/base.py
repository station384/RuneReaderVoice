# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/base.py
#
# AbstractTtsBackend: the protocol all TTS backends must implement.
# Backends are loaded by BackendRegistry in __init__.py.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


@dataclass
class VoiceInfo:
    voice_id:     str
    display_name: str
    language:     str
    gender:       str   # "male" | "female" | "neutral" | "unknown"
    type:         str   # "base" | "reference" | "blend"


@dataclass
class SynthesisRequest:
    """
    Unified synthesis request — same shape regardless of provider.
    Only the fields relevant to the voice type need to be populated.
    """
    text:        str
    lang_code:   str          = "en"
    speech_rate: float        = 1.0

    # voice.type = "base"
    voice_id:    Optional[str] = None

    # voice.type = "reference"
    sample_path:  Optional[Path] = None   # resolved by the route from sample_id
    sample_id:    Optional[str]  = None   # original sample_id (stem), for provider-specific lookup
    samples_dir:  Optional[Path] = None   # samples directory root, for provider-specific lookup
    ref_text:     str            = ""     # transcript of reference clip

    # voice.type = "blend"
    blend:        list[dict]   = field(default_factory=list)
    # blend entries: [{"voice_id": "am_adam", "weight": 0.4}, ...]

    # provider-specific optional controls
    cfg_weight:         Optional[float] = None
    exaggeration:       Optional[float] = None
    cfg_strength:       Optional[float] = None   # F5-TTS: reference adherence (default 2.0)
    nfe_step:           Optional[int]   = None   # F5-TTS: ODE solver steps (default 32)
    cross_fade_duration: Optional[float] = None  # F5-TTS: internal chunk stitch duration (default 0.15)


@dataclass
class SynthesisResult:
    ogg_bytes:    bytes
    duration_sec: float


class AbstractTtsBackend(ABC):
    """
    Base class for all TTS backends.
    Each backend is instantiated once and reused for the server lifetime.
    """

    @property
    @abstractmethod
    def provider_id(self) -> str:
        """Stable identifier string, e.g. 'kokoro', 'f5tts', 'chatterbox'."""
        ...

    @property
    @abstractmethod
    def display_name(self) -> str:
        """Human-readable name shown in capability responses."""
        ...

    @property
    @abstractmethod
    def supports_base_voices(self) -> bool:
        """True if the backend has built-in named voices."""
        ...

    @property
    @abstractmethod
    def supports_voice_matching(self) -> bool:
        """True if the backend can clone from a reference audio clip."""
        ...

    @property
    @abstractmethod
    def supports_voice_blending(self) -> bool:
        """True if the backend supports weighted voice blends."""
        ...

    @property
    @abstractmethod
    def supports_inline_pronunciation(self) -> bool:
        """True if the backend supports Kokoro/Misaki IPA phoneme markup."""
        ...

    @property
    @abstractmethod
    def languages(self) -> list[str]:
        """ISO language codes this backend can synthesize."""
        ...

    @property
    @abstractmethod
    def model_version(self) -> str:
        """
        Short hash derived from the primary model file contents (8 hex chars).
        Used as a component of the cache key. Computed at load time.
        """
        ...

    @abstractmethod
    async def load(self) -> None:
        """
        Load model weights and prepare for synthesis.
        Called once at startup. Must be idempotent.
        Raise RuntimeError with a clear message if required model files are missing.
        """
        ...

    @abstractmethod
    def get_voices(self) -> list[VoiceInfo]:
        """
        Return the voices loaded and available right now.
        This is an operational snapshot, not a theoretical catalog.
        """
        ...

    @abstractmethod
    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        """
        Synthesize text and return an OGG audio result.
        The backend must validate that the request's voice type is supported
        and raise ValueError with a clear message if not.
        """
        ...

    def extra_controls(self) -> dict:
        """
        Optional provider-specific user-tunable controls exposed via /providers.
        Backends that do not support extra controls return an empty dict.
        """
        return {}

    def capability_dict(self, execution_provider: str) -> dict:
        """
        Serialize capabilities to the API response shape.
        All backends inherit this implementation.
        """
        data = {
            "provider_id":                self.provider_id,
            "display_name":               self.display_name,
            "loaded":                     True,
            "execution_provider":         execution_provider,
            "supports_base_voices":       self.supports_base_voices,
            "supports_voice_matching":    self.supports_voice_matching,
            "supports_voice_blending":    self.supports_voice_blending,
            "supports_inline_pronunciation": self.supports_inline_pronunciation,
            "languages":                  self.languages,
        }
        controls = self.extra_controls()
        if controls:
            data["controls"] = controls
        return data
