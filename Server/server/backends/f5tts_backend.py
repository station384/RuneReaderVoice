# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/f5tts_backend.py
#
# F5-TTS backend (Phase 5b — GPU-gated).
#
# Requires: pip install f5-tts torch torchaudio
#
# Model files MUST be pre-placed in RRV_MODELS_DIR/f5tts/ before starting.
# The server NEVER downloads anything at runtime — no HuggingFace, no Whisper.
#
# Required files:
#   data/models/f5tts/F5TTS_v1_Base/model_1250000.safetensors  (~1.2 GB)
#
# Download from: https://huggingface.co/SWivid/F5-TTS
#   File path in repo: ckpts/F5TTS_v1_Base/model_1250000.safetensors
#
# Reference samples MUST include a .ref.txt transcript sidecar.
# F5-TTS synthesis is refused if the sidecar is missing — Whisper is never
# invoked at runtime because this server is designed for air-gapped deployment.
#
# Supported:
#   - Zero-shot voice cloning from reference audio clip (6-15 seconds)
#   - Strong English quality; multilingual expanding
#   - CPU (very slow ~60s/sentence) or CUDA/ROCm GPU (fast)

from __future__ import annotations

import asyncio
import io
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from ..cache import compute_file_hash

log = logging.getLogger(__name__)


class F5TtsBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._model_version = ""
        self._infer_lock    = asyncio.Lock()

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "f5tts"

    @property
    def display_name(self) -> str:
        return "F5-TTS"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return True

    @property
    def supports_voice_blending(self) -> bool:
        return False

    @property
    def supports_inline_pronunciation(self) -> bool:
        return False

    @property
    def languages(self) -> list[str]:
        return ["en", "zh-cn"]

    @property
    def model_version(self) -> str:
        return self._model_version

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "F5-TTS loaded: model_version=%s device=%s",
            self._model_version, self._torch_device,
        )

    def _load_sync(self) -> None:
        try:
            from f5_tts.api import F5TTS
        except ImportError:
            raise RuntimeError(
                "f5-tts is not installed. Run: pip install f5-tts"
            )

        model_file = self._models_dir / "f5tts" / "F5TTS_v1_Base" / "model_1250000.safetensors"
        vocos_dir  = self._models_dir / "f5tts" / "vocos"

        if not model_file.exists():
            raise RuntimeError(
                f"F5-TTS model file not found: {model_file}\n"
                f"Download from: https://huggingface.co/SWivid/F5-TTS\n"
                f"  File path in repo: ckpts/F5TTS_v1_Base/model_1250000.safetensors\n"
                f"  Place at: {model_file}"
            )

        if not vocos_dir.exists() or not (vocos_dir / "pytorch_model.bin").exists():
            raise RuntimeError(
                f"F5-TTS Vocos vocoder not found: {vocos_dir}\n"
                f"Download from: https://huggingface.co/charactr/vocos-mel-24khz\n"
                f"  Files needed: config.yaml, pytorch_model.bin\n"
                f"  Place in: {vocos_dir}"
            )

        log.info("F5-TTS: loading from %s", model_file)
        log.info("F5-TTS: loading Vocos from %s", vocos_dir)
        self._model = F5TTS(
            model="F5TTS_v1_Base",
            ckpt_file=str(model_file),
            vocoder_local_path=str(vocos_dir),
            device=self._torch_device,
        )
        self._model_version = compute_file_hash(model_file)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []  # F5-TTS has no built-in named voices — reference clips only

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("F5-TTS backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "F5-TTS requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        if not request.ref_text:
            raise ValueError(
                f"F5-TTS requires a transcript of the reference audio clip. "
                f"Create a .ref.txt sidecar file alongside the sample containing "
                f"the verbatim spoken text. "
                f"The server does not use Whisper for auto-transcription."
            )

        if request.blend:
            raise ValueError("F5-TTS does not support voice blending.")

        loop = asyncio.get_event_loop()
        async with self._infer_lock:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        duration = _estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        wav, sample_rate, _ = self._model.infer(
            ref_file=str(request.sample_path),
            ref_text=request.ref_text,
            # Append trailing punctuation to give the model enough runway to
            # complete the final word. Without this, F5-TTS clips the last
            # syllable abruptly. The padding falls in the natural tail-off
            # and does not appear in the audible output.
            gen_text=request.text.rstrip() + " ...  ",
            speed=request.speech_rate,
            remove_silence=False,

        )

        samples = np.array(wav, dtype=np.float32)
        if samples.ndim > 1:
            samples = samples.squeeze()

        return _pcm_to_ogg(samples, sample_rate)


# ── Helpers ───────────────────────────────────────────────────────────────────

def _pcm_to_ogg(samples, sample_rate: int) -> bytes:
    import soundfile as sf
    buf = io.BytesIO()
    sf.write(buf, samples, sample_rate, format="OGG", subtype="VORBIS")
    buf.seek(0)
    return buf.read()


def _estimate_duration(ogg_bytes: bytes) -> float:
    try:
        import soundfile as sf
        buf = io.BytesIO(ogg_bytes)
        info = sf.info(buf)
        return info.duration
    except Exception:
        return 0.0
