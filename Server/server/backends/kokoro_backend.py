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
# server/backends/kokoro_backend.py
#
# Kokoro-82M ONNX backend.
#
# Requires: pip install "rrv-server[kokoro]"
# Model files (in RRV_MODELS_DIR):
#   kokoro-v1.0.onnx
#   voices-v1.0.bin
#
# Model files are shared with the RuneReader Voice C# local client
# if both run on the same machine.
#
# Kokoro supports:
#   - 54 built-in voices (base)
#   - Weighted voice blending (blend)
#   - Kokoro/Misaki inline IPA phoneme markup
#   - CPU, ORT-CUDA, ORT-ROCm execution providers
#   - No voice matching (reference clips) — use F5-TTS or Chatterbox Turbo for that

from __future__ import annotations

import asyncio
import io
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..cache import compute_file_hash

log = logging.getLogger(__name__)

# All 54 Kokoro voice IDs with display metadata
# gender derived from prefix: af_/bf_ = female, am_/bm_/jf_/jm_/zf_/zm_ etc.
_KOKORO_VOICES = [
    # American English female
    ("af_alloy",    "Alloy",    "en-us", "female"),
    ("af_aoede",    "Aoede",    "en-us", "female"),
    ("af_bella",    "Bella",    "en-us", "female"),
    ("af_heart",    "Heart",    "en-us", "female"),
    ("af_jessica",  "Jessica",  "en-us", "female"),
    ("af_kore",     "Kore",     "en-us", "female"),
    ("af_nicole",   "Nicole",   "en-us", "female"),
    ("af_nova",     "Nova",     "en-us", "female"),
    ("af_river",    "River",    "en-us", "female"),
    ("af_sarah",    "Sarah",    "en-us", "female"),
    ("af_sky",      "Sky",      "en-us", "female"),
    # American English male
    ("am_adam",     "Adam",     "en-us", "male"),
    ("am_echo",     "Echo",     "en-us", "male"),
    ("am_eric",     "Eric",     "en-us", "male"),
    ("am_fenrir",   "Fenrir",   "en-us", "male"),
    ("am_liam",     "Liam",     "en-us", "male"),
    ("am_michael",  "Michael",  "en-us", "male"),
    ("am_onyx",     "Onyx",     "en-us", "male"),
    ("am_puck",     "Puck",     "en-us", "male"),
    ("am_santa",    "Santa",    "en-us", "male"),
    # British English female
    ("bf_alice",    "Alice",    "en-gb", "female"),
    ("bf_emma",     "Emma",     "en-gb", "female"),
    ("bf_isabella", "Isabella", "en-gb", "female"),
    ("bf_lily",     "Lily",     "en-gb", "female"),
    # British English male
    ("bm_daniel",   "Daniel",   "en-gb", "male"),
    ("bm_fable",    "Fable",    "en-gb", "male"),
    ("bm_george",   "George",   "en-gb", "male"),
    ("bm_lewis",    "Lewis",    "en-gb", "male"),
    ("bm_will",     "Will",     "en-gb", "male"),
    # Japanese female
    ("jf_alpha",    "Alpha",    "ja",    "female"),
    ("jf_gongitsune","Gongitsune","ja",  "female"),
    ("jf_nezuko",   "Nezuko",   "ja",    "female"),
    ("jf_tebukuro", "Tebukuro", "ja",    "female"),
    # Japanese male
    ("jm_kumo",     "Kumo",     "ja",    "male"),
    # Mandarin Chinese female
    ("zf_xiaobei",  "Xiaobei",  "zh-cn", "female"),
    ("zf_xiaoni",   "Xiaoni",   "zh-cn", "female"),
    ("zf_xiaoxiao", "Xiaoxiao", "zh-cn", "female"),
    ("zf_xiaoyi",   "Xiaoyi",   "zh-cn", "female"),
    # Mandarin Chinese male
    ("zm_yunjian",  "Yunjian",  "zh-cn", "male"),
    ("zm_yunxi",    "Yunxi",    "zh-cn", "male"),
    ("zm_yunxia",   "Yunxia",   "zh-cn", "male"),
    ("zm_yunyang",  "Yunyang",  "zh-cn", "male"),
    # Spanish female
    ("ef_dora",     "Dora",     "es",    "female"),
    # Spanish male
    ("em_alex",     "Alex",     "es",    "male"),
    ("em_santa",    "Santa",    "es",    "male"),
    # French female
    ("ff_siwis",    "Siwis",    "fr",    "female"),
    # Hindi female
    ("hf_alpha",    "Alpha",    "hi",    "female"),
    ("hf_beta",     "Beta",     "hi",    "female"),
    # Hindi male
    ("hm_omega",    "Omega",    "hi",    "male"),
    ("hm_psi",      "Psi",      "hi",    "male"),
    # Italian female
    ("if_sara",     "Sara",     "it",    "female"),
    # Italian male
    ("im_nicola",   "Nicola",   "it",    "male"),
    # Portuguese female
    ("pf_dora",     "Dora",     "pt",    "female"),
    # Portuguese male
    ("pm_alex",     "Alex",     "pt",    "male"),
    ("pm_santa",    "Santa",    "pt",    "male"),
]


class KokoroBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, ort_providers: list) -> None:
        self._models_dir    = models_dir
        self._ort_providers = ort_providers
        self._pipeline      = None
        self._model_version = ""

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "kokoro"

    @property
    def display_name(self) -> str:
        return "Kokoro 82M"

    @property
    def supports_base_voices(self) -> bool:
        return True

    @property
    def supports_voice_matching(self) -> bool:
        return False

    @property
    def supports_voice_blending(self) -> bool:
        return True

    @property
    def supports_inline_pronunciation(self) -> bool:
        return True

    @property
    def languages(self) -> list[str]:
        return ["en-us", "en-gb", "ja", "zh-cn", "es", "fr", "hi", "it", "pt"]

    @property
    def model_version(self) -> str:
        return self._model_version

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        onnx_path = self._models_dir / "kokoro-v1.0.onnx"
        voices_path = self._models_dir / "voices-v1.0.bin"

        if not onnx_path.exists():
            raise RuntimeError(
                f"Kokoro model file not found: {onnx_path}\n"
                f"Download from: https://github.com/thewh1teagle/kokoro-onnx/releases\n"
                f"  kokoro-v1.0.onnx\n"
                f"  voices-v1.0.bin"
            )
        if not voices_path.exists():
            raise RuntimeError(
                f"Kokoro voices file not found: {voices_path}\n"
                f"Download from: https://github.com/thewh1teagle/kokoro-onnx/releases"
            )

        # Compute model version hash from ONNX file
        self._model_version = compute_file_hash(onnx_path)

        # Load in thread pool — ONNX session creation is CPU-bound
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync, onnx_path, voices_path)
        log.info(
            "Kokoro loaded: model_version=%s providers=%s",
            self._model_version, self._ort_providers,
        )

    def _load_sync(self, onnx_path: Path, voices_path: Path) -> None:
        try:
            from kokoro_onnx import Kokoro
        except ImportError:
            raise RuntimeError(
                "kokoro-onnx is not installed. "
                "Run: pip install 'rrv-server[kokoro]'"
            )

        # kokoro-onnx 0.5.0+ no longer accepts a providers argument directly.
        # Set the ONNX Runtime execution providers via environment before loading,
        # or just let kokoro-onnx pick them up automatically from onnxruntime.
        # The ORT providers are still used by onnxruntime internally.
        self._pipeline = Kokoro(str(onnx_path), str(voices_path))

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return [
            VoiceInfo(
                voice_id=vid,
                display_name=name,
                language=lang,
                gender=gender,
                type="base",
            )
            for vid, name, lang, gender in _KOKORO_VOICES
        ]

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._pipeline is None:
            raise RuntimeError("Kokoro backend is not loaded")

        if request.sample_path is not None:
            raise ValueError("Kokoro does not support voice matching (reference clips). "
                             "Use F5-TTS or Chatterbox Turbo for reference-based synthesis.")

        loop = asyncio.get_event_loop()
        ogg_bytes = await loop.run_in_executor(
            None, self._synthesize_sync, request
        )

        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        if request.blend:
            voice_spec = _build_blend_spec(request.blend)
        else:
            voice_spec = request.voice_id or "am_michael"

        # kokoro-onnx returns (samples, sample_rate) as numpy array + int
        samples, sample_rate = self._pipeline.create(
            text=request.text,
            voice=voice_spec,
            speed=request.speech_rate,
            lang=_normalize_lang(request.lang_code),
        )

        return pcm_to_ogg(samples, sample_rate)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _build_blend_spec(blend: list[dict]) -> str:
    """
    Convert blend list to Kokoro mix: string format.
    e.g. [{"voice_id": "am_adam", "weight": 0.4}, ...]
      → "mix:am_adam:0.4|bm_lewis:0.6"
    """
    parts = [f"{entry['voice_id']}:{entry['weight']:.4f}" for entry in blend]
    return "mix:" + "|".join(parts)


def _normalize_lang(lang_code: str) -> str:
    """Map generic lang codes to Kokoro's expected format."""
    mapping = {
        "en":    "en-us",
        "en-us": "en-us",
        "en-gb": "en-gb",
        "ja":    "ja",
        "zh":    "zh",
        "zh-cn": "zh",
        "es":    "es",
        "fr":    "fr",
        "hi":    "hi",
        "it":    "it",
        "pt":    "pt",
    }
    return mapping.get(lang_code.lower(), "en-us")


# OGG encoding — see backends/audio.py


