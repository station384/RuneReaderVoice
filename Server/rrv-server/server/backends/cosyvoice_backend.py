# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# server/backends/cosyvoice_backend.py
#
# Fun-CosyVoice3 backend — zero-shot multilingual voice cloning.
# Apache 2.0 licensed.
#
# Install (in rrv-cosyvoice/.venv, Python 3.11):
#   git clone --recursive https://github.com/FunAudioLLM/CosyVoice.git cosyvoice-src
#   cd cosyvoice-src
#   pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
#   pip install -r requirements.txt
#   sudo apt-get install sox libsox-dev -y
#
# The run_worker.py launcher MUST add cosyvoice-src and
# cosyvoice-src/third_party/Matcha-TTS to sys.path before this module loads.
#
# Model — download to data/models/cosyvoice/cosyvoice3/:
#   snapshot_download('FunAudioLLM/Fun-CosyVoice3-0.5B-2512',
#                     local_dir='data/models/cosyvoice/cosyvoice3')
#
# Text normalization: ttsfrd is not required — wetext fallback is used.
# pynini/conda is NOT required — wetext handles English normalization.
#
# Architecture: LLM (Qwen tokenizer) + chunk-aware flow matching.
# Hybrid autoregressive+flow — faster than pure AR (Qwen natural) but
# still has some autoregressive overhead in the LLM stage.
# Approximate speed: 2-5x realtime on consumer GPU (RTX 3080 class).
#
# Voice cloning: inference_zero_shot() — reference audio + transcript.
# Requires a ref_text (transcript of the reference clip). If ref_text
# is empty the backend logs a warning and proceeds — quality may suffer.
#
# Output: 22050 Hz (CosyVoice3 native rate).

from __future__ import annotations

import asyncio
import logging
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)

# Language code map: RRV lang_code -> CosyVoice3 prompt prefix
_LANG_TOKENS = {
    "en":    "<|en|>",
    "en-us": "<|en|>",
    "en-gb": "<|en|>",
    "zh":    "<|zh|>",
    "zh-cn": "<|zh|>",
    "zh-tw": "<|zh|>",
    "ja":    "<|jp|>",
    "ko":    "<|ko|>",
    "de":    "<|de|>",
    "fr":    "<|fr|>",
    "es":    "<|es|>",
    "it":    "<|it|>",
    "ru":    "<|ru|>",
    "pt":    "<|pt|>",
    "pt-br": "<|pt|>",
}

# System prompt prepended to reference transcript for CosyVoice3
# This is the expected format per the official example
_SYSTEM_PROMPT = "You are a helpful assistant.<|endofprompt|>"


class CosyVoiceBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._model_version = ""
        self._sample_rate   = 22050  # CosyVoice3 native

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "cosyvoice"

    @property
    def display_name(self) -> str:
        return "CosyVoice3"

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
    def supports_voice_instruct(self) -> bool:
        return True

    @property
    def languages(self) -> list[str]:
        return ["en", "en-us", "en-gb", "zh", "zh-cn", "zh-tw",
                "ja", "ko", "de", "fr", "es", "it", "ru", "pt", "pt-br"]

    @property
    def model_version(self) -> str:
        return self._model_version

    def extra_controls(self) -> dict:
        return {
            "cosy_instruct": {
                "type":        "string",
                "default":     "",
                "description": (
                    "Natural language style instruction embedded in the synthesis prompt. "
                    "Examples: 'speak slowly with a dramatic tone', 'whisper softly', "
                    "'speak with excitement'. Uses inference_instruct2() when set."
                ),
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info("CosyVoice3 loaded: device=%s sample_rate=%d",
                 self._torch_device, self._sample_rate)

    def _load_sync(self) -> None:
        # sys.path must include cosyvoice-src and cosyvoice-src/third_party/Matcha-TTS
        # This is set up by the run_worker.py launcher in rrv-cosyvoice/
        try:
            from cosyvoice.cli.cosyvoice import AutoModel  # type: ignore
        except ImportError:
            raise RuntimeError(
                "CosyVoice is not installed. "
                "Ensure cosyvoice-src is cloned and in sys.path in the worker venv. "
                "See INSTALL notes in cosyvoice_backend.py."
            )

        model_dir = self._models_dir / "cosyvoice" / "cosyvoice3"
        if not model_dir.exists() or not any(model_dir.iterdir()):
            raise RuntimeError(
                f"CosyVoice3 model files not found: {model_dir}\n"
                f"Download with:\n"
                f"  from huggingface_hub import snapshot_download\n"
                f"  snapshot_download('FunAudioLLM/Fun-CosyVoice3-0.5B-2512',\n"
                f"                    local_dir='{model_dir}')"
            )

        log.info("CosyVoice3: loading from %s", model_dir)

        # Load model — AutoModel auto-detects version from cosyvoice3.yaml
        # load_jit=False avoids JIT compilation issues on some configs
        self._model = AutoModel(
            model_dir=str(model_dir),
        )

        # Get the actual sample rate from the loaded model
        if hasattr(self._model, 'sample_rate'):
            self._sample_rate = self._model.sample_rate
            log.info("CosyVoice3: sample_rate=%d", self._sample_rate)

        # Model version from yaml/safetensors files
        import hashlib
        files = sorted(
            str(p) for p in model_dir.rglob("*.pt")
            if p.is_file()
        ) + sorted(
            str(p) for p in model_dir.rglob("*.yaml")
            if p.is_file()
        )
        self._model_version = (
            hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
            if files else "cosyvoice3"
        )

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("CosyVoice3 backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "CosyVoice3 requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        loop = asyncio.get_event_loop()
        ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _build_prompt_text(self, ref_text: str, lang_code: str) -> str:
        """
        Build the prompt text in CosyVoice3 format.
        CosyVoice3 expects: <system_prompt><lang_token><ref_text>
        Example: "You are a helpful assistant.<|endofprompt|>Hello there."
        The lang token in the prompt text helps the model match language.
        """
        if not ref_text:
            log.warning("CosyVoice3: no ref_text provided — voice cloning quality may suffer")
            ref_text = ""

        # CosyVoice3 uses system prompt + ref text as the prompt_text argument
        return f"{_SYSTEM_PROMPT}{ref_text}"

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np
        import torchaudio

        ref_text = request.ref_text or ""
        if not ref_text:
            log.warning("CosyVoice3: synthesizing '%s' without ref_text transcript",
                        request.sample_id or str(request.sample_path))

        prompt_text = self._build_prompt_text(ref_text, request.lang_code)

        # cosy_instruct uses inference_instruct2() — natural language style control
        # embedded in the prompt text alongside the system prompt.
        cosy_instruct = request.cosy_instruct or ""

        log.debug("CosyVoice3: synthesizing %d chars from sample '%s' instruct=%r",
                  len(request.text), request.sample_id or str(request.sample_path),
                  cosy_instruct[:50] if cosy_instruct else "")

        # Collect all audio chunks from the generator
        audio_chunks = []
        if cosy_instruct:
            # inference_instruct2: style instruction for delivery control.
            # Format: "You are a helpful assistant. <instruction><|endofprompt|>"
            # No ref_text here — voice cloning comes from the reference audio alone.
            instruct_prompt = f"You are a helpful assistant. {cosy_instruct}<|endofprompt|>"
            for _, chunk in enumerate(self._model.inference_instruct2(
                request.text,
                instruct_prompt,
                str(request.sample_path),
                stream=False,
            )):
                audio_chunks.append(chunk['tts_speech'])
        else:
            for _, chunk in enumerate(self._model.inference_zero_shot(
                request.text,
                prompt_text,
                str(request.sample_path),
                stream=False,
            )):
                audio_chunks.append(chunk['tts_speech'])

        if not audio_chunks:
            raise RuntimeError("CosyVoice3: inference produced no audio output")

        # Concatenate all chunks along time axis
        import torch
        if len(audio_chunks) == 1:
            wav_tensor = audio_chunks[0]
        else:
            wav_tensor = torch.cat(audio_chunks, dim=-1)

        # Convert to numpy float32
        samples = wav_tensor.squeeze().numpy().astype(np.float32)

        return pcm_to_ogg(samples, self._sample_rate)
