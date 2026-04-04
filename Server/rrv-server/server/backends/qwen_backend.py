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
# server/backends/qwen_backend.py
#
# Qwen3-TTS backends.
#
# Three provider IDs, each backed by a different Qwen3-TTS checkpoint:
#
#   qwen_natural  — voice cloning from a reference audio clip
#                   Checkpoint: Qwen3-TTS-12Hz-{size}B-Base
#                   API:        model.generate_voice_clone(text, ref_audio, ref_text)
#                   Voice type: "reference" (sample_path + ref_text)
#
#   qwen_custom   — 9 named premium voices with natural language style control
#                   Checkpoint: Qwen3-TTS-12Hz-{size}B-CustomVoice
#                   API:        model.generate_custom_voice(text, language, speaker, instruct)
#                   Voice type: "base" (voice_id = speaker name)
#                   Extra:      voice_instruct = style instruction (optional)
#
#   qwen_design   — text-description-driven voice creation (no reference needed)
#                   Checkpoint: Qwen3-TTS-12Hz-1.7B-VoiceDesign (large only)
#                   API:        model.generate_voice_design(text, instruct)
#                   Voice type: "description" (voice_description = persona text)
#
# Model size (large=1.7B, small=0.6B) is set server-side via config and passed
# to the worker as --qwen-size. The client only sees provider_id, not size.
#
# All three backends share the same venv (rrv-qwen) and the same tokenizer
# checkpoint (Qwen3-TTS-Tokenizer-12Hz). Each runs as a separate worker process
# so they never share memory, but each loads its own tokenizer instance.
#
# Requires: pip install qwen-tts
# Optional: pip install flash-attn --no-build-isolation  (for faster attention)
#
# Model layout in data/models/qwen/:
#   tokenizer/          ← Qwen3-TTS-Tokenizer-12Hz (required by all)
#   natural-large/      ← Qwen3-TTS-12Hz-1.7B-Base
#   natural-small/      ← Qwen3-TTS-12Hz-0.6B-Base
#   custom-large/       ← Qwen3-TTS-12Hz-1.7B-CustomVoice
#   custom-small/       ← Qwen3-TTS-12Hz-0.6B-CustomVoice
#   design/             ← Qwen3-TTS-12Hz-1.7B-VoiceDesign (large only)

from __future__ import annotations

import asyncio
import io
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)

# Supported languages — Qwen3-TTS uses full English names, not ISO codes.
# Map from our lang_code format to Qwen's expected language string.
_LANG_MAP: dict[str, str] = {
    "en":    "English",
    "en-us": "English",
    "en-gb": "English",
    "zh":    "Chinese",
    "zh-cn": "Chinese",
    "zh-tw": "Chinese",
    "ja":    "Japanese",
    "ko":    "Korean",
    "de":    "German",
    "fr":    "French",
    "ru":    "Russian",
    "pt":    "Portuguese",
    "pt-br": "Portuguese",
    "es":    "Spanish",
    "it":    "Italian",
}

_SUPPORTED_LANGUAGES = sorted(set(_LANG_MAP.keys()))

# CustomVoice — built-in speakers.
# Names are queried from the model at load time via get_supported_speakers().
# This fallback list is used only if the model query fails.
# Names confirmed from model: aiden, dylan, eric, ono_anna, ryan, serena, sohee, uncle_fu, vivian
_CUSTOM_VOICES_FALLBACK = [
    # name,        display,      lang,     gender
    ("aiden",      "Aiden",      "en-us",  "male"),
    ("dylan",      "Dylan",      "en-us",  "male"),
    ("eric",       "Eric",       "en-us",  "male"),
    ("ono_anna",   "Ono Anna",   "ja",     "female"),
    ("ryan",       "Ryan",       "en-us",  "male"),
    ("serena",     "Serena",     "en-us",  "female"),
    ("sohee",      "Sohee",      "ko",     "female"),
    ("uncle_fu",   "Uncle Fu",   "zh-cn",  "male"),
    ("vivian",     "Vivian",     "zh-cn",  "female"),
]


def _normalize_lang(lang_code: str) -> str:
    """Map our ISO lang_code to Qwen's full-name language string."""
    normalized = _LANG_MAP.get(lang_code.lower())
    if normalized:
        return normalized
    # Try prefix match — "en-au" → "en" → "English"
    prefix = lang_code.split("-")[0].lower()
    return _LANG_MAP.get(prefix, "Auto")


def _qwen_models_dir(models_dir: Path) -> Path:
    """Qwen models live in a qwen/ subdirectory of the server models dir."""
    return models_dir / "qwen"


# ── Base class shared by all three Qwen backends ──────────────────────────────

class _QwenBackendBase(AbstractTtsBackend):
    """
    Common infrastructure for all three Qwen backends.
    Subclasses implement load(), synthesize(), and the property overrides.
    """

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir = _qwen_models_dir(models_dir)
        self._torch_device = torch_device
        self._model = None
        self._model_version_str = ""

    @property
    def model_version(self) -> str:
        return self._model_version_str

    @property
    def supports_voice_blending(self) -> bool:
        return False

    @property
    def supports_inline_pronunciation(self) -> bool:
        return False

    def _resolve_attn_impl(self) -> str:
        """Use flash_attention_2 if available, otherwise fall back to eager."""
        try:
            import flash_attn  # noqa: F401
            log.info("Qwen: flash_attention_2 available (v%s) — using it", flash_attn.__version__)
            return "flash_attention_2"
        except ImportError:
            log.warning("Qwen: flash_attn not installed — falling back to eager attention. "
                        "Install with: pip install flash-attn --no-build-isolation")
            return "eager"

    def _resolve_dtype(self):
        """
        Resolve the torch dtype for model loading.
        Qwen3-TTS requires bfloat16 — float16 causes device-side assert errors
        due to internal dtype assumptions in the model's CUDA kernels.
        bfloat16 is natively supported on Ampere (3080) and later GPUs.
        """
        import torch
        return torch.bfloat16

    def _resolve_device_map(self):
        """
        Resolve the device_map for Qwen3TTSModel.from_pretrained().

        qwen-tts has a known bug where string device_map values ("cuda", "cuda:0")
        are ignored and the model loads on CPU. The workaround is to use a dict
        format {"": device_index} which forces all tensors to the specified device.
        See: https://github.com/QwenLM/Qwen-Audio/issues/85
        """
        if self._torch_device == "cpu":
            return "cpu"
        # Extract device index from "cuda" or "cuda:N"
        if self._torch_device == "cuda":
            return {"": 0}
        elif self._torch_device.startswith("cuda:"):
            idx = int(self._torch_device.split(":")[1])
            return {"": idx}
        else:
            return "cpu"

    def _pcm_to_result(self, wavs, sr: int) -> SynthesisResult:
        """Convert Qwen's numpy waveform output to OGG SynthesisResult."""
        import numpy as np
        wav = wavs[0] if isinstance(wavs, (list, tuple)) else wavs
        wav_np = np.array(wav, dtype=np.float32)
        # Normalize to [-1, 1] if needed
        if wav_np.max() > 1.0 or wav_np.min() < -1.0:
            wav_np = wav_np / max(abs(wav_np.max()), abs(wav_np.min()), 1e-8)
        ogg_bytes = pcm_to_ogg(wav_np, sr)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)


# ── QwenNaturalBackend ────────────────────────────────────────────────────────

class QwenNaturalBackend(_QwenBackendBase):
    """
    qwen_natural — voice cloning from a reference audio clip.

    Uses Qwen3-TTS-12Hz-{size}B-Base checkpoint.
    Accepts voice.type = "reference" with sample_path + ref_text.
    """

    def __init__(self, models_dir: Path, torch_device: str, size: str = "large") -> None:
        super().__init__(models_dir, torch_device)
        self._size = size
        self._checkpoint_dir = self._models_dir / f"natural-{size}"

    @property
    def provider_id(self) -> str:
        return "qwen_natural"

    @property
    def display_name(self) -> str:
        return f"Qwen Natural ({self._size})"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return True

    @property
    def languages(self) -> list[str]:
        return _SUPPORTED_LANGUAGES

    def get_voices(self) -> list[VoiceInfo]:
        return []  # Reference-only — no named voices

    async def load(self) -> None:
        if not self._checkpoint_dir.exists():
            raise RuntimeError(
                f"Qwen Natural ({self._size}) model not found: {self._checkpoint_dir}\n"
                f"Expected: data/models/qwen/natural-{self._size}/\n"
                f"Download: Qwen/Qwen3-TTS-12Hz-"
                f"{'1.7B' if self._size == 'large' else '0.6B'}-Base"
            )
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)

    def _load_sync(self) -> None:
        import torch
        from qwen_tts import Qwen3TTSModel

        attn = self._resolve_attn_impl()
        log.info("Loading Qwen Natural (%s) from %s (attn=%s)", self._size, self._checkpoint_dir, attn)

        self._model = Qwen3TTSModel.from_pretrained(
            str(self._checkpoint_dir),
            device_map=self._resolve_device_map(),
            dtype=self._resolve_dtype(),
            attn_implementation=attn,
        )
        self._model_version_str = f"natural-{self._size}"

        # torch.compile() reduces Python overhead in the autoregressive token loop.
        # One-time compilation cost on first synthesis (~30s), faster for all subsequent calls.
        try:
            import os
            if os.environ.get("RRV_QWEN_COMPILE", "").lower() in ("1", "true", "yes"):
                log.info("Qwen Natural: compiling model with torch.compile() — first synthesis will be slow")
                self._model.model = torch.compile(self._model.model, mode="reduce-overhead")
        except Exception as e:
            log.warning("torch.compile() failed (non-fatal): %s", e)

        log.info("Qwen Natural (%s) loaded", self._size)

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Qwen Natural backend is not loaded")
        if request.sample_path is None:
            raise ValueError(
                "qwen_natural requires a reference audio clip. "
                "Provide sample_id in the request."
            )
        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(None, self._synthesize_sync, request)

    def _synthesize_sync(self, request: SynthesisRequest) -> SynthesisResult:
        ref_audio = str(request.sample_path)
        ref_text = request.ref_text or None  # None triggers x_vector_only mode

        log.debug(
            "Qwen Natural: cloning voice from %s ref_text_len=%d",
            request.sample_path.name if request.sample_path else "none",
            len(ref_text) if ref_text else 0,
        )

        wavs, sr = self._model.generate_voice_clone(
            text=request.text,
            ref_audio=ref_audio,
            ref_text=ref_text,
            language=_normalize_lang(request.lang_code),
        )
        return self._pcm_to_result(wavs, sr)


# ── QwenCustomBackend ─────────────────────────────────────────────────────────

class QwenCustomBackend(_QwenBackendBase):
    """
    qwen_custom — 9 named premium voices with natural language style control.

    Uses Qwen3-TTS-12Hz-{size}B-CustomVoice checkpoint.
    Accepts voice.type = "base" with voice_id = speaker name.
    Optional voice_instruct for style control (e.g. "speak excitedly").
    """

    def __init__(self, models_dir: Path, torch_device: str, size: str = "large") -> None:
        super().__init__(models_dir, torch_device)
        self._size = size
        self._checkpoint_dir = self._models_dir / f"custom-{size}"
        self._voices: list[VoiceInfo] = []
        self._valid_speakers: set[str] = set()

    @property
    def provider_id(self) -> str:
        return "qwen_custom"

    @property
    def display_name(self) -> str:
        return f"Qwen Custom ({self._size})"

    @property
    def supports_base_voices(self) -> bool:
        return True

    @property
    def supports_voice_matching(self) -> bool:
        return False

    @property
    def supports_voice_instruct(self) -> bool:
        return True

    @property
    def languages(self) -> list[str]:
        return _SUPPORTED_LANGUAGES

    def extra_controls(self) -> dict:
        return {
            "voice_instruct": {
                "type":        "string",
                "description": "Natural language style instruction, e.g. 'speak softly and warmly'",
                "default":     "",
                "required":    False,
            }
        }

    def get_voices(self) -> list[VoiceInfo]:
        return list(self._voices)

    async def load(self) -> None:
        if not self._checkpoint_dir.exists():
            raise RuntimeError(
                f"Qwen Custom ({self._size}) model not found: {self._checkpoint_dir}\n"
                f"Expected: data/models/qwen/custom-{self._size}/\n"
                f"Download: Qwen/Qwen3-TTS-12Hz-"
                f"{'1.7B' if self._size == 'large' else '0.6B'}-CustomVoice"
            )
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)

    def _load_sync(self) -> None:
        import torch
        from qwen_tts import Qwen3TTSModel

        attn = self._resolve_attn_impl()
        log.info("Loading Qwen Custom (%s) from %s (attn=%s)", self._size, self._checkpoint_dir, attn)

        self._model = Qwen3TTSModel.from_pretrained(
            str(self._checkpoint_dir),
            device_map=self._resolve_device_map(),
            dtype=self._resolve_dtype(),
            attn_implementation=attn,
        )
        self._model_version_str = f"custom-{self._size}"

        # Query supported speakers from the model itself
        try:
            speakers = self._model.get_supported_speakers()
            log.info("Qwen Custom (%s) speakers: %s", self._size, speakers)
        except Exception as e:
            log.warning("Could not query speakers from model, using fallback list: %s", e)
            speakers = [name for name, _, _, _ in _CUSTOM_VOICES_FALLBACK]

        # Build VoiceInfo list — use title-case display names
        self._voices = [
            VoiceInfo(
                voice_id=s,
                display_name=s.replace("_", " ").title(),
                language="en",   # most speakers support all languages; en is safe default
                gender="unknown",
                type="base",
            )
            for s in speakers
        ]
        self._valid_speakers = {v.voice_id for v in self._voices}
        try:
            import os
            if os.environ.get("RRV_QWEN_COMPILE", "").lower() in ("1", "true", "yes"):
                log.info("Qwen Custom: compiling model with torch.compile() — first synthesis will be slow")
                self._model.model = torch.compile(self._model.model, mode="reduce-overhead")
        except Exception as e:
            log.warning("torch.compile() failed (non-fatal): %s", e)

        log.info("Qwen Custom (%s) loaded — %d speakers", self._size, len(self._voices))

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Qwen Custom backend is not loaded")

        speaker = request.voice_id
        if not speaker:
            # Default to first voice if none specified
            speaker = _CUSTOM_VOICES[0][0]
            log.warning("qwen_custom: no voice_id provided, defaulting to %s", speaker)

        # Validate speaker against what the model actually supports
        if self._valid_speakers and speaker not in self._valid_speakers:
            raise ValueError(
                f"Unknown speaker '{speaker}' for qwen_custom. "
                f"Valid: {sorted(self._valid_speakers)}"
            )

        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(None, self._synthesize_sync, request, speaker)

    def _synthesize_sync(self, request: SynthesisRequest, speaker: str) -> SynthesisResult:
        language = _normalize_lang(request.lang_code)
        instruct = request.voice_instruct or None  # None = no style instruction

        log.debug(
            "Qwen Custom: speaker=%s language=%s instruct=%s",
            speaker, language, repr(instruct),
        )

        wavs, sr = self._model.generate_custom_voice(
            text=request.text,
            language=language,
            speaker=speaker,
            instruct=instruct,
        )
        return self._pcm_to_result(wavs, sr)


# ── QwenDesignBackend ─────────────────────────────────────────────────────────

class QwenDesignBackend(_QwenBackendBase):
    """
    qwen_design — synthesize with a voice described in natural language.

    Uses Qwen3-TTS-12Hz-1.7B-VoiceDesign checkpoint (large only — no 0.6B variant).
    Accepts voice.type = "description" with voice.voice_description = persona text.

    Example voice_description:
      "A warm elderly male narrator with a slight British accent and calm, measured pace"
      "A young excited female with high energy and a California Valley Girl accent"
    """

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        super().__init__(models_dir, torch_device)
        self._checkpoint_dir = self._models_dir / "design"

    @property
    def provider_id(self) -> str:
        return "qwen_design"

    @property
    def display_name(self) -> str:
        return "Qwen Voice Design"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return False

    @property
    def supports_voice_design(self) -> bool:
        return True

    @property
    def languages(self) -> list[str]:
        return _SUPPORTED_LANGUAGES

    def extra_controls(self) -> dict:
        return {
            "voice_description": {
                "type":        "string",
                "description": "Natural language description of the voice persona",
                "default":     "",
                "required":    True,
                "example":     "A warm middle-aged male narrator with a gentle British accent",
            }
        }

    def get_voices(self) -> list[VoiceInfo]:
        return []  # No named voices — description drives everything

    async def load(self) -> None:
        if not self._checkpoint_dir.exists():
            raise RuntimeError(
                f"Qwen Voice Design model not found: {self._checkpoint_dir}\n"
                f"Expected: data/models/qwen/design/\n"
                f"Download: Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign"
            )
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)

    def _load_sync(self) -> None:
        import torch
        from qwen_tts import Qwen3TTSModel

        attn = self._resolve_attn_impl()
        log.info("Loading Qwen Voice Design from %s (attn=%s)", self._checkpoint_dir, attn)

        self._model = Qwen3TTSModel.from_pretrained(
            str(self._checkpoint_dir),
            device_map=self._resolve_device_map(),
            dtype=self._resolve_dtype(),
            attn_implementation=attn,
        )
        self._model_version_str = "design"

        try:
            import os
            if os.environ.get("RRV_QWEN_COMPILE", "").lower() in ("1", "true", "yes"):
                log.info("Qwen Design: compiling model with torch.compile() — first synthesis will be slow")
                self._model.model = torch.compile(self._model.model, mode="reduce-overhead")
        except Exception as e:
            log.warning("torch.compile() failed (non-fatal): %s", e)

        log.info("Qwen Voice Design loaded")

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Qwen Voice Design backend is not loaded")

        if not request.voice_description:
            raise ValueError(
                "qwen_design requires a voice description. "
                "Set voice.voice_description in the request."
            )

        loop = asyncio.get_event_loop()
        return await loop.run_in_executor(None, self._synthesize_sync, request)

    def _synthesize_sync(self, request: SynthesisRequest) -> SynthesisResult:
        log.debug(
            "Qwen Design: description=%s",
            repr(request.voice_description[:80] + "..." if len(request.voice_description or "") > 80
                 else request.voice_description),
        )

        wavs, sr = self._model.generate_voice_design(
            text=request.text,
            language=_normalize_lang(request.lang_code),
            instruct=request.voice_description,
        )
        return self._pcm_to_result(wavs, sr)
