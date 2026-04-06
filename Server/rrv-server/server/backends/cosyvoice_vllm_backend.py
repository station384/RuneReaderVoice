# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# server/backends/cosyvoice_vllm_backend.py
#
# Fun-CosyVoice3 backend with vLLM acceleration.
# Same voice cloning API as cosyvoice_backend but loads with load_vllm=True,
# enabling PagedAttention and continuous batching for the LLM stage.
# Apache 2.0 licensed model.
#
# Runs in rrv-cosyvoice-vllm/.venv — a separate venv from rrv-cosyvoice/.venv.
# DO NOT install CosyVoice's full requirements.txt into this venv — it breaks
# the vLLM stack. Only the minimal CosyVoice deps are installed individually.
#
# Required working versions:
#   vllm==0.11.2, torch==2.9.0, torchaudio==2.9.0, transformers==4.57.6
#   pydantic==2.12.5, onnxruntime-gpu==1.20.2, openai-whisper>=20250625
#   inflect, omegaconf, conformer, diffusers, hydra-core, HyperPyYAML
#
# Model — shared with cosyvoice backend at data/models/cosyvoice/cosyvoice3/
# No additional model download needed.
#
# LD_LIBRARY_PATH must be set by the run_worker.py launcher before any CUDA
# imports — see rrv-cosyvoice-vllm/run_worker.py.
#
# Source patch required in cosyvoice-src:
#   cosyvoice/vllm/cosyvoice2.py must import Union from typing.

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

_SYSTEM_PROMPT = "You are a helpful assistant.<|endofprompt|>"


class CosyVoiceVllmBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, max_concurrent: int = 6) -> None:
        self._models_dir     = models_dir
        self._torch_device   = torch_device
        self._model          = None
        self._model_version  = ""
        self._sample_rate    = 22050  # CosyVoice3 native; updated after load
        self._MAX_CONCURRENT = max_concurrent
        # Voice slot concurrency — same pattern as ChatterboxBackend.
        # Only allows concurrent requests for the same voice key.
        # Serializes across different voices to avoid reference audio context drift.
        self._voice_cond: asyncio.Condition | None = None
        self._active_voice_key: str | None = None
        self._active_count   = 0

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "cosyvoice_vllm"

    @property
    def display_name(self) -> str:
        return "CosyVoice3 vLLM"

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

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or ""
        instruct   = request.cosy_instruct or ""
        return f"{sample_key}|{lang_key}|{instruct}"

    async def _acquire_voice_slot(self, voice_key: str) -> None:
        if self._voice_cond is None:
            self._voice_cond = asyncio.Condition()
        async with self._voice_cond:
            while True:
                if self._active_voice_key is None:
                    self._active_voice_key = voice_key
                    self._active_count = 1
                    return
                if self._active_voice_key == voice_key and self._active_count < self._MAX_CONCURRENT:
                    self._active_count += 1
                    return
                await self._voice_cond.wait()

    async def _release_voice_slot(self, voice_key: str) -> None:
        async with self._voice_cond:
            if self._active_voice_key == voice_key and self._active_count > 0:
                self._active_count -= 1
                if self._active_count == 0:
                    self._active_voice_key = None
            self._voice_cond.notify_all()

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info("CosyVoice3-vLLM loaded: device=%s sample_rate=%d",
                 self._torch_device, self._sample_rate)

    def _load_sync(self) -> None:
        # Register vLLM model class BEFORE importing AutoModel —
        # vLLM's ModelRegistry must know about CosyVoice2ForCausalLM.
        try:
            from vllm import ModelRegistry  # type: ignore
            from cosyvoice.vllm.cosyvoice2 import CosyVoice2ForCausalLM  # type: ignore
            ModelRegistry.register_model("CosyVoice2ForCausalLM", CosyVoice2ForCausalLM)
        except ImportError as e:
            raise RuntimeError(
                f"vLLM or CosyVoice vLLM wrapper not available: {e}\n"
                "Ensure rrv-cosyvoice-vllm/.venv has vllm installed and "
                "cosyvoice-src is in sys.path via run_worker.py."
            )

        try:
            from cosyvoice.cli.cosyvoice import AutoModel  # type: ignore
        except ImportError:
            raise RuntimeError(
                "CosyVoice is not installed. "
                "Ensure cosyvoice-src is cloned and in sys.path in the worker venv."
            )

        model_dir = self._models_dir / "cosyvoice" / "cosyvoice3"
        if not model_dir.exists() or not any(model_dir.iterdir()):
            raise RuntimeError(
                f"CosyVoice3 model files not found: {model_dir}\n"
                f"Shared with cosyvoice backend — download with:\n"
                f"  snapshot_download('FunAudioLLM/Fun-CosyVoice3-0.5B-2512',\n"
                f"                    local_dir='{model_dir}')"
            )

        log.info("CosyVoice3-vLLM: loading from %s (load_vllm=True)", model_dir)

        log.info("CosyVoice3-vLLM: loading with load_vllm=True")
        # max_model_len is patched directly in cosyvoice-src/cosyvoice/cli/model.py
        # load_vllm() via RRV_COSYVOICE_VLLM_MAX_CTX env var.
        self._model = AutoModel(
            model_dir=str(model_dir),
            load_trt=False,
            load_vllm=True,
            fp16=False,
        )

        if hasattr(self._model, 'sample_rate'):
            self._sample_rate = self._model.sample_rate
            log.info("CosyVoice3-vLLM: sample_rate=%d", self._sample_rate)

        import hashlib
        files = sorted(
            str(p) for p in model_dir.rglob("*.pt") if p.is_file()
        ) + sorted(
            str(p) for p in model_dir.rglob("*.yaml") if p.is_file()
        )
        self._model_version = (
            hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
            if files else "cosyvoice3-vllm"
        )

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("CosyVoice3-vLLM backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "CosyVoice3-vLLM requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            loop = asyncio.get_event_loop()
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
            duration = estimate_duration(ogg_bytes)
            return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)
        finally:
            await self._release_voice_slot(voice_key)

    def _build_prompt_text(self, ref_text: str, lang_code: str) -> str:
        if not ref_text:
            log.warning("CosyVoice3-vLLM: no ref_text — voice cloning quality may suffer")
        return f"{_SYSTEM_PROMPT}{ref_text}"

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        ref_text = request.ref_text or ""
        if not ref_text:
            log.warning("CosyVoice3-vLLM: synthesizing '%s' without ref_text transcript",
                        request.sample_id or str(request.sample_path))

        prompt_text    = self._build_prompt_text(ref_text, request.lang_code)
        cosy_instruct  = request.cosy_instruct or ""

        log.debug("CosyVoice3-vLLM: synthesizing %d chars from sample '%s' instruct=%r",
                  len(request.text), request.sample_id or str(request.sample_path),
                  cosy_instruct[:50] if cosy_instruct else "")

        audio_chunks = []
        if cosy_instruct:
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
            raise RuntimeError("CosyVoice3-vLLM: inference produced no audio output")

        import torch
        wav_tensor = audio_chunks[0] if len(audio_chunks) == 1 else torch.cat(audio_chunks, dim=-1)
        samples = wav_tensor.squeeze().numpy().astype(np.float32)

        return pcm_to_ogg(samples, self._sample_rate)
