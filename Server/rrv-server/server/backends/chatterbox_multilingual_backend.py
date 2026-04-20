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
#
# server/backends/chatterbox_multilingual_backend.py
#
# Chatterbox Multilingual backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Same 0.5B architecture as the original Chatterbox full model, retrained
# for 23-language support. Uses ChatterboxMultilingualTTS from chatterbox.mtl_tts.
#
# Install (chatterbox-tts 0.1.7+, Python 3.11):
#   pip install chatterbox-tts
#   pip install --no-deps s3tokenizer
#   pip install onnx>=1.16.0
#
# Model files — place in data/models/chatterbox-ml/:
#   Download from: https://huggingface.co/ResembleAI/chatterbox-multilingual
#
# Supported languages (pass as lang_code in the synthesis request):
#   ar da de el en es fi fr he hi it ja ko ms nl no pl pt ru sv sw tr zh
#
# Notes:
#   - Full feature parity with chatterbox_full_backend:
#     chunking, conditioning cache, prior speech tokens, blend, instrumentation
#   - language_id is passed to the tokenizer for each chunk — it prepends a
#     language tag token that conditions the T3 model on the target language
#   - punc_norm() from mtl_tts is applied to each chunk before tokenization,
#     matching the behaviour of ChatterboxMultilingualTTS.generate()

from __future__ import annotations

import asyncio
import logging
import os
import re
import tempfile
from collections import OrderedDict
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..utils import compute_file_hash

log = logging.getLogger(__name__)

# ISO 639-1 codes supported by ChatterboxMultilingualTTS
SUPPORTED_LANGUAGES = [
    "ar", "da", "de", "el", "en", "es", "fi", "fr",
    "he", "hi", "it", "ja", "ko", "ms", "nl", "no",
    "pl", "pt", "ru", "sv", "sw", "tr", "zh",
]

# ── Transparent sentence-level chunking ───────────────────────────────────────
# Same limits as chatterbox_full_backend — ~400 chars / ~65 words before
# truncation/hallucination become likely (benchmark data, April 2026).

_CB_CHUNK_TARGET_CHARS = int(os.environ.get("RRV_CB_CHUNK_TARGET_CHARS", "380"))
_CB_CHUNK_HARD_CHARS   = int(os.environ.get("RRV_CB_CHUNK_HARD_CHARS",   "480"))

_SENT_END = re.compile(r'(?<=[.!?])\s+')
_CLAUSE   = re.compile(r'(?<=[,;:])\s+')


def _split_into_chunks(text: str,
                       target: int = _CB_CHUNK_TARGET_CHARS,
                       hard: int   = _CB_CHUNK_HARD_CHARS) -> list[str]:
    """
    Split text into chunks at sentence boundaries, targeting target chars each.
    Falls back to clause boundaries when a sentence is itself oversized.
    Never splits mid-word. Returns a list of non-empty strings.
    """
    sentences = [s.strip() for s in _SENT_END.split(text) if s.strip()]
    chunks: list[str] = []
    current = ""

    for sent in sentences:
        if len(sent) > hard:
            clauses = [c.strip() for c in _CLAUSE.split(sent) if c.strip()]
            for clause in clauses:
                if len(clause) > hard:
                    words = clause.split()
                    part = ""
                    for w in words:
                        candidate = (part + " " + w).strip()
                        if len(candidate) > hard and part:
                            if current:
                                chunks.append(current.strip())
                                current = ""
                            chunks.append(part.strip())
                            part = w
                        else:
                            part = candidate
                    if part:
                        candidate = (current + " " + part).strip() if current else part
                        if len(candidate) <= hard:
                            current = candidate
                        else:
                            if current:
                                chunks.append(current.strip())
                            current = part
                else:
                    candidate = (current + " " + clause).strip() if current else clause
                    if len(candidate) <= target:
                        current = candidate
                    else:
                        if current:
                            chunks.append(current.strip())
                        current = clause
        else:
            candidate = (current + " " + sent).strip() if current else sent
            if len(candidate) <= target:
                current = candidate
            else:
                if current:
                    chunks.append(current.strip())
                current = sent

    if current.strip():
        chunks.append(current.strip())

    return [c for c in chunks if c]


class ChatterboxMultilingualBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str, max_concurrent: int = 2,
                 cond_cache_dir: Path | None = None) -> None:
        self._models_dir     = models_dir
        self._torch_device   = torch_device
        self._model          = None
        self._model_version  = ""
        self._MAX_CONCURRENT = max_concurrent
        # asyncio.Condition must be created inside a running event loop.
        # Initialized lazily in load() which runs inside asyncio.run().
        self._voice_cond: asyncio.Condition | None = None
        self._active_voice_key: str | None = None
        self._active_count   = 0

        # Single-sample in-process cache (hash of last loaded sample)
        self._cond_sample_hash: str = ""
        self._cond_tmp_wav: str = ""

        # Two-level voice conditioning cache (memory + disk)
        self._cond_mem_cache: OrderedDict = OrderedDict()
        self._cond_cache_dir: Path = (
            Path(cond_cache_dir) if cond_cache_dir is not None
            else Path("../data/cond_cache")
        )

        # Prior speech token context — keyed by voice identity string.
        self._prior_speech_tokens: dict = {}

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or "en"
        return f"{sample_key}|{lang_key}"

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

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "chatterbox_multilingual"

    @property
    def display_name(self) -> str:
        return "Chatterbox Multilingual"

    @property
    def supports_base_voices(self) -> bool:
        return False

    @property
    def supports_voice_matching(self) -> bool:
        return True

    @property
    def supports_voice_blending(self) -> bool:
        return True

    @property
    def supports_inline_pronunciation(self) -> bool:
        return False

    @property
    def languages(self) -> list[str]:
        return SUPPORTED_LANGUAGES

    @property
    def model_version(self) -> str:
        return self._model_version

    def extra_controls(self) -> dict:
        return {
            "cfg_weight": {
                "type":        "float",
                "default":     0.5,
                "min":         0.0,
                "max":         3.0,
                "description": "Classifier-free guidance weight for prompt adherence.",
            },
            "exaggeration": {
                "type":        "float",
                "default":     0.5,
                "min":         0.0,
                "max":         3.0,
                "description": "Emotion and expressiveness control. 0.0=monotone, 0.5=natural, 1.0+=dramatic.",
            },
            "cb_temperature": {
                "type": "float", "default": 0.8, "min": 0.1, "max": 2.0,
                "description": "T3 token sampling temperature. Lower=stable/consistent, higher=expressive/variable.",
            },
            "cb_top_p": {
                "type": "float", "default": 1.0, "min": 0.01, "max": 1.0,
                "description": "Nucleus sampling cutoff. Lower=more conservative token selection.",
            },
            "cb_repetition_penalty": {
                "type": "float", "default": 1.2, "min": 1.0, "max": 3.0,
                "description": "Penalizes repeated tokens. Raise to 1.5-2.0 if model loops or hallucinates.",
            },
            "language_id": {
                "type":        "string",
                "default":     "en",
                "options":     SUPPORTED_LANGUAGES,
                "description": (
                    "Language code for synthesis. Standard ISO 639-1 codes (e.g. 'en', 'fr', 'de') "
                    "are the documented set. Extended variants such as 'en-gb', 'en-us' "
                    "are normalized to base code automatically."
                ),
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        if self._voice_cond is None:
            self._voice_cond = asyncio.Condition()
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "Chatterbox Multilingual loaded: model_version=%s device=%s languages=%d",
            self._model_version, self._torch_device, len(SUPPORTED_LANGUAGES),
        )

                # Warm up torch.compile on T3 at load time — triggers CUDA kernel
        # compilation now rather than stalling the first user request.
        # Runs a minimal dummy inference (2 tokens) with no audio output.
        import os
        if os.environ.get("RRV_T3_COMPILE", "1") == "1":
            await loop.run_in_executor(None, self._warmup_t3_compile)

    def _warmup_t3_compile(self) -> None:
        """
        Run a minimal T3 inference to trigger torch.compile warmup.
        Uses a dummy speaker embedding and short text so no audio is produced.
        Warmup cost: ~10-30s on first server start, paid once per process.
        """
        import os
        try:
            import torch
            from chatterbox.models.t3.modules.cond_enc import T3Cond
            log.info(
                "Chatterbox T3: torch.compile warmup starting — "
                "this may take 10-30s on first run"
            )
            device = self._torch_device
            dim = self._model.t3.hp.n_channels
            # Minimal dummy T3Cond — zero speaker embedding
            dummy_t3_cond = T3Cond(
                speaker_emb=torch.zeros(1, 1, 256, device=device),
                cond_prompt_speech_tokens=torch.zeros(
                    1, self._model.t3.hp.speech_cond_prompt_len,
                    dtype=torch.long, device=device),
                emotion_adv=torch.tensor([[[0.5]]], device=device),
            ).to(device=device)
            # Minimal text: SOT + one token + EOT
            hp = self._model.t3.hp
            text_tokens = torch.tensor(
                [[hp.start_text_token, 100, hp.stop_text_token]],
                dtype=torch.long, device=device)
            # CFG doubles batch
            text_tokens = torch.cat([text_tokens, text_tokens], dim=0)
            with torch.inference_mode():
                self._model.t3.inference(
                    t3_cond=dummy_t3_cond,
                    text_tokens=text_tokens,
                    max_new_tokens=2,   # just enough to trigger compile
                    cfg_weight=0.5,
                    temperature=0.8,
                )
            log.info("Chatterbox T3: torch.compile warmup complete")
        except Exception as e:
            log.warning("Chatterbox T3: compile warmup failed (non-fatal): %s", e)



    def _warmup_t3_compile(self) -> None:
        """
        Run a minimal T3 inference to trigger torch.compile warmup.
        Uses a dummy speaker embedding and short text so no audio is produced.
        Warmup cost: ~10-30s on first server start, paid once per process.
        """
        import os
        try:
            import torch
            from chatterbox.models.t3.modules.cond_enc import T3Cond
            log.info(
                "Chatterbox T3: torch.compile warmup starting — "
                "this may take 10-30s on first run"
            )
            device = self._torch_device
            dim = self._model.t3.hp.n_channels
            # Minimal dummy T3Cond — zero speaker embedding
            dummy_t3_cond = T3Cond(
                speaker_emb=torch.zeros(1, 1, 256, device=device),
                cond_prompt_speech_tokens=torch.zeros(
                    1, self._model.t3.hp.speech_cond_prompt_len,
                    dtype=torch.long, device=device),
                emotion_adv=torch.tensor([[[0.5]]], device=device),
            ).to(device=device)
            # Minimal text: SOT + one token + EOT
            hp = self._model.t3.hp
            text_tokens = torch.tensor(
                [[hp.start_text_token, 100, hp.stop_text_token]],
                dtype=torch.long, device=device)
            # CFG doubles batch
            text_tokens = torch.cat([text_tokens, text_tokens], dim=0)
            with torch.inference_mode():
                self._model.t3.inference(
                    t3_cond=dummy_t3_cond,
                    text_tokens=text_tokens,
                    max_new_tokens=2,   # just enough to trigger compile
                    cfg_weight=0.5,
                    temperature=0.8,
                )
            log.info("Chatterbox T3: torch.compile warmup complete")
        except Exception as e:
            log.warning("Chatterbox T3: compile warmup failed (non-fatal): %s", e)


    def _load_sync(self) -> None:
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _original_load = librosa.load
            def _float32_load(path, *args, **kwargs):
                y, sr = _original_load(path, *args, **kwargs)
                return y.astype(np.float32), sr
            librosa.load = _float32_load
            librosa._rrv_patched = True
            log.info("Chatterbox Multilingual: patched librosa.load -> float32")

        try:
            from chatterbox.mtl_tts import ChatterboxMultilingualTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed or does not include mtl_tts. "
                "Run: pip install chatterbox-tts>=0.1.7"
            )

        local_model_dir = self._models_dir / "chatterbox-ml"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox Multilingual: loading from %s", local_model_dir)
            self._model = ChatterboxMultilingualTTS.from_local(
                str(local_model_dir),
                self._torch_device,
            )
            import torch as _torch
            _torch.set_float32_matmul_precision("high")
            self._patch_mel_filters()
            self._patch_t3_hidden_states()
            self._patch_t3_inference()
            self._patch_watermarker()
            import hashlib
            files = sorted(str(p) for p in local_model_dir.rglob("*.safetensors"))
            self._model_version = (
                hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
                if files else "local"
            )
        else:
            raise RuntimeError(
                f"Chatterbox Multilingual model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-multilingual\n"
                f"Place all files in: {local_model_dir}"
            )

    # ── Patches ───────────────────────────────────────────────────────────────

    def _patch_watermarker(self) -> None:
        """
        No-op the Perth implicit watermarker.

        Resemble AI embeds an imperceptible steganographic watermark in every
        generated waveform via perth.PerthImplicitWatermarker. This is a CPU
        signal-processing pass that runs on every chunk after S3Gen inference.
        Since audio never leaves the local network, the watermark serves no
        purpose and adds unnecessary per-chunk overhead.
        """
        if self._model is not None and hasattr(self._model, 'watermarker'):
            self._model.watermarker.apply_watermark = lambda wav, sample_rate=None: wav
            log.info("Chatterbox Multilingual: Perth watermarker disabled (no-op patch applied)")


    def _patch_t3_inference(self) -> None:
        """
        Patch T3.inference() for maximum generation throughput.

        Three independent improvements applied as a single monkey-patch:

        1. BUILD T3HuggingfaceBackend ONCE AT LOAD TIME
           The original code sets self.compiled = False at the top of every
           inference() call, then rebuilds the T3HuggingfaceBackend wrapper
           unconditionally. That wrapper construction is cheap but pointless
           churn. We build it once here and reuse it.

        2. StaticCache INSTEAD OF DynamicCache
           HuggingFace's default DynamicCache grows by appending tensors every
           token — each step triggers a new allocation and a cat() on every
           layer's K and V tensors. StaticCache pre-allocates the full
           [batch, num_heads, max_seq_len, head_dim] buffer once and writes
           in-place. For a 500-token generation across 30 layers this
           eliminates ~15,000 tensor allocations.

           max_seq_len = cond_len(~150) + text_len(~100) + bos(1) + max_new_tokens(1000)
           Padded to RRV_T3_STATIC_CACHE_LEN (default 1400, CFG doubles to 2800).
           Override with RRV_T3_STATIC_CACHE_LEN env var if you hit OOM or need
           longer sequences.

        3. REMOVE output_attentions=True FROM THE GENERATION LOOP
           The original loop passes output_attentions=True on every single
           token forward pass. This materializes full [batch, heads, seq, seq]
           attention weight matrices every step that are never used downstream.
           Pure allocation and compute overhead. Removed entirely.

        StaticCache + torch.compile:
           StaticCache is a prerequisite for torch.compile because it provides
           static tensor shapes throughout the loop. We wrap the single-token
           forward pass in torch.compile(mode='reduce-overhead') which fuses
           the CUDA kernels across the 30 LlamaDecoder layers into a single
           launch. First call per process triggers compilation (~10-30s on
           3080); subsequent calls use the compiled graph.
           Disable with RRV_T3_COMPILE=0 env var.
        """
        import os
        import types
        import torch

        if self._model is None:
            return

        # ── Check StaticCache availability ────────────────────────────────────
        try:
            from transformers.cache_utils import StaticCache
            has_static_cache = True
        except ImportError:
            has_static_cache = False
            log.warning(
                "Chatterbox: StaticCache not available in this transformers version "
                "(need >=4.36). Falling back to DynamicCache."
            )

        use_compile = os.environ.get("RRV_T3_COMPILE", "1") == "1"
        static_cache_len = int(os.environ.get("RRV_T3_STATIC_CACHE_LEN", "1400"))
        # CFG runs batch=2, so StaticCache must be sized for batch=2
        cfg_batch = 2

        t3 = self._model.t3

        # ── Build T3HuggingfaceBackend once ───────────────────────────────────
        from chatterbox.models.t3.inference.t3_hf_backend import T3HuggingfaceBackend

        alignment_stream_analyzer = None
        if t3.hp.is_multilingual:
            # AlignmentStreamAnalyzer needs text_tokens_slice which is
            # input-dependent — can't build it at load time for multilingual.
            # We skip the persistent backend for multilingual and let the
            # original code path handle it. The other optimizations still apply.
            log.info(
                "Chatterbox: multilingual model — skipping persistent "
                "T3HuggingfaceBackend (AlignmentStreamAnalyzer is input-dependent)"
            )
            persistent_backend = None
        else:
            persistent_backend = T3HuggingfaceBackend(
                config=t3.cfg,
                llama=t3.tfmr,
                speech_enc=t3.speech_emb,
                speech_head=t3.speech_head,
                alignment_stream_analyzer=None,
            )
            log.info("Chatterbox: T3HuggingfaceBackend built once at load time")

        # ── Build the patched inference() ─────────────────────────────────────
        _has_static_cache = has_static_cache
        _use_compile      = use_compile
        _static_cache_len = static_cache_len
        _cfg_batch        = cfg_batch
        _persistent_backend = persistent_backend
        _log              = log

        def _patched_inference(
            self_t3,
            *,
            t3_cond,
            text_tokens,
            initial_speech_tokens=None,
            prepend_prompt_speech_tokens=None,
            num_return_sequences=1,
            max_new_tokens=None,
            stop_on_eos=True,
            do_sample=True,
            temperature=0.8,
            top_p=0.95,
            min_p=0.05,
            length_penalty=1.0,
            repetition_penalty=1.2,
            cfg_weight=0.5,
        ):
            from transformers.generation.logits_process import (
                RepetitionPenaltyLogitsProcessor,
                TopPLogitsWarper,
                MinPLogitsWarper,
            )
            from tqdm import tqdm

            assert prepend_prompt_speech_tokens is None, "not implemented"

            # Ensure BOT/EOT present
            text_tokens = torch.atleast_2d(text_tokens).to(
                dtype=torch.long, device=self_t3.device)

            if initial_speech_tokens is None:
                initial_speech_tokens = (
                    self_t3.hp.start_speech_token
                    * torch.ones_like(text_tokens[:, :1])
                )

            embeds, len_cond = self_t3.prepare_input_embeds(
                t3_cond=t3_cond,
                text_tokens=text_tokens,
                speech_tokens=initial_speech_tokens,
                cfg_weight=cfg_weight,
            )

            # ── Select / build backend ────────────────────────────────────────
            if _persistent_backend is not None:
                patched_model = _persistent_backend
                # Reset _added_cond flag for this inference call
                patched_model._added_cond = False
            else:
                # Multilingual: rebuild with input-dependent analyzer
                from chatterbox.models.t3.inference.alignment_stream_analyzer import (
                    AlignmentStreamAnalyzer)
                analyzer = AlignmentStreamAnalyzer(
                    self_t3.tfmr,
                    None,
                    text_tokens_slice=(len_cond, len_cond + text_tokens.size(-1)),
                    alignment_layer_idx=9,
                    eos_idx=self_t3.hp.stop_speech_token,
                )
                patched_model = T3HuggingfaceBackend(
                    config=self_t3.cfg,
                    llama=self_t3.tfmr,
                    speech_enc=self_t3.speech_emb,
                    speech_head=self_t3.speech_head,
                    alignment_stream_analyzer=analyzer,
                )

            device = embeds.device
            max_new_tokens = max_new_tokens or self_t3.hp.max_speech_tokens

            bos_token = torch.tensor(
                [[self_t3.hp.start_speech_token]], dtype=torch.long, device=device)
            bos_embed = self_t3.speech_emb(bos_token)
            bos_embed = bos_embed + self_t3.speech_pos_emb.get_fixed_embedding(0)
            bos_embed = torch.cat([bos_embed, bos_embed])  # CFG batch=2

            inputs_embeds = torch.cat([embeds, bos_embed], dim=1)

            generated_ids = bos_token.clone()
            predicted = []

            top_p_warper = TopPLogitsWarper(top_p=top_p)
            min_p_warper = MinPLogitsWarper(min_p=min_p)
            repetition_penalty_processor = RepetitionPenaltyLogitsProcessor(
                penalty=float(repetition_penalty))

            # ── Build StaticCache if available ────────────────────────────────
            past = None
            if _has_static_cache:
                try:
                    from transformers.cache_utils import StaticCache
                    # seq_len = context (embeds) + bos + max_new_tokens
                    context_len = inputs_embeds.size(1)
                    total_len = context_len + max_new_tokens
                    # Pad up to configured static length, take the larger
                    cache_len = max(total_len, _static_cache_len)
                    past = StaticCache(
                        config=self_t3.cfg,
                        max_batch_size=_cfg_batch,
                        max_cache_len=cache_len,
                        device=device,
                        dtype=embeds.dtype,
                    )
                    _log.debug(
                        "Chatterbox T3: StaticCache allocated batch=%d len=%d",
                        _cfg_batch, cache_len,
                    )
                except Exception as e:
                    _log.warning(
                        "Chatterbox T3: StaticCache init failed (%s) — "
                        "falling back to DynamicCache", e)
                    past = None

            # ── Initial forward pass ──────────────────────────────────────────
            output = patched_model(
                inputs_embeds=inputs_embeds,
                past_key_values=past,
                use_cache=True,
                output_attentions=False,   # ← was True, never used
                output_hidden_states=True,
                return_dict=True,
            )
            past = output.past_key_values

            # ── Optionally compile the single-token forward pass ──────────────
            if _use_compile and not getattr(patched_model, '_rrv_compiled', False):
                try:
                    patched_model._compiled_forward = torch.compile(
                        patched_model,
                        mode="reduce-overhead",
                        fullgraph=False,
                    )
                    patched_model._rrv_compiled = True
                    _log.info(
                        "Chatterbox T3: torch.compile applied to single-token "
                        "forward pass (first call will warm up)"
                    )
                except Exception as e:
                    _log.warning(
                        "Chatterbox T3: torch.compile failed (%s) — "
                        "running uncompiled", e)
                    patched_model._compiled_forward = patched_model

            step_forward = getattr(patched_model, '_compiled_forward', patched_model)

            # ── Generation loop ───────────────────────────────────────────────
            for i in tqdm(range(max_new_tokens), desc="Sampling", dynamic_ncols=True):
                logits_step = output.logits[:, -1, :]
                cond   = logits_step[0:1, :]
                uncond = logits_step[1:2, :]
                cfg    = torch.as_tensor(cfg_weight, device=cond.device, dtype=cond.dtype)
                logits = cond + cfg * (cond - uncond)

                if patched_model.alignment_stream_analyzer is not None:
                    if logits.dim() == 1:
                        logits = logits.unsqueeze(0)
                    last_token = (generated_ids[0, -1].item()
                                  if len(generated_ids[0]) > 0 else None)
                    logits = patched_model.alignment_stream_analyzer.step(
                        logits, next_token=last_token)

                ids_for_proc = generated_ids[:1, ...]
                logits = repetition_penalty_processor(ids_for_proc, logits)

                if temperature != 1.0:
                    logits = logits / temperature

                logits = min_p_warper(ids_for_proc, logits)
                logits = top_p_warper(ids_for_proc, logits)

                probs = torch.softmax(logits, dim=-1)
                next_token = torch.multinomial(probs, num_samples=1)

                predicted.append(next_token)
                generated_ids = torch.cat([generated_ids, next_token], dim=1)

                if next_token.view(-1) == self_t3.hp.stop_speech_token:
                    _log.info(
                        f"✅ EOS token detected! Stopping generation at step {i+1}")
                    break

                next_token_embed = self_t3.speech_emb(next_token)
                next_token_embed = (next_token_embed
                                    + self_t3.speech_pos_emb.get_fixed_embedding(i + 1))
                next_token_embed = torch.cat([next_token_embed, next_token_embed])

                output = step_forward(
                    inputs_embeds=next_token_embed,
                    past_key_values=past,
                    use_cache=True,
                    output_attentions=False,   # ← was True, never used
                    output_hidden_states=True,
                    return_dict=True,
                )
                past = output.past_key_values

            predicted_tokens = torch.cat(predicted, dim=1)
            return predicted_tokens

        # Bind the patched inference as a method on the T3 instance
        t3.inference = types.MethodType(_patched_inference, t3)
        log.info(
            "Chatterbox: T3 inference patched — "
            "static_cache=%s compile=%s cache_len=%d",
            has_static_cache, use_compile, static_cache_len,
        )

    def _patch_mel_filters(self) -> None:
        """Force float32 through Chatterbox's pipeline."""
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _orig = librosa.load
            def _f32(path, *a, **kw):
                y, sr = _orig(path, *a, **kw)
                return y.astype(np.float32), sr
            librosa.load = _f32
            librosa._rrv_patched = True

        try:
            import torch
            import torch.nn.functional as F
            from chatterbox.models.s3tokenizer.s3tokenizer import S3Tokenizer

            if not getattr(S3Tokenizer, '_rrv_patched', False):
                orig_log_mel = S3Tokenizer.log_mel_spectrogram

                def _patched_log_mel(self_t, audio, padding=0):
                    if not torch.is_tensor(audio):
                        audio = torch.from_numpy(audio)
                    audio = audio.to(self_t.device)
                    if padding > 0:
                        audio = F.pad(audio, (0, padding))
                    stft = torch.stft(
                        audio, self_t.n_fft,
                        orig_log_mel.__globals__.get('S3_HOP', 160),
                        window=self_t.window.to(self_t.device),
                        return_complex=True,
                    )
                    magnitudes = stft[..., :-1].abs() ** 2
                    mel_filters = self_t._mel_filters.to(self_t.device)
                    magnitudes = magnitudes.to(dtype=mel_filters.dtype)
                    mel_spec = mel_filters @ magnitudes
                    log_spec = torch.clamp(mel_spec, min=1e-10).log10()
                    log_spec = torch.maximum(log_spec, log_spec.max() - 8.0)
                    log_spec = (log_spec + 4.0) / 4.0
                    return log_spec

                S3Tokenizer.log_mel_spectrogram = _patched_log_mel
                S3Tokenizer._rrv_patched = True
                log.debug("Chatterbox Multilingual: patched S3Tokenizer.log_mel_spectrogram")
        except Exception as e:
            log.warning("Chatterbox Multilingual: could not patch S3Tokenizer: %s", e)

        try:
            from chatterbox.models.voice_encoder.voice_encoder import VoiceEncoder
            import numpy as np

            if not getattr(VoiceEncoder, '_rrv_patched', False):
                _orig_embeds = VoiceEncoder.embeds_from_wavs

                def _patched_embeds(self_ve, wavs, *args, **kwargs):
                    wavs = [w.astype(np.float32) if hasattr(w, 'astype') else w for w in wavs]
                    return _orig_embeds(self_ve, wavs, *args, **kwargs)

                VoiceEncoder.embeds_from_wavs = _patched_embeds
                VoiceEncoder._rrv_patched = True
                log.debug("Chatterbox Multilingual: patched VoiceEncoder.embeds_from_wavs -> float32")
        except Exception as e:
            log.warning("Chatterbox Multilingual: could not patch VoiceEncoder: %s", e)

    def _patch_t3_hidden_states(self) -> None:
        """
        Patch T3HuggingfaceBackend.forward for transformers 4.57.x compatibility.
        See chatterbox_full_backend._patch_t3_hidden_states for full explanation.
        """
        try:
            from chatterbox.models.t3.inference.t3_hf_backend import T3HuggingfaceBackend
            from transformers.modeling_outputs import CausalLMOutputWithCrossAttentions

            if getattr(T3HuggingfaceBackend, '_rrv_hidden_states_patched', False):
                return

            _orig_forward = T3HuggingfaceBackend.forward

            def _patched_forward(self_t3, inputs_embeds, past_key_values=None,
                                 use_cache=True, output_attentions=False,
                                 output_hidden_states=True, return_dict=True):
                import torch
                from transformers.modeling_outputs import CausalLMOutputWithCrossAttentions
                import logging as _logging
                _log = _logging.getLogger(__name__)

                tfmr_out = self_t3.model(
                    inputs_embeds=inputs_embeds,
                    past_key_values=past_key_values,
                    use_cache=use_cache,
                    output_attentions=output_attentions,
                    output_hidden_states=output_hidden_states,
                    return_dict=True,
                )
                if tfmr_out.hidden_states is not None:
                    hidden_states = tfmr_out.hidden_states[-1]
                else:
                    hidden_states = tfmr_out.last_hidden_state
                    _log.debug("Chatterbox Multilingual T3: hidden_states was None, using last_hidden_state "
                               "(transformers 4.57.x compatibility)")

                logits = self_t3.speech_head(hidden_states)
                return CausalLMOutputWithCrossAttentions(
                    logits=logits,
                    past_key_values=tfmr_out.past_key_values,
                    hidden_states=tfmr_out.hidden_states,
                    attentions=tfmr_out.attentions,
                )

            T3HuggingfaceBackend.forward = _patched_forward
            T3HuggingfaceBackend._rrv_hidden_states_patched = True
            log.info("Chatterbox Multilingual: patched T3HuggingfaceBackend.forward for transformers 4.57.x compatibility")

        except Exception as e:
            log.warning("Chatterbox Multilingual: could not patch T3HuggingfaceBackend.forward: %s", e)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox Multilingual backend is not loaded")

        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        if not blend_entries and request.sample_path is None:
            raise ValueError(
                "Chatterbox Multilingual requires a reference audio clip. "
                "Provide sample_id in the request, or use voice.type='blend'."
            )

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        finally:
            await self._release_voice_slot(voice_key)

        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _setup_blend_generate(self, t3_cond, gen_dict: dict) -> None:
        """
        Install the generate() bypass for blend/cache-loaded conditionals.
        Identical to chatterbox_full_backend but passes language_id to tokenizer.
        """
        import types as _types
        _t3_ref  = t3_cond
        _gen_ref = gen_dict

        def _patched_generate(self_m, text, language_id="en", repetition_penalty=1.2,
                               min_p=0.05, top_p=1.0, audio_prompt_path=None,
                               exaggeration=0.5, cfg_weight=0.5, temperature=0.8):
            import torch as _torch
            import torch.nn.functional as F
            from chatterbox.models.s3tokenizer import drop_invalid_tokens
            try:
                from chatterbox.mtl_tts import punc_norm
                text = punc_norm(text)
            except Exception:
                pass
            self_m.conds.t3  = _t3_ref
            self_m.conds.gen = _gen_ref
            text_proc = self_m.tokenizer.text_to_tokens(
                text, language_id=language_id).to(self_m.device)
            text_proc = _torch.cat([text_proc, text_proc], dim=0)
            sot = self_m.t3.hp.start_text_token
            eot = self_m.t3.hp.stop_text_token
            text_proc = F.pad(text_proc, (1, 0), value=sot)
            text_proc = F.pad(text_proc, (0, 1), value=eot)
            with _torch.inference_mode():
                speech_tokens = self_m.t3.inference(
                    t3_cond=self_m.conds.t3,
                    text_tokens=text_proc,
                    max_new_tokens=1000,
                    temperature=temperature,
                    cfg_weight=cfg_weight,
                    repetition_penalty=repetition_penalty,
                    min_p=min_p,
                    top_p=top_p,
                )
                speech_tokens = drop_invalid_tokens(speech_tokens[0])
                speech_tokens = speech_tokens[speech_tokens < 6561].to(self_m.device)
                wav, _ = self_m.s3gen.inference(
                    speech_tokens=speech_tokens,
                    ref_dict=self_m.conds.gen,
                )
                wav = wav.squeeze(0).detach().cpu().numpy()
                wav = self_m.watermarker.apply_watermark(wav, sample_rate=self_m.sr)
            return _torch.from_numpy(wav).unsqueeze(0)

        self._model._rrv_blend_generate = _types.MethodType(_patched_generate, self._model)
        self._is_blend_active = True

    def _blend_conditionals_inner(self, blend: list[dict], exaggeration: float) -> None:
        """
        Blend voice conditionals from multiple reference samples.
        Identical to chatterbox_full_backend._blend_conditionals_inner.
        """
        import torch, tempfile, os
        import soundfile as sf

        sample_entries = [e for e in blend if e.get("sample_path")]
        if not sample_entries:
            raise ValueError("_blend_conditionals: no sample_path entries in blend")

        total_w = sum(e["weight"] for e in sample_entries)
        entries = [(e["sample_path"], e["weight"] / total_w) for e in sample_entries]
        primary_path = max(entries, key=lambda x: x[1])[0]
        entries_sorted = sorted(entries, key=lambda x: x[0] == primary_path)

        t3_speaker_embs = []
        gen_embeddings  = []
        tmp_wavs = []

        try:
            for sample_path_str, weight in entries_sorted:
                audio_data, sr = sf.read(str(sample_path_str), dtype="float32")
                if audio_data.ndim > 1:
                    audio_data = audio_data.mean(axis=1)
                with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
                    tmp_path = tmp.name
                sf.write(tmp_path, audio_data, sr, subtype="PCM_16")
                tmp_wavs.append(tmp_path)

                self._model.prepare_conditionals(tmp_path, exaggeration=exaggeration)

                t3_spk = self._model.conds.t3.speaker_emb.detach().clone()
                t3_speaker_embs.append((t3_spk, weight))

                if "embedding" in self._model.conds.gen:
                    gen_emb = self._model.conds.gen["embedding"].detach().clone()
                    gen_embeddings.append((gen_emb, weight))

            blended_t3_spk = sum(emb * w for emb, w in t3_speaker_embs)
            mean_mag = sum(emb.norm() * w for emb, w in t3_speaker_embs)
            b_norm = blended_t3_spk.norm()
            if b_norm > 1e-8:
                blended_t3_spk = blended_t3_spk / b_norm * mean_mag

            blended_gen_emb = None
            if gen_embeddings:
                blended_gen_emb = sum(emb * w for emb, w in gen_embeddings)
                mean_mag_gen = sum(emb.norm() * w for emb, w in gen_embeddings)
                g_norm = blended_gen_emb.norm()
                if g_norm > 1e-8:
                    blended_gen_emb = blended_gen_emb / g_norm * mean_mag_gen

            from chatterbox.models.t3.modules.cond_enc import T3Cond
            _primary_t3 = self._model.conds.t3
            blended_t3 = T3Cond(
                speaker_emb=blended_t3_spk,
                cond_prompt_speech_tokens=_primary_t3.cond_prompt_speech_tokens,
                emotion_adv=torch.tensor([[[exaggeration]]],
                                         dtype=blended_t3_spk.dtype,
                                         device=blended_t3_spk.device),
            ).to(device=self._torch_device)

            blended_gen = dict(self._model.conds.gen)
            if blended_gen_emb is not None:
                blended_gen["embedding"] = blended_gen_emb

            self._model.conds.t3  = blended_t3
            self._model.conds.gen = blended_gen
            self._setup_blend_generate(blended_t3, blended_gen)

            log.debug(
                "Chatterbox Multilingual blend: t3_speaker_emb + gen[embedding] blended (%d samples)",
                len(t3_speaker_embs)
            )

        finally:
            for tmp_path in tmp_wavs:
                try:
                    os.unlink(tmp_path)
                except Exception:
                    pass

        self._cond_sample_hash = ""

    # ── Voice Conditioning Cache ───────────────────────────────────────────────

    _COND_MEM_CACHE_SIZE = 4

    def _cond_key_single(self, sample_path, exaggeration: float) -> str:
        import hashlib
        h = hashlib.sha256(Path(str(sample_path)).read_bytes()).hexdigest()[:16]
        return f"{h}|ex:{exaggeration:.3f}"

    def _cond_key_blend(self, blend_entries: list[dict], exaggeration: float) -> str:
        import hashlib
        parts = sorted(
            f"{compute_file_hash(Path(e['sample_path']))[:16]}:{e['weight']:.4f}"
            for e in blend_entries if e.get("sample_path")
        )
        h = hashlib.sha256("|".join(parts).encode()).hexdigest()[:16]
        return f"blend_{h}|ex:{exaggeration:.3f}"

    def _cond_disk_path(self, cache_key: str) -> Path:
        safe = cache_key.replace("|", "_").replace(":", "-").replace(".", "p")
        return self._cond_cache_dir / f"{safe}.pt"

    def _cond_mem_get(self, cache_key: str):
        if cache_key in self._cond_mem_cache:
            self._cond_mem_cache.move_to_end(cache_key)
            return self._cond_mem_cache[cache_key]
        return None

    def _cond_mem_put(self, cache_key: str, t3_cond, gen_dict: dict) -> None:
        self._cond_mem_cache[cache_key] = (t3_cond, gen_dict)
        self._cond_mem_cache.move_to_end(cache_key)
        while len(self._cond_mem_cache) > self._COND_MEM_CACHE_SIZE:
            self._cond_mem_cache.popitem(last=False)

    def _cond_disk_save(self, cache_key: str, t3_cond, gen_dict: dict) -> None:
        import torch
        try:
            self._cond_cache_dir.mkdir(parents=True, exist_ok=True)
            data = {
                "t3": {k: v.detach().cpu() if torch.is_tensor(v) else v
                       for k, v in t3_cond.__dict__.items()},
                "gen": {k: v.detach().cpu() if torch.is_tensor(v) else v
                        for k, v in gen_dict.items()},
            }
            tmp = self._cond_disk_path(cache_key).with_suffix(".pt.tmp")
            torch.save(data, tmp)
            tmp.rename(self._cond_disk_path(cache_key))
            log.debug("Cond cache: saved to disk key=%s", cache_key[:20])
        except Exception as e:
            log.warning("Cond cache: disk save failed (%s)", e)

    def _cond_disk_load(self, cache_key: str):
        import torch
        from chatterbox.models.t3.modules.cond_enc import T3Cond
        p = self._cond_disk_path(cache_key)
        if not p.exists():
            return None
        try:
            data = torch.load(p, map_location=self._torch_device, weights_only=True)
            t3_cond = T3Cond(**data["t3"]).to(device=self._torch_device)
            gen_dict = {k: v.to(device=self._torch_device) if torch.is_tensor(v) else v
                        for k, v in data["gen"].items()}
            log.debug("Cond cache: loaded from disk key=%s", cache_key[:20])
            self._prior_speech_tokens.pop(cache_key, None)
            return t3_cond, gen_dict
        except Exception as e:
            log.warning("Cond cache: disk load failed (%s) — will recompute", e)
            try: p.unlink()
            except Exception: pass
            return None

    def _cond_get_or_compute_single(self, sample_path, exaggeration: float) -> tuple:
        """Memory → disk → full prepare_conditionals()."""
        cache_key = self._cond_key_single(sample_path, exaggeration)
        hit = self._cond_mem_get(cache_key)
        if hit:
            log.debug("Cond cache: memory HIT single key=%s", cache_key[:20])
            return hit
        hit = self._cond_disk_load(cache_key)
        if hit:
            self._cond_mem_put(cache_key, *hit)
            return hit
        log.info("Cond cache: MISS single — running prepare_conditionals key=%s", cache_key[:20])
        import soundfile as sf
        audio_data, sr = sf.read(str(sample_path), dtype="float32")
        if audio_data.ndim > 1:
            audio_data = audio_data.mean(axis=1)
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp_path = tmp.name
        try:
            sf.write(tmp_path, audio_data, sr, subtype="PCM_16")
            self._model.prepare_conditionals(tmp_path, exaggeration=exaggeration)
        finally:
            try: os.unlink(tmp_path)
            except Exception: pass
        t3_cond, gen_dict = self._model.conds.t3, self._model.conds.gen
        self._cond_mem_put(cache_key, t3_cond, gen_dict)
        self._cond_disk_save(cache_key, t3_cond, gen_dict)
        self._cond_sample_hash = cache_key
        return t3_cond, gen_dict

    def _cond_get_or_compute_blend(self, blend_entries: list[dict],
                                    exaggeration: float) -> tuple:
        """Memory → disk → full blend compute."""
        cache_key = self._cond_key_blend(blend_entries, exaggeration)
        hit = self._cond_mem_get(cache_key)
        if hit:
            log.debug("Cond cache: memory HIT blend key=%s", cache_key[:20])
            return hit
        hit = self._cond_disk_load(cache_key)
        if hit:
            self._cond_mem_put(cache_key, *hit)
            return hit
        log.info("Cond cache: MISS blend — computing conditionals key=%s", cache_key[:20])
        self._blend_conditionals_inner(blend_entries, exaggeration)
        t3_cond, gen_dict = self._model.conds.t3, self._model.conds.gen
        self._cond_mem_put(cache_key, t3_cond, gen_dict)
        self._cond_disk_save(cache_key, t3_cond, gen_dict)
        return t3_cond, gen_dict

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np

        cfg_weight         = request.cfg_weight          if request.cfg_weight          is not None else 0.5
        exaggeration       = request.exaggeration        if request.exaggeration        is not None else 0.5
        cfg_weight         = max(0.1, min(cfg_weight, 3.0))
        exaggeration       = max(0.1, min(exaggeration, 3.0))
        temperature        = request.cb_temperature      if request.cb_temperature      is not None else 0.8
        top_p              = request.cb_top_p            if request.cb_top_p            is not None else 1.0
        repetition_penalty = request.cb_repetition_penalty if request.cb_repetition_penalty is not None else 1.2

        # Resolve lang_code — normalize sub-locale, validate, fall back to en
        raw_lang  = (request.lang_code or "en").lower().strip()
        base_lang = raw_lang.split("-")[0]
        if raw_lang in SUPPORTED_LANGUAGES:
            lang = raw_lang
        elif base_lang in SUPPORTED_LANGUAGES:
            lang = base_lang
        else:
            log.warning(
                "Chatterbox Multilingual: unsupported lang_code='%s' — falling back to 'en'", raw_lang)
            lang = "en"

        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        if blend_entries:
            t3_cond, gen_dict = self._cond_get_or_compute_blend(blend_entries, exaggeration)
            self._model.conds.t3  = t3_cond
            self._model.conds.gen = gen_dict
            self._setup_blend_generate(t3_cond, gen_dict)
        else:
            if request.sample_path is None:
                raise ValueError("Chatterbox Multilingual requires either a reference sample or a blend.")
            t3_cond, gen_dict = self._cond_get_or_compute_single(
                request.sample_path, exaggeration)
            self._model.conds.t3  = t3_cond
            self._model.conds.gen = gen_dict
            self._is_blend_active = False
            if hasattr(self._model, "_rrv_blend_generate"):
                del self._model._rrv_blend_generate

        if blend_entries:
            _voice_key = self._cond_key_blend(blend_entries, exaggeration)
        else:
            _voice_key = self._cond_key_single(request.sample_path, exaggeration)

        if request.synthesis_seed is not None:
            import torch as _torch
            _torch.manual_seed(request.synthesis_seed)
            _torch.cuda.manual_seed_all(request.synthesis_seed)

        chunks = _split_into_chunks(request.text)
        total  = len(chunks)
        _progress_cb = request.progress_callback

        _SHORT_WORD_THRESHOLD = int(
            __import__('os').environ.get('RRV_CB_PRIOR_TOKEN_WORDS', '3'))
        _PRIOR_TOKEN_LEN = int(
            __import__('os').environ.get('RRV_CB_PRIOR_TOKEN_LEN', '75'))

        import torch as _torch
        import torch.nn.functional as _F
        from chatterbox.models.t3.modules.cond_enc import T3Cond as _T3Cond
        from chatterbox.models.s3tokenizer import drop_invalid_tokens as _drop_invalid

        # punc_norm from mtl_tts — applied per chunk before tokenization
        try:
            from chatterbox.mtl_tts import punc_norm as _punc_norm
        except Exception:
            _punc_norm = lambda t: t

        def _load_tail_tokens_for_cache_key(cache_key: str):
            if not cache_key or not request.cache_dir:
                return None, ""
            try:
                sidecar = Path(request.cache_dir) / self.provider_id / f"{cache_key}.tokens.pt"
                if not sidecar.exists():
                    return None, ""
                payload = _torch.load(sidecar, map_location='cpu')
                if isinstance(payload, dict):
                    tokens = payload.get('tokens')
                    sidecar_voice_key = payload.get('voice_key', '') or ''
                    sidecar_ctx = payload.get('voice_context', '') or ''
                    if sidecar_voice_key and sidecar_voice_key != _voice_key:
                        log.debug("Tail token sidecar voice mismatch for %s — ignoring", cache_key[:12])
                        return None, ""
                    return tokens, sidecar_ctx
                return payload, ""
            except Exception as _e:
                log.debug("Tail token sidecar load failed (non-fatal): %s", _e)
                return None, ""

        all_samples: list[np.ndarray] = []

        def _run_inference(chunk_text: str, active_t3_cond) -> tuple:
            """Run t3.inference() + s3gen.inference() directly, return (wav_np, tokens)."""
            normed = _punc_norm(chunk_text)
            log.info(
                "T3 inference start: chars=%d text=%r",
                len(chunk_text), chunk_text[:120]
            )
            text_proc = self._model.tokenizer.text_to_tokens(
                normed, language_id=lang).to(self._model.device)
            text_proc = _torch.cat([text_proc, text_proc], dim=0)
            sot = self._model.t3.hp.start_text_token
            eot = self._model.t3.hp.stop_text_token
            text_proc = _F.pad(text_proc, (1, 0), value=sot)
            text_proc = _F.pad(text_proc, (0, 1), value=eot)
            with _torch.inference_mode():
                speech_tokens = self._model.t3.inference(
                    t3_cond=active_t3_cond,
                    text_tokens=text_proc,
                    max_new_tokens=1000,
                    temperature=temperature,
                    cfg_weight=cfg_weight,
                    repetition_penalty=repetition_penalty,
                    min_p=0.05,
                    top_p=top_p,
                )
                clean = _drop_invalid(speech_tokens[0])
                clean = clean[clean < 6561].to(self._model.device)

                log.info(
                    "T3 inference done: tokens=%d has_nan=%s has_inf=%s min=%d max=%d",
                    clean.shape[-1],
                    bool(_torch.isnan(clean.float()).any()),
                    bool(_torch.isinf(clean.float()).any()),
                    int(clean.min()) if clean.numel() > 0 else -1,
                    int(clean.max()) if clean.numel() > 0 else -1,
                )

                if clean.numel() == 0:
                    log.error("T3 produced zero valid tokens for text=%r — skipping s3gen", chunk_text[:120])
                    return np.zeros(self._model.sr, dtype=np.float32), clean

                wav, _ = self._model.s3gen.inference(
                    speech_tokens=clean,
                    ref_dict=gen_dict,
                )
                log.info("S3Gen inference done: wav_samples=%d", wav.numel())
                wav_np = wav.squeeze(0).detach().cpu().numpy()
                wav_np = self._model.watermarker.apply_watermark(
                    wav_np, sample_rate=self._model.sr)
            return wav_np, clean

        try:
            for i, chunk_text in enumerate(chunks):
                if not chunk_text.strip():
                    continue

                word_count = len(chunk_text.split())
                _cur_ctx = request.voice_context or ""
                explicit_continue = bool(request.continue_from_cache_key)
                _prior_entry = self._prior_speech_tokens.get(_voice_key)
                prior_tokens = None
                _prior_ctx = ""

                if _prior_entry is not None:
                    prior_tokens, _prior_ctx = _prior_entry
                    if _prior_ctx != _cur_ctx and not explicit_continue:
                        prior_tokens = None
                        log.debug("Tail token context mismatch (%s vs %s) — using reference tokens",
                                  _prior_ctx, _cur_ctx)

                if explicit_continue and prior_tokens is None:
                    prior_tokens, _prior_ctx = _load_tail_tokens_for_cache_key(
                        request.continue_from_cache_key)
                    if prior_tokens is not None:
                        log.debug("Loaded prior tail tokens from disk for explicit continuation: %s",
                                  request.continue_from_cache_key[:12])

                use_prior = (
                    prior_tokens is not None
                    and (
                        explicit_continue
                        or (word_count <= _SHORT_WORD_THRESHOLD and bool(_cur_ctx))
                    )
                )

                if use_prior:
                    active_t3 = _T3Cond(
                        speaker_emb=t3_cond.speaker_emb,
                        cond_prompt_speech_tokens=prior_tokens,
                        emotion_adv=t3_cond.emotion_adv,
                    ).to(device=self._torch_device)
                    log.debug(
                        "Chatterbox Multilingual: chunk %d/%d primed (%d words) using %d prior tokens",
                        i + 1, total, word_count, prior_tokens.shape[-1])
                else:
                    active_t3 = t3_cond

                wav_np, clean_tokens = _run_inference(chunk_text, active_t3)

                tail = clean_tokens[-_PRIOR_TOKEN_LEN:].unsqueeze(0).detach()
                _ctx_tag = request.voice_context or ""
                self._prior_speech_tokens[_voice_key] = (tail, _ctx_tag)

                if total == 1 and request.cache_key and request.cache_dir:
                    try:
                        import torch as _ts
                        _sidecar_dir = Path(request.cache_dir) / self.provider_id
                        _sidecar_dir.mkdir(parents=True, exist_ok=True)
                        _sidecar_tmp = _sidecar_dir / f"{request.cache_key}.tokens.pt.tmp"
                        _sidecar     = _sidecar_dir / f"{request.cache_key}.tokens.pt"
                        _ts.save({
                            "tokens": tail.cpu(),
                            "voice_key": _voice_key,
                            "voice_context": _ctx_tag,
                        }, _sidecar_tmp)
                        _sidecar_tmp.rename(_sidecar)
                        log.debug("Tail tokens saved: %s", request.cache_key[:12])
                    except Exception as _e:
                        log.debug("Tail token sidecar write failed (non-fatal): %s", _e)

                samples = wav_np.squeeze()
                if samples.ndim > 1:
                    samples = samples.mean(axis=0)
                all_samples.append(samples.astype(np.float32))
                log.debug("Chatterbox Multilingual: chunk %d/%d synthesized (%d chars)",
                          i + 1, total, len(chunk_text))
                if _progress_cb is not None:
                    try:
                        _progress_cb(i + 1, total)
                    except Exception:
                        pass
        finally:
            if getattr(self, '_is_blend_active', False):
                self._is_blend_active = False
                if hasattr(self._model, '_rrv_blend_generate'):
                    del self._model._rrv_blend_generate

        if not all_samples:
            return pcm_to_ogg(np.zeros(self._model.sr, dtype=np.float32), self._model.sr)

        combined = np.concatenate(all_samples)
        return pcm_to_ogg(combined, self._model.sr)
