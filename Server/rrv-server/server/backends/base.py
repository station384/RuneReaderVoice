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
    cfg_strength:        Optional[float] = None   # F5-TTS: reference adherence (default 2.0)
    nfe_step:            Optional[int]   = None   # F5-TTS: ODE solver steps (default 64 vocos, 32 bigvgan)
    cross_fade_duration: Optional[float] = None   # F5-TTS: internal chunk stitch duration (default 0.15)
    sway_sampling_coef:  Optional[float] = None   # F5-TTS: ODE time step distribution (-1.0=sway optimal, 0=uniform)
    # Qwen-specific controls
    voice_instruct:    Optional[str] = None  # qwen_custom: natural language style instruction
    voice_context:     Optional[str] = None  # slot identity string (e.g. "NightElf/Female") for cache discrimination and prior token gating
    voice_description: Optional[str] = None  # qwen_design: natural language voice persona description

    # LuxTTS-specific controls
    lux_num_steps:     Optional[int]   = None  # ODE solver steps (default 10, range 4-32)
    lux_t_shift:       Optional[float] = None  # time shift (default 0.7, range 0.1-1.0)
    lux_return_smooth: Optional[bool]  = None  # smoothing to reduce raspy artifacts

    # CosyVoice3-specific controls
    cosy_instruct:     Optional[str]   = None  # natural language style instruction for inference_instruct2()

    # Chatterbox/T3 sampling controls — passed directly to t3.inference()
    # These are available in both ChatterboxTTS and ChatterboxTTSTurbo.
    cb_temperature:        Optional[float] = None  # token sampling temperature (default 0.8). Lower=stable, higher=expressive.
    cb_top_p:              Optional[float] = None  # nucleus sampling cutoff (default 1.0 full, 0.95 turbo). Lower=conservative.
    cb_repetition_penalty: Optional[float] = None  # repeat token penalty (default 1.2). Higher=less repetition/hallucination.

    # LongCat-AudioDiT-specific controls
    longcat_steps:        Optional[int]   = None  # ODE Euler steps (default 16)
    longcat_cfg_strength: Optional[float] = None  # guidance strength (default 4.0)
    longcat_guidance:     Optional[str]   = None  # 'apg' or 'cfg' (default 'apg')

    # Reproducibility — supported by all backends that use random sampling.
    # None = non-deterministic (default). Integer = fixed seed for repeatable output.
    synthesis_seed:       Optional[int]   = None

    # Cache key and cache dir — set by the route so the backend can write
    # tail token sidecars directly after synthesis.
    cache_key:  Optional[str]  = None
    cache_dir:  Optional[str]  = None

    # Explicit T3 continuation reference for batch stitching.
    # This is ONLY for prior speech token carryover and must never affect
    # voice conditioning cache selection. The client may point a segment at
    # the prior same-speaker segment so continuity survives cache hits,
    # partial regeneration, and worker restarts.
    continue_from_cache_key: Optional[str] = None

    # Progress callback — called by backend as each chunk completes.
    # Signature: callback(chunk: int, total: int) -> None
    # None = no progress reporting (v1 endpoint)
    progress_callback: Optional[object] = None


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
    def supports_voice_instruct(self) -> bool:
        """True if the backend supports natural language style instructions (qwen_custom)."""
        return False

    @property
    def supports_voice_design(self) -> bool:
        """True if the backend supports text-description-based voice creation (qwen_design)."""
        return False

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

    @property
    def supports_synthesis_seed(self) -> bool:
        """
        True if the backend uses random sampling and honours synthesis_seed.
        Kokoro (deterministic ONNX) returns False. All stochastic backends
        (Chatterbox, F5-TTS, LuxTTS, LongCat, CosyVoice) return True.
        Defaults to True — backends that are deterministic should override.
        """
        return True

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
            "supports_voice_instruct":    self.supports_voice_instruct,
            "supports_voice_design":      self.supports_voice_design,
            "supports_synthesis_seed":    self.supports_synthesis_seed,
            "languages":                  self.languages,
        }
        controls = self.extra_controls()
        if controls:
            data["controls"] = controls
        return data
