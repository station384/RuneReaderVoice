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
# server/backends/chatterbox_full_backend.py
#
# Chatterbox (full) backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Install (chatterbox-tts 0.1.7+, Python 3.11):
#   pip install chatterbox-tts
#   pip install --no-deps s3tokenizer
#   pip install onnx>=1.16.0
#
# Model files — place in data/models/chatterbox-hf/:
#   Download from: https://huggingface.co/ResembleAI/chatterbox-hf
#
# Supports:
#   - Zero-shot voice cloning from a reference audio clip
#   - 0.5B parameters — original Chatterbox model with CFG/exaggeration tuning
#   - CPU (slow) or CUDA/ROCm GPU
#
# Conditionals caching:
#   Chatterbox separates voice conditioning (prepare_conditionals) from text
#   generation. This backend caches the conditionals for the last-used sample
#   so that consecutive chunks for the same NPC voice reuse the identical voice
#   embedding rather than re-deriving it from the audio file each time. This
#   produces more consistent voice character across dialog chunks.
#
#   The voice slot serialization (same speaker runs concurrently, different
#   speakers are serialized) guarantees the cached conditionals always belong
#   to the speaker currently being synthesized — no cross-speaker contamination.
#
#   Cache invalidates when a different sample hash is seen. The PCM_16 temp WAV
#   is kept alive for the lifetime of the cache entry and deleted when evicted.

from __future__ import annotations

import asyncio
import logging
import os
import re
import tempfile
from collections import OrderedDict
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..utils import compute_file_hash

log = logging.getLogger(__name__)

# ── Transparent sentence-level chunking ───────────────────────────────────────
# Chatterbox has a practical ceiling of ~400 chars / ~65 words before truncation
# and hallucination become likely (benchmark data, April 2026).
# For longer inputs the backend splits at sentence boundaries, synthesizes each
# chunk independently against the same reference sample, and concatenates.
# Conditionals are cached after the first chunk so subsequent chunks are cheap.
# This is transparent to the client — full text in, single OGG out.

_CB_CHUNK_TARGET_CHARS = int(os.environ.get("RRV_CB_CHUNK_TARGET_CHARS", "380"))
_CB_CHUNK_HARD_CHARS   = int(os.environ.get("RRV_CB_CHUNK_HARD_CHARS",   "480"))

# Sentence-ending punctuation — split here preferentially
_SENT_END = re.compile(r'(?<=[.!?])\s+')
# Clause boundary — fallback if sentence split produces oversized chunks
_CLAUSE   = re.compile(r'(?<=[,;:])\s+')


def _split_into_chunks(text: str,
                       target: int = _CB_CHUNK_TARGET_CHARS,
                       hard: int   = _CB_CHUNK_HARD_CHARS) -> list[str]:
    """
    Split text into chunks at sentence boundaries, targeting target chars each.
    Falls back to clause boundaries when a sentence is itself oversized.
    Never splits mid-word. Returns a list of non-empty strings.

    target / hard are the soft and hard character limits per chunk.
    If a single sentence exceeds hard, it is split at the nearest clause
    boundary under hard, or at the hard limit as a last resort.
    """
    import re as _re

    # First pass: split at sentence endings
    sentences = [s.strip() for s in _SENT_END.split(text) if s.strip()]

    chunks: list[str] = []
    current = ""

    for sent in sentences:
        # If the sentence itself exceeds hard cap, split it further
        if len(sent) > hard:
            # Try clause boundaries first
            clauses = [c.strip() for c in _CLAUSE.split(sent) if c.strip()]
            for clause in clauses:
                if len(clause) > hard:
                    # Last resort: hard split at word boundary under hard cap
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



class ChatterboxFullBackend(AbstractTtsBackend):

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
        # Store tokens on CPU and bound the LRU cache; storing CUDA tensors here
        # retains VRAM forever as new voices/samples are used.
        self._prior_token_cache_size = max(0, int(os.environ.get("RRV_CB_PRIOR_TOKEN_CACHE_SIZE", "16")))
        self._prior_speech_tokens: OrderedDict[str, tuple[object, str]] = OrderedDict()

        # Persistent StaticCache — allocated once at load time, reset() between
        # inference calls instead of allocating/freeing every synthesis.
        # Eliminates PyTorch CUDA allocator pool bloat that accumulates over long
        # uptimes when large KV buffers are repeatedly allocated and released.
        self._static_cache = None        # transformers.cache_utils.StaticCache | None
        self._static_cache_len: int = 0  # max_cache_len this cache was built for

        # Hot CUDA cond cache — one slot holding the CUDA-device copy of the
        # most recently used conditioning tensors. Eliminates CPU→GPU transfer
        # on consecutive calls with the same voice (e.g. all segments in a batch).
        # The CPU mem cache (_cond_mem_cache) remains authoritative for LRU eviction
        # and multi-voice support; this is purely a same-voice fast path.
        # VRAM cost: ~few KB (T3Cond speaker_emb + gen_dict embedding/prompt tensors).
        self._cond_hot_key: str = ""          # cache key for the CUDA-resident conds
        self._cond_hot_t3 = None              # T3Cond on CUDA
        self._cond_hot_gen: dict | None = None  # gen_dict on CUDA

        # Tail-token sidecar persistence is useful for continuation across cache
        # hits / worker lifetimes, but torch.save can block on busy storage. Keep
        # it off the synthesis critical path by default.
        # Values: defer (default), sync, off.
        self._tail_sidecar_mode = os.environ.get("RRV_CB_TAIL_SIDECAR_WRITE", "defer").strip().lower()
        if self._tail_sidecar_mode not in {"defer", "sync", "off"}:
            self._tail_sidecar_mode = "defer"
        self._tail_sidecar_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="rrv-cb-tail-sidecar")

        # Render precision experiment. This affects model weights, conditionals,
        # output cache identity, conditioning cache identity, and tail-token sidecars.
        # Supported effective modes: fp32 (default), fp16. fp8 is recognized but
        # intentionally fails fast because the current Chatterbox/PyTorch op stack
        # does not support blanket float8 module inference safely.
        self._requested_precision = os.environ.get("RRV_CB_PRECISION", "fp32").strip().lower() or "fp32"
        if self._requested_precision not in {"fp32", "fp16", "t3_fp16", "fp16_full", "fp8"}:
            log.warning("Chatterbox precision: unsupported RRV_CB_PRECISION=%r; using fp32", self._requested_precision)
            self._requested_precision = "fp32"
        # fp16 now means the quality-safe mixed mode: T3 in fp16, S3Gen/HiFT-GAN in fp32.
        # Full S3Gen/HiFT-GAN fp16 is retained only as an explicit experiment because it
        # produced recognizable but choppy/cut-off audio during testing.
        self._render_precision = "t3_fp16" if self._requested_precision == "fp16" else self._requested_precision

        # Model load strategy experiment. The stock Chatterbox loader can move
        # each model to CUDA immediately after loading fp32 weights. For mixed
        # precision this can create a higher peak VRAM window than necessary:
        # fp32 tensors reach the GPU, then selected modules are converted.
        #
        # cpu_precision_gpu loads each major model on CPU, applies the desired
        # precision for that module, moves only that finalized module to GPU,
        # then releases the CPU state dict before loading the next module. This
        # targets peak VRAM/load pressure only; steady-state VRAM is governed by
        # the final module dtypes.
        #
        # Values:
        #   direct            = use ChatterboxTTS.from_local as before
        #   cpu_precision_gpu = CPU load -> convert selected precision -> GPU per module
        self._load_strategy = os.environ.get("RRV_CB_LOAD_STRATEGY", "direct").strip().lower() or "direct"
        if self._load_strategy not in {"direct", "cpu_precision_gpu"}:
            log.warning("Chatterbox load strategy: unsupported RRV_CB_LOAD_STRATEGY=%r; using direct", self._load_strategy)
            self._load_strategy = "direct"

    def _target_float_dtype(self):
        import torch
        if self._render_precision in {"fp16", "t3_fp16", "fp16_full"}:
            return torch.float16
        return torch.float32

    def _t3_float_dtype(self):
        import torch
        if self._render_precision in {"t3_fp16", "fp16_full"}:
            return torch.float16
        return torch.float32

    def _s3gen_float_dtype(self):
        import torch
        if self._render_precision == "fp16_full":
            return torch.float16
        return torch.float32

    def _move_model_to_target_device(self) -> None:
        """Move already-loaded model modules to final device/dtypes.

        Used by the CPU-load strategy after modules are loaded/converted on CPU.
        The stock direct strategy reaches the same final state through
        ChatterboxTTS.from_local(..., cuda) followed by _apply_model_precision().
        """
        if self._model is None:
            return
        import torch
        device = self._torch_device
        if hasattr(self._model, "ve") and self._model.ve is not None:
            self._model.ve.to(device=device).eval()
        if hasattr(self._model, "t3") and self._model.t3 is not None:
            self._model.t3.to(device=device, dtype=self._t3_float_dtype()).eval()
        if hasattr(self._model, "s3gen") and self._model.s3gen is not None:
            self._model.s3gen.to(device=device, dtype=self._s3gen_float_dtype()).eval()
        if getattr(self._model, "conds", None) is not None:
            try:
                t3_cond, gen_dict = self._cond_to_device(self._model.conds.t3, self._model.conds.gen)
                from chatterbox.tts import Conditionals
                self._model.conds = Conditionals(t3_cond, gen_dict)
            except Exception as e:
                log.warning("Chatterbox load strategy: failed to move built-in conditionals to target precision (%s)", e)
        self._model.device = device

    def _free_load_intermediate(self, name: str = "") -> None:
        """Drop temporary loader memory between major model parts."""
        import gc
        gc.collect()
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception:
            pass

    def _log_vram_checkpoint(self, label: str, *, reset_peak: bool = False) -> None:
        """Log CUDA memory at important load/warmup checkpoints.

        Purpose: make model-load experiments self-contained in logs.  The worker
        ready capability line reports final VRAM, but it does not show load-path
        behavior.  These checkpoints let us compare direct loading vs
        cpu_precision_gpu without needing to guess where memory moved.

        Values are PyTorch allocator numbers, not total process VRAM from NVML:
        - allocated = live tensors tracked by PyTorch
        - reserved  = CUDA caching allocator pool held by PyTorch
        - peak      = max allocated since last reset or process start

        `reset_peak=True` starts a fresh peak window after the checkpoint is
        logged.  Use it at the beginning of load/warmup phases.
        """
        try:
            import torch
            if not torch.cuda.is_available() or not str(self._torch_device).startswith("cuda"):
                return
            device = torch.device(self._torch_device)
            allocated = torch.cuda.memory_allocated(device) / (1024 * 1024)
            reserved = torch.cuda.memory_reserved(device) / (1024 * 1024)
            peak = torch.cuda.max_memory_allocated(device) / (1024 * 1024)
            log.info(
                "Chatterbox VRAM checkpoint: %s allocated=%.1fMiB reserved=%.1fMiB peak_allocated=%.1fMiB",
                label, allocated, reserved, peak,
            )
            if reset_peak:
                torch.cuda.reset_peak_memory_stats(device)
        except Exception as e:
            log.debug("Chatterbox VRAM checkpoint failed for %s: %s", label, e)

    def _log_runtime_profile(self) -> None:
        """One-line runtime profile for future diagnostics/new chat context."""
        static_cache = bool(self._static_cache is not None)
        try:
            import torch
            t3_dtype = getattr(next(self._model.t3.parameters()), "dtype", "unknown") if self._model and getattr(self._model, "t3", None) is not None else "none"
            s3_dtype = getattr(next(self._model.s3gen.parameters()), "dtype", "unknown") if self._model and getattr(self._model, "s3gen", None) is not None else "none"
        except Exception:
            t3_dtype = "unknown"
            s3_dtype = "unknown"
        self._log_vram_checkpoint("runtime-profile")
        log.info(
            "Chatterbox runtime profile: precision=%s requested_precision=%s load_strategy=%s "
            "eos_interval=%s sidecar=%s static_cache=%s t3_compile_mode=%s t3_dtype=%s s3gen_dtype=%s",
            self._render_precision,
            self._requested_precision,
            self._load_strategy,
            os.environ.get("RRV_T3_EOS_CHECK_INTERVAL", "16"),
            self._tail_sidecar_mode,
            static_cache,
            os.environ.get("RRV_T3_COMPILE_MODE", "default"),
            t3_dtype,
            s3_dtype,
        )

    def _load_chatterbox_cpu_precision_gpu(self, local_model_dir):
        """Load Chatterbox with lower peak GPU pressure.

        This mirrors chatterbox.tts.ChatterboxTTS.from_local(), but avoids
        moving fp32 modules to CUDA before the requested precision is applied.
        Each module is loaded on CPU, converted to its final dtype, moved to GPU,
        then the CPU state dict is released before the next module loads.
        """
        from pathlib import Path as _Path
        import torch
        from safetensors.torch import load_file
        from chatterbox.tts import ChatterboxTTS, Conditionals, T3, S3Gen, VoiceEncoder, EnTokenizer

        ckpt_dir = _Path(local_model_dir)
        device = self._torch_device
        t3_dtype = self._t3_float_dtype()
        s3_dtype = self._s3gen_float_dtype()

        log.info(
            "Chatterbox load strategy: cpu_precision_gpu starting — device=%s t3_dtype=%s s3gen_dtype=%s",
            device, t3_dtype, s3_dtype,
        )
        self._log_vram_checkpoint("load-start cpu_precision_gpu", reset_peak=True)

        ve = VoiceEncoder()
        ve_state = load_file(ckpt_dir / "ve.safetensors", device="cpu")
        ve.load_state_dict(ve_state)
        del ve_state
        ve.to(device=device).eval()
        self._free_load_intermediate("ve")
        self._log_vram_checkpoint("after VoiceEncoder load")

        t3 = T3()
        t3_state = load_file(ckpt_dir / "t3_cfg.safetensors", device="cpu")
        if "model" in t3_state.keys():
            t3_state = t3_state["model"][0]
        t3.load_state_dict(t3_state)
        del t3_state
        t3.to(dtype=t3_dtype)
        t3.to(device=device).eval()
        self._free_load_intermediate("t3")
        self._log_vram_checkpoint("after T3 load")

        s3gen = S3Gen()
        s3_state = load_file(ckpt_dir / "s3gen.safetensors", device="cpu")
        s3gen.load_state_dict(s3_state, strict=False)
        del s3_state
        s3gen.to(dtype=s3_dtype)
        s3gen.to(device=device).eval()
        self._free_load_intermediate("s3gen")
        self._log_vram_checkpoint("after S3Gen load")

        tokenizer = EnTokenizer(str(ckpt_dir / "tokenizer.json"))

        conds = None
        builtin_voice = ckpt_dir / "conds.pt"
        if builtin_voice.exists():
            conds = Conditionals.load(builtin_voice, map_location="cpu")
            t3_cond, gen_dict = self._cond_to_device(conds.t3, conds.gen)
            conds = Conditionals(t3_cond, gen_dict)
            self._free_load_intermediate("conds")
            self._log_vram_checkpoint("after built-in conditionals load")

        model = ChatterboxTTS(t3, s3gen, ve, tokenizer, device, conds=conds)
        self._log_vram_checkpoint("load-complete cpu_precision_gpu")
        log.info("Chatterbox load strategy: cpu_precision_gpu complete")
        return model

    def _tensor_to_device_precision(self, value):
        import torch
        if not torch.is_tensor(value):
            return value
        if value.is_floating_point():
            return value.to(device=self._torch_device, dtype=self._target_float_dtype())
        return value.to(device=self._torch_device)

    def _apply_model_precision(self) -> None:
        import torch
        if self._model is None:
            return
        if self._render_precision == "fp32":
            log.info("Chatterbox precision: fp32 (default)")
            return
        if self._render_precision == "fp8":
            # PyTorch exposes float8 dtypes on some builds, but most transformer,
            # sampling, and HiFT-GAN ops used here cannot run from naive .to(fp8).
            # Failing fast is safer than silently producing fp32 audio under an fp8
            # cache key or corrupting tail-token/conditioning reuse.
            raise RuntimeError(
                "RRV_CB_PRECISION=fp8 requested, but fp8 Chatterbox inference is not "
                "implemented safely. Use fp16 for this experiment. Future fp8 work "
                "needs explicit quantization/autocast support, not blanket module casts."
            )
        if self._render_precision == "t3_fp16":
            # Quality-safe mixed precision: T3 is the hot autoregressive loop and
            # benefits from fp16. S3Gen/HiFT-GAN stays fp32 because full fp16 audio
            # tested recognizable but choppy/cut-off, with multiple dtype islands
            # in the vocoder/source/STFT path.
            if hasattr(self._model, "t3") and self._model.t3 is not None:
                self._model.t3.to(dtype=torch.float16)
            if hasattr(self._model, "s3gen") and self._model.s3gen is not None:
                self._model.s3gen.to(dtype=torch.float32)
            log.info("Chatterbox precision: t3_fp16 enabled — T3 fp16, S3Gen/HiFT-GAN fp32 for audio quality")
            return

        if self._render_precision == "fp16_full":
            # Full fp16 experiment. This is retained for future investigation, but
            # is not the recommended fp16 mode because it produced bad audio in v137.
            self._patch_s3gen_speaker_encoder_inference()
            if hasattr(self._model, "t3") and self._model.t3 is not None:
                self._model.t3.to(dtype=torch.float16)
            if hasattr(self._model, "s3gen") and self._model.s3gen is not None:
                self._model.s3gen.to(dtype=torch.float16)
            log.info("Chatterbox precision: fp16_full enabled for T3 + S3Gen including speaker_encoder (experimental)")
            return

    def _patch_s3gen_speaker_encoder_inference(self) -> None:
        """Patch CAMPPlus/xvector inference to be dtype-correct for fp16 runs.

        Stock Chatterbox forces xvector features to torch.float32 right before
        forward(). That is valid only when speaker_encoder weights stay fp32. In
        fp16 mode it creates FloatTensor input vs HalfTensor weight failures in
        Conv2d. We keep feature extraction itself fp32 for stability, then cast
        the extracted feature tensor to the module's actual parameter dtype and
        device before forward().
        """
        if self._model is None or not hasattr(self._model, "s3gen"):
            return
        speaker_encoder = getattr(self._model.s3gen, "speaker_encoder", None)
        if speaker_encoder is None or getattr(speaker_encoder, "_rrv_dtype_patched", False):
            return

        import types as _types
        import torch as _torch

        original_inference = getattr(speaker_encoder, "inference", None)
        if original_inference is None:
            return
        extract_feature = original_inference.__func__.__globals__.get("extract_feature")
        if extract_feature is None:
            log.warning("Chatterbox precision: unable to patch speaker_encoder.inference; extract_feature not found")
            return

        def _rrv_inference(self_encoder, audio_list):
            # Feature extraction should remain fp32; fp16 FFT/mel paths are both
            # slower and less widely supported. Handle tensor and simple list/tuple
            # inputs without assuming Chatterbox internals will stay unchanged.
            if _torch.is_tensor(audio_list):
                feature_audio = audio_list.to(dtype=_torch.float32)
            elif isinstance(audio_list, (list, tuple)):
                feature_audio = [x.to(dtype=_torch.float32) if _torch.is_tensor(x) else x for x in audio_list]
            else:
                feature_audio = audio_list

            speech, speech_lengths, speech_times = extract_feature(feature_audio)

            try:
                p = next(self_encoder.parameters())
                target_device = p.device
                target_dtype = p.dtype if p.is_floating_point() else _torch.float32
            except StopIteration:
                target_device = speech.device
                target_dtype = _torch.float32

            return self_encoder.forward(speech.to(device=target_device, dtype=target_dtype))

        speaker_encoder.inference = _types.MethodType(_rrv_inference, speaker_encoder)
        speaker_encoder._rrv_dtype_patched = True
        log.info("Chatterbox precision: patched S3Gen speaker_encoder.inference for dtype-correct fp16")

    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key   = request.lang_code or ""
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

    def _acquire_static_cache(self, device, dtype, context_len: int,
                               max_new_tokens: int, llama_config):
        """Return the persistent StaticCache, reset for a new inference call.

        On first call (or if max_cache_len needs to grow) the cache is allocated
        and stored on self._static_cache.  Subsequent calls zero the KV buffers
        via reset() so no new GPU memory is allocated.

        The cache is sized to max(context_len + max_new_tokens, RRV_T3_STATIC_CACHE_LEN).
        If a request arrives that exceeds the current allocation (e.g. very long
        text + large max_new_tokens) the cache is reallocated at the new size.

        Thread safety: _synthesize_sync runs in a thread-pool executor but the
        voice-slot serialisation guarantees only one synthesis is active at a time
        inside a given worker process, so self._static_cache is never accessed
        concurrently.
        """
        try:
            from transformers.cache_utils import StaticCache
        except ImportError:
            return None

        needed_len = max(context_len + max_new_tokens,
                         int(os.environ.get("RRV_T3_STATIC_CACHE_LEN", "1400")))

        if self._static_cache is None or self._static_cache_len < needed_len:
            # First allocation or cache too small — (re)allocate.
            # This should happen only once per worker lifetime under normal usage.
            if self._static_cache is not None:
                log.info(
                    "Chatterbox T3: StaticCache resize %d → %d",
                    self._static_cache_len, needed_len,
                )
            self._static_cache = StaticCache(
                config=llama_config,
                max_batch_size=2,          # CFG always batch=2
                max_cache_len=needed_len,
                device=device,
                dtype=dtype,
            )
            self._static_cache_len = needed_len
            log.info(
                "Chatterbox T3: StaticCache allocated batch=2 len=%d dtype=%s",
                needed_len, dtype,
            )
        else:
            # Reset in-place — zeros KV buffers, no reallocation.
            try:
                self._static_cache.reset()
            except AttributeError:
                # transformers < 4.44 lacks reset(); reallocate as fallback.
                log.debug("Chatterbox T3: StaticCache.reset() unavailable — reallocating")
                self._static_cache = StaticCache(
                    config=llama_config,
                    max_batch_size=2,
                    max_cache_len=self._static_cache_len,
                    device=device,
                    dtype=dtype,
                )

        return self._static_cache

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "chatterbox_full"

    @property
    def display_name(self) -> str:
        return "Chatterbox"

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
        return ["en"]

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
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        # Create asyncio primitives here — inside the running event loop
        if self._voice_cond is None:
            self._voice_cond = asyncio.Condition()
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        self._log_vram_checkpoint("after model load", reset_peak=True)
        log.info(
            "Chatterbox loaded: model_version=%s device=%s precision=%s",
            self._model_version, self._torch_device, self._render_precision,
        )
        # Warm up only what must be hydrated in this process.
        #
        # Important distinction:
        # - TORCHINDUCTOR_CACHE_DIR persists compiled kernels/subgraphs on disk.
        # - It does NOT persist live model/runtime state, CUDA allocator state, StaticCache,
        #   tokenizer lazy init, librosa/numba functions, or Chatterbox conditionals in RAM.
        #
        # Therefore a restarted worker still needs a small "touch" of the real path.
        # But once a disk warmup stamp exists, do not replay the expensive multi-shape
        # compile campaign every boot. Use one representative S3Gen probe plus one full
        # mini render to hydrate the process.
        await loop.run_in_executor(None, self._warmup_librosa)
        self._log_vram_checkpoint("after librosa warmup")
        import os
        if os.environ.get("RRV_T3_COMPILE", "1") == "1":
            await loop.run_in_executor(None, self._warmup_t3_compile)
            self._log_vram_checkpoint("after T3 compile warmup")
        if os.environ.get("RRV_S3GEN_COMPILE", "0") == "1":
            await loop.run_in_executor(None, self._warmup_s3gen_compile)
            self._log_vram_checkpoint("after S3Gen compile warmup")

        if os.environ.get("RRV_CB_FIRST_RENDER_WARMUP", "1") == "1":
            await loop.run_in_executor(None, self._warmup_first_render_path)
            self._log_vram_checkpoint("after first-render warmup")
        self._log_runtime_profile()

    def _warmup_librosa(self) -> None:
        """
        Trigger librosa/numba JIT compilation at startup.

        librosa defers numba's JIT compilation of its DSP routines (mel filterbank,
        resampling, STFT) to first use. On first call this causes a ~10-20s CPU
        spike as numba compiles to native code. Subsequent calls are instant.

        This warmup runs a dummy load+resample to pay that cost at startup rather
        than stalling the first user render request.
        """
        try:
            import librosa
            import numpy as np
            # Dummy audio: 1 second of silence at 22050 Hz
            dummy = np.zeros(22050, dtype=np.float32)
            # Resample triggers numba mel/resampling JIT
            _ = librosa.resample(dummy, orig_sr=22050, target_sr=16000)
            # Mel spectrogram triggers numba filterbank JIT
            _ = librosa.feature.melspectrogram(y=dummy, sr=22050, n_mels=80)
            log.info("Chatterbox: librosa/numba warmup complete")
        except Exception as e:
            log.warning("Chatterbox: librosa warmup failed (non-fatal): %s", e)

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
            float_dtype = self._t3_float_dtype()
            dummy_t3_cond = T3Cond(
                speaker_emb=torch.zeros(1, 1, 256, device=device, dtype=float_dtype),
                cond_prompt_speech_tokens=torch.zeros(
                    1, self._model.t3.hp.speech_cond_prompt_len,
                    dtype=torch.long, device=device),
                emotion_adv=torch.tensor([[[0.5]]], device=device, dtype=float_dtype),
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

    def _find_startup_warmup_sample(self) -> Path | None:
        """Return first usable Chatterbox reference sample for startup warmup."""
        samples_dir = os.environ.get("RRV_SAMPLES_DIR", "")
        root = Path(samples_dir) if samples_dir else (self._models_dir.parent / "samples")
        if not root.exists():
            return None

        candidates = sorted(root.rglob("*.wav"))
        for wav_path in candidates:
            name = wav_path.name.lower()
            # Prefer provider-tagged Chatterbox artifacts when present.
            if "-chatterbox" in name:
                return wav_path
        return candidates[0] if candidates else None

    def _warmup_first_render_path(self) -> None:
        """
        Exercise the same path a real render uses.

        This is intentionally separate from torch.compile warmup. The compile
        warmups touch T3 and S3Gen directly, but first user render also hits
        sample conditionals, tokenizer, prior-token setup, watermarking, OGG
        encode, and assorted lazy imports. Running one tiny real synthesis at
        startup moves that one-time latency out of the first gameplay request.
        """
        try:
            sample_path = self._find_startup_warmup_sample()
            if sample_path is None:
                log.info("Chatterbox first-render warmup skipped — no sample WAV found")
                return

            import time as _time_mod
            t0 = _time_mod.perf_counter()
            req = SynthesisRequest(
                text="Startup warmup complete.",
                lang_code="en",
                speech_rate=1.0,
                sample_path=sample_path,
                sample_id=sample_path.stem,
                samples_dir=sample_path.parent,
                cfg_weight=0.5,
                exaggeration=0.5,
                cb_temperature=0.8,
                cb_top_p=1.0,
                cb_repetition_penalty=1.2,
                voice_context="__startup_warmup__",
            )
            _ = self._synthesize_sync(req)
            elapsed = _time_mod.perf_counter() - t0
            log.info(
                "Chatterbox first-render warmup complete — sample=%s elapsed=%.3fs",
                sample_path.name,
                elapsed,
            )
        except Exception as e:
            log.warning("Chatterbox first-render warmup failed (non-fatal): %s", e)

    def _warmup_stamp_path(self) -> Path:
        """Process-warmup stamp used to avoid full compile campaigns every restart."""
        return self._cond_cache_dir / f"{self.provider_id}_compile_warmup.stamp"

    def _warmup_stamp_key(self) -> str:
        import sys
        try:
            import torch
            torch_ver = getattr(torch, "__version__", "unknown")
        except Exception:
            torch_ver = "unknown"
        return "|".join([
            f"provider={self.provider_id}",
            f"model={self._model_version or 'unknown'}",
            f"python={sys.version_info.major}.{sys.version_info.minor}",
            f"torch={torch_ver}",
            f"t3={os.environ.get('RRV_T3_COMPILE', '1')}",
            f"s3gen={os.environ.get('RRV_S3GEN_COMPILE', '0')}",
            f"max_tokens={os.environ.get('RRV_CB_MAX_NEW_TOKENS', '1000')}",
        ])

    def _has_valid_warmup_stamp(self) -> bool:
        try:
            p = self._warmup_stamp_path()
            return p.exists() and p.read_text(encoding="utf-8").strip() == self._warmup_stamp_key()
        except Exception:
            return False

    def _write_warmup_stamp(self) -> None:
        try:
            self._cond_cache_dir.mkdir(parents=True, exist_ok=True)
            self._warmup_stamp_path().write_text(self._warmup_stamp_key(), encoding="utf-8")
        except Exception as e:
            log.debug("Chatterbox warmup stamp write failed (non-fatal): %s", e)

    @staticmethod
    def _parse_warmup_token_counts(raw: str, fallback: tuple[int, ...]) -> tuple[int, ...]:
        vals: list[int] = []
        for part in (raw or "").replace(";", ",").split(","):
            part = part.strip()
            if not part:
                continue
            try:
                n = int(part)
                if n > 0:
                    vals.append(n)
            except ValueError:
                continue
        return tuple(vals) if vals else fallback

    def _warmup_s3gen_compile(self) -> None:
        """
        Hydrate S3Gen torch.compile using the SAME branch real synthesis uses.

        Previous warmup called:
            s3gen.inference(..., ref_wav=real_ref, ref_sr=real_sr)

        Real render calls:
            s3gen.inference(..., ref_dict=gen_dict)

        Those are not equivalent for torch.compile. The old warmup could build/load
        graphs for the ref_wav path while first user render still paid the ref_dict
        path compile/load cost. This warmup intentionally gets/caches real
        conditionals first, then calls the ref_dict path.
        """
        try:
            import time as _time_mod
            import torch
            if self._model is None or not hasattr(self._model, "s3gen"):
                return

            stamp_ok = self._has_valid_warmup_stamp()
            if stamp_ok:
                default_counts = (200,)
                mode = "fast disk-cache probe"
            else:
                default_counts = (100, 200, 350)
                mode = "cold compile campaign"

            raw_counts = os.environ.get("RRV_S3GEN_WARMUP_TOKENS", "")
            warmup_token_counts = self._parse_warmup_token_counts(raw_counts, default_counts)

            log.info(
                "Chatterbox S3Gen: warmup starting — %s; tokens=%s",
                mode,
                ",".join(str(x) for x in warmup_token_counts),
            )

            sample_path = self._find_startup_warmup_sample()
            if sample_path is None:
                log.info("Chatterbox S3Gen: warmup skipped — no sample WAV found")
                return

            # Use real cached conditionals and real ref_dict path. This also verifies
            # the condition cache can load from disk after restart.
            t0_cond = _time_mod.perf_counter()
            t3_cond, gen_dict = self._cond_get_or_compute_single(sample_path, 0.5)
            self._model.conds.t3 = t3_cond
            self._model.conds.gen = gen_dict
            log.info(
                "Chatterbox S3Gen: warmup conditionals ready — sample=%s elapsed=%.3fs",
                sample_path.name,
                _time_mod.perf_counter() - t0_cond,
            )

            s3gen = self._model.s3gen
            device = self._torch_device
            for n_tokens in warmup_token_counts:
                dummy_tokens = torch.randint(0, 6560, (1, n_tokens), dtype=torch.long, device=device)
                t0 = _time_mod.perf_counter()
                with torch.inference_mode():
                    s3gen.inference(
                        speech_tokens=dummy_tokens,
                        ref_dict=gen_dict,
                    )
                log.info(
                    "Chatterbox S3Gen: warmup pass complete — tokens=%d elapsed=%.3fs",
                    n_tokens,
                    _time_mod.perf_counter() - t0,
                )

            self._write_warmup_stamp()
            log.info("Chatterbox S3Gen: warmup complete")
        except Exception as e:
            log.error("Chatterbox S3Gen: compile warmup failed (%s: %s)", type(e).__name__, e, exc_info=True)

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
            log.info("Chatterbox: patched librosa.load -> float32")

        try:
            from chatterbox.tts import ChatterboxTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed. Run: pip install chatterbox-tts"
            )

        local_model_dir = self._models_dir / "chatterbox-hf"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox: loading from %s", local_model_dir)
            import torch as _torch
            _torch.set_float32_matmul_precision("high")

            if self._load_strategy == "cpu_precision_gpu":
                self._model = self._load_chatterbox_cpu_precision_gpu(local_model_dir)
                # _load_chatterbox_cpu_precision_gpu already applied target
                # module dtypes and moved each finalized module to GPU. Do not
                # call _apply_model_precision() again; it would be redundant.
                log.info("Chatterbox precision: %s applied during CPU-load strategy", self._render_precision)
            else:
                self._log_vram_checkpoint("load-start direct", reset_peak=True)
                self._model = ChatterboxTTS.from_local(
                    str(local_model_dir),
                    self._torch_device,
                )
                self._log_vram_checkpoint("after ChatterboxTTS.from_local direct")
                self._apply_model_precision()
                self._log_vram_checkpoint("after precision apply direct")

            self._patch_mel_filters()
            self._patch_t3_hidden_states()
            self._patch_t3_inference()
            self._patch_watermarker()
            self._patch_s3gen()
            import hashlib
            files = sorted(str(p) for p in local_model_dir.rglob("*.safetensors"))
            self._model_version = (
                hashlib.sha256("\n".join(files).encode()).hexdigest()[:8]
                if files else "local"
            )
        else:
            raise RuntimeError(
                f"Chatterbox model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-hf/tree/main\n"
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
        if self._model is not None and hasattr(self._model, "watermarker"):
            self._model.watermarker.apply_watermark = lambda wav, sample_rate=None: wav
            log.info("Chatterbox: Perth watermarker disabled (no-op patch applied)")

    def _patch_s3gen(self) -> None:
        """
        Vectorize SineGen.forward() in HiFT-GAN to eliminate a CPU-bound Python loop.

        The original HiFT-GAN SineGen builds a harmonic frequency matrix using a
        Python for-loop over harmonic_num+1 (typically 9) iterations, each doing a
        slice-assign on a [B, harmonics, sample_len] tensor:

            for i in range(self.harmonic_num + 1):
                F_mat[:, i: i + 1, :] = f0 * (i + 1) / self.sampling_rate

        This loop runs on a single CPU core regardless of device. For 24kHz output
        at 5 seconds that's 120,000 samples × 9 iterations of Python overhead,
        plus a CPU→GPU sync for each slice-assign. This is the primary cause of
        100% single-core CPU utilization during S3Gen inference.

        Also vectorized: the torch.distributions.Uniform phase_vec sampling, which
        has significant Python-side overhead. Replaced with torch.empty().uniform_()
        which dispatches directly to the CUDA RNG kernel.

        The patched forward is semantically identical — same math, same output shape,
        same dtype/device behaviour. Only the execution path changes.
        """
        log.info(
            "Chatterbox: _patch_s3gen called — RRV_S3GEN_COMPILE=%s model=%s",
            os.environ.get("RRV_S3GEN_COMPILE", "NOT_SET"),
            "present" if self._model is not None else "None",
        )
        if self._model is None:
            return
        if not hasattr(self._model, "s3gen"):
            return
        s3gen = self._model.s3gen
        if not hasattr(s3gen, "mel2wav"):
            return

        sine_gen = s3gen.mel2wav.m_source.l_sin_gen

        import torch as _torch
        import numpy as _np
        import types

        def _patched_sinegen_forward(self_sg, f0):
            # f0: [B, 1, sample_len]
            # Vectorized harmonic matrix — single GPU kernel instead of Python loop
            harmonics = _torch.arange(
                1, self_sg.harmonic_num + 2,
                device=f0.device, dtype=f0.dtype,
            ).view(1, -1, 1)  # [1, H, 1]
            F_mat = f0 * harmonics / self_sg.sampling_rate  # [B, H, sample_len]

            theta_mat = 2 * _np.pi * (_torch.cumsum(F_mat, dim=-1) % 1)

            # Vectorized phase sampling — torch RNG dispatches to CUDA directly
            phase_vec = _torch.empty(
                f0.size(0), self_sg.harmonic_num + 1, 1,
                device=f0.device, dtype=f0.dtype,
            ).uniform_(-_np.pi, _np.pi)
            phase_vec[:, 0, :] = 0.0

            sine_waves = self_sg.sine_amp * _torch.sin(theta_mat + phase_vec)

            uv = self_sg._f02uv(f0)
            noise_amp = uv * self_sg.noise_std + (1 - uv) * self_sg.sine_amp / 3
            noise = noise_amp * _torch.randn_like(sine_waves)
            sine_waves = sine_waves * uv + noise
            return sine_waves, uv, noise

        sine_gen.forward = types.MethodType(_patched_sinegen_forward, sine_gen)
        log.info(
            "Chatterbox: SineGen.forward patched — vectorized harmonic loop, "
            "harmonic_num=%d", sine_gen.harmonic_num,
        )

        source_mod = s3gen.mel2wav.m_source
        if not getattr(source_mod, "_rrv_dtype_patched", False):
            def _patched_source_forward(self_src, x):
                """dtype-safe SourceModuleHnNSF.forward for fp16 HiFT-GAN.

                Stock HiFT-GAN lets F0/source synthesis stay fp32, then feeds
                those fp32 sine waves into l_linear. That is fine when HiFT-GAN
                weights are fp32, but crashes in fp16 mode with FloatTensor input
                vs HalfTensor weights. Keep source generation numerically stable,
                then cast the generated sine/uv tensors to l_linear's actual dtype
                before trainable layers see them.
                """
                with _torch.no_grad():
                    sine_wavs, uv, _ = self_src.l_sin_gen(x.transpose(1, 2))
                    sine_wavs = sine_wavs.transpose(1, 2)
                    uv = uv.transpose(1, 2)

                try:
                    target_weight = self_src.l_linear.weight
                    target_device = target_weight.device
                    target_dtype = target_weight.dtype
                except Exception:
                    target_device = sine_wavs.device
                    target_dtype = sine_wavs.dtype

                sine_wavs = sine_wavs.to(device=target_device, dtype=target_dtype)
                uv = uv.to(device=target_device, dtype=target_dtype)

                sine_merge = self_src.l_tanh(self_src.l_linear(sine_wavs))
                noise = _torch.randn_like(uv) * self_src.sine_amp / 3
                return sine_merge, noise, uv

            source_mod.forward = types.MethodType(_patched_source_forward, source_mod)
            source_mod._rrv_dtype_patched = True
            log.info("Chatterbox: SourceModuleHnNSF.forward patched — dtype-safe source path")

        mel2wav = s3gen.mel2wav
        if not getattr(mel2wav, "_rrv_decode_dtype_patched", False):
            import torch.nn.functional as _F

            def _patched_hifigan_decode(self_hg, x: _torch.Tensor, s: _torch.Tensor = None) -> _torch.Tensor:
                """dtype-safe HiFT-GAN decode for fp16.

                Stock decode builds s_stft through torch.stft. That path returns
                fp32/complex64 even when source/module weights are fp16, then
                immediately feeds s_stft into source_downs fp16 Conv1d layers.
                Cast only at trainable layer boundaries; leave STFT/ISTFT math in
                fp32 where PyTorch has the best operator support.
                """
                if s is None:
                    s = _torch.zeros(1, 1, 0, device=x.device, dtype=x.dtype)

                s_stft_real, s_stft_imag = self_hg._stft(s.squeeze(1))
                s_stft = _torch.cat([s_stft_real, s_stft_imag], dim=1)

                try:
                    conv_pre_weight = self_hg.conv_pre.weight
                    x = x.to(device=conv_pre_weight.device, dtype=conv_pre_weight.dtype)
                except Exception:
                    pass

                x = self_hg.conv_pre(x)
                for i in range(self_hg.num_upsamples):
                    x = _F.leaky_relu(x, self_hg.lrelu_slope)
                    x = self_hg.ups[i](x)

                    if i == self_hg.num_upsamples - 1:
                        x = self_hg.reflection_pad(x)

                    source_down = self_hg.source_downs[i]
                    try:
                        sd_weight = source_down.weight
                        s_input = s_stft.to(device=sd_weight.device, dtype=sd_weight.dtype)
                    except Exception:
                        s_input = s_stft

                    si = source_down(s_input)
                    si = self_hg.source_resblocks[i](si)
                    if si.dtype != x.dtype or si.device != x.device:
                        si = si.to(device=x.device, dtype=x.dtype)
                    x = x + si

                    xs = None
                    for j in range(self_hg.num_kernels):
                        rb_out = self_hg.resblocks[i * self_hg.num_kernels + j](x)
                        if xs is None:
                            xs = rb_out
                        else:
                            xs = xs + rb_out
                    x = xs / self_hg.num_kernels

                x = _F.leaky_relu(x)
                x = self_hg.conv_post(x)
                magnitude = _torch.exp(x[:, :self_hg.istft_params["n_fft"] // 2 + 1, :])
                phase = _torch.sin(x[:, self_hg.istft_params["n_fft"] // 2 + 1:, :])

                # ISTFT is numerically safer and better supported in fp32.
                out = self_hg._istft(magnitude.float(), phase.float())
                out = _torch.clamp(out, -self_hg.audio_limit, self_hg.audio_limit)
                return out

            mel2wav.decode = types.MethodType(_patched_hifigan_decode, mel2wav)
            mel2wav._rrv_decode_dtype_patched = True
            log.info("Chatterbox: HiFT-GAN decode patched — dtype-safe STFT/source path")

        # ── Patch mask utility functions — eliminate GPU sync points ─────────
        # make_pad_mask calls lengths.max().item() which forces a CPU/GPU sync.
        # add_optional_chunk_mask calls (chunk_masks.sum(...)==0).sum().item()
        # — another sync. At inference both are called twice per CFM ODE step
        # (encoder + up_encoder), so 10 steps × 2 calls × 2 syncs = 40 forced
        # GPU syncs per S3Gen call, each stalling the CPU while waiting for the
        # GPU to flush. Patched versions avoid .item() entirely.
        try:
            import chatterbox.models.s3gen.utils.mask as _mask_mod

            def _make_pad_mask_nosync(lengths: _torch.Tensor, max_len: int = 0) -> _torch.Tensor:
                lengths = lengths.long()
                batch_size = lengths.size(0)
                # Avoid .item() — keep max_len on device
                if max_len <= 0:
                    max_len_t = lengths.max()
                else:
                    max_len_t = max_len
                seq_range = _torch.arange(0, max_len_t if isinstance(max_len_t, int) else max_len_t.item(),
                                          dtype=_torch.int64, device=lengths.device)
                seq_range_expand = seq_range.unsqueeze(0).expand(batch_size, seq_range.size(0))
                seq_length_expand = lengths.unsqueeze(-1)
                return seq_range_expand >= seq_length_expand

            def _add_optional_chunk_mask_nosync(
                xs, masks, use_dynamic_chunk, use_dynamic_left_chunk,
                decoding_chunk_size, static_chunk_size, num_decoding_left_chunks,
                enable_full_context=True,
            ):
                if use_dynamic_chunk:
                    # training path — keep original behaviour including .item()
                    return _mask_mod._orig_add_optional_chunk_mask(
                        xs, masks, use_dynamic_chunk, use_dynamic_left_chunk,
                        decoding_chunk_size, static_chunk_size,
                        num_decoding_left_chunks, enable_full_context,
                    )
                elif static_chunk_size > 0:
                    chunk_masks = _mask_mod.subsequent_chunk_mask(
                        xs.size(1), static_chunk_size,
                        num_decoding_left_chunks, xs.device,
                    )
                    chunk_masks = chunk_masks.unsqueeze(0)
                    chunk_masks = masks & chunk_masks
                else:
                    chunk_masks = masks
                # Skip the .item() validation check — it's a debug guard that
                # forces a GPU sync on every call. Invalid masks would surface
                # as NaN/garbage output during synthesis, not a silent failure.
                assert chunk_masks.dtype == _torch.bool
                return chunk_masks

            # Stash original for the training fallback
            _mask_mod._orig_add_optional_chunk_mask = _mask_mod.add_optional_chunk_mask
            _mask_mod.make_pad_mask = _make_pad_mask_nosync
            _mask_mod.add_optional_chunk_mask = _add_optional_chunk_mask_nosync

            # Also patch the references already imported into upsample_encoder
            import chatterbox.models.s3gen.transformer.upsample_encoder as _enc_mod
            _enc_mod.make_pad_mask = _make_pad_mask_nosync
            _enc_mod.add_optional_chunk_mask = _add_optional_chunk_mask_nosync

            # And flow.py which calls make_pad_mask
            import chatterbox.models.s3gen.flow as _flow_mod
            if hasattr(_flow_mod, 'make_pad_mask'):
                _flow_mod.make_pad_mask = _make_pad_mask_nosync

            # decoder.py imports add_optional_chunk_mask directly into its namespace
            # — must patch that reference too (called 3× per ConditionalDecoder forward)
            import chatterbox.models.s3gen.decoder as _decoder_mod
            _decoder_mod.add_optional_chunk_mask = _add_optional_chunk_mask_nosync

            log.info("Chatterbox: S3Gen mask utils patched — GPU sync points eliminated")
        except Exception as e:
            log.warning("Chatterbox: S3Gen mask patch failed (%s) — running unpatched", e)

        # ── torch.compile on CFM estimator (ConditionalDecoder) ──────────────
        # The CFM ODE loop runs 10 iterations of ConditionalDecoder.forward().
        # Each forward dispatches hundreds of small CUDA kernels through Python
        # (rearrange, CausalConv1d, 20 transformer blocks, etc.) — all serialized
        # on one CPU thread. torch.compile fuses these into a CUDA graph, cutting
        # Python dispatch overhead the same way it does for T3's LlamaModel.
        #
        # dynamic=True: mel length T varies per utterance — tell the compiler to
        # emit a single compiled graph that works for any T rather than recompiling
        # per-shape. First call still triggers JIT compilation (~15-30s).
        #
        # fullgraph=False: allows graph breaks at einops.rearrange and Python
        # control flow (if spks/cond is not None) without failing — subgraphs
        # between breaks are still compiled and fused.
        #
        # NOTE: mode="reduce-overhead" requires static shapes (uses CUDA graphs).
        # The CFM decoder receives variable mel length T per utterance — using
        # reduce-overhead + dynamic=True causes a recompile for every new shape,
        # adding 10-15s per synthesis. Use mode="default" instead, which handles
        # dynamic shapes correctly. Disabled by default; enable with RRV_S3GEN_COMPILE=1.
        use_compile = os.environ.get("RRV_S3GEN_COMPILE", "0") == "1"
        if use_compile and self._model is not None and hasattr(self._model, "s3gen"):
            try:
                # Ensure fx graph cache and dynamo cache are configured correctly
                # before compiling — these may not have been set at import time.
                import torch._inductor.config as _inductor_cfg
                import torch._dynamo.config as _dynamo_cfg
                _inductor_cfg.fx_graph_cache = True
                _inductor_cfg.force_disable_caches = False
                if _dynamo_cfg.cache_size_limit < 64:
                    _dynamo_cfg.cache_size_limit = 64

                estimator = self._model.s3gen.flow.decoder.estimator

                # CRITICAL: CausalConditionalCFM calls self.estimator.forward(...)
                # directly — not self.estimator(...). Wrapping the module with
                # torch.compile() only intercepts __call__, not .forward(). So we
                # must compile .forward() itself and replace it on the instance,
                # so the direct .forward() call hits the compiled path.
                compiled_forward = _torch.compile(
                    estimator.forward,
                    mode="default",
                    fullgraph=False,
                    dynamic=True,
                )
                estimator.forward = compiled_forward
                log.info(
                    "Chatterbox: S3Gen CFM estimator.forward compiled with torch.compile "
                    "(mode=default, dynamic=True, first call will warm up)"
                )
            except Exception as e:
                log.error(
                    "Chatterbox: S3Gen torch.compile failed (%s: %s) — running uncompiled",
                    type(e).__name__, e, exc_info=True,
                )


    def _patch_t3_inference(self) -> None:
        """
        Patch T3.inference() for maximum generation throughput.

        1. BUILD T3HuggingfaceBackend ONCE AT LOAD TIME
           Original code rebuilds it every inference() call. Built once here,
           reused forever. Alignment stream reset per call via _added_cond flag.

        2. StaticCache INSTEAD OF DynamicCache
           Pre-allocates [batch=2, num_heads, max_seq_len, head_dim] once.
           Eliminates ~15,000 tensor allocations for a 500-token generation.
           Sized by RRV_T3_STATIC_CACHE_LEN (default 1400). Falls back to
           DynamicCache if transformers < 4.36.

        3. REMOVE output_attentions=True
           Was materializing full attention matrices every step. Pure waste.

        4. torch.compile ON THE RAW LlamaModel (tfmr)
           Previous attempts compiled through HF wrapper stack — dynamo cannot
           trace through transformers decorators reliably. The correct target is
           t3.tfmr (raw LlamaModel) which has no decorator layers. We compile it
           directly at load time, then call it directly in the hot loop bypassing
           T3HuggingfaceBackend entirely for the per-token forward pass.
           The speech_head projection is a simple nn.Linear — fast enough raw.
           Disable with RRV_T3_COMPILE=0.
        """
        import os
        import types
        import torch

        if self._model is None:
            return

        # ── Check StaticCache availability / enable switch ─────────────────────
        # RRV_T3_STATIC_CACHE is a real kill-switch for experiments. Default ON.
        # StaticCache is the proven fast/stable path for default compile mode, but
        # torch.compile(mode="reduce-overhead") tries CUDA graphs and can conflict
        # with StaticCache's in-place KV-cache mutation. Set RRV_T3_STATIC_CACHE=0
        # only for throwaway tests such as reduce-overhead + DynamicCache.
        static_cache_env = os.environ.get("RRV_T3_STATIC_CACHE", "1").strip().lower()
        static_cache_enabled = static_cache_env not in {"0", "false", "off", "no"}
        if static_cache_enabled:
            try:
                from transformers.cache_utils import StaticCache
                has_static_cache = True
            except ImportError:
                has_static_cache = False
                log.warning(
                    "Chatterbox: StaticCache not available (need transformers>=4.36) "
                    "— falling back to DynamicCache."
                )
        else:
            has_static_cache = False
            log.warning(
                "Chatterbox: StaticCache disabled by RRV_T3_STATIC_CACHE=%s — "
                "using DynamicCache/standard cache path. This is experimental and may be slower.",
                static_cache_env,
            )

        use_compile       = os.environ.get("RRV_T3_COMPILE", "1") == "1"
        # T3 autoregressive decode is now partly CPU/launch-bound after t3_fp16.
        # `default` is the proven baseline. `reduce-overhead` asks torch.compile
        # to spend more memory/startup work reducing Python/kernel-launch overhead
        # (often via CUDA graphs when the graph is capturable). Keep this as a
        # knob because it can be fragile with dynamic control flow and may increase
        # VRAM. Expected values: default, reduce-overhead, max-autotune.
        t3_compile_mode   = os.environ.get("RRV_T3_COMPILE_MODE", "default").strip() or "default"
        static_cache_len  = int(os.environ.get("RRV_T3_STATIC_CACHE_LEN", "1400"))
        cfg_batch         = 2   # CFG always runs batch=2

        t3 = self._model.t3

        # ── Build T3HuggingfaceBackend once (for alignment analyzer + fallback) ─
        from chatterbox.models.t3.inference.t3_hf_backend import T3HuggingfaceBackend

        if t3.hp.is_multilingual:
            persistent_backend = None
            log.info("Chatterbox: multilingual — T3HuggingfaceBackend rebuilt per call")
        else:
            persistent_backend = T3HuggingfaceBackend(
                config=t3.cfg,
                llama=t3.tfmr,
                speech_enc=t3.speech_emb,
                speech_head=t3.speech_head,
                alignment_stream_analyzer=None,
            )
            log.info("Chatterbox: T3HuggingfaceBackend built once at load time")

        # ── Compile raw LlamaModel (tfmr) directly ────────────────────────────
        # This is the correct compile target — no HF decorator stack, no
        # output_capturing wrappers, no NameError from dynamo scope issues.
        # We call tfmr directly in the hot loop and apply speech_head ourselves.
        _tfmr       = t3.tfmr
        _speech_head = t3.speech_head

        if use_compile:
            try:
                import torch._dynamo as _dynamo
                _dynamo.reset()

                # transformers output_capturing.py has a dynamo resume stub that
                # references torch without importing it — a known bug in some
                # transformers versions. Tell dynamo to treat the entire
                # output_capturing module as a skip boundary so it never tries
                # to trace or resume inside it.
                try:
                    import transformers.utils.output_capturing as _oc
                    _dynamo.mark_dynamic  # probe — available in torch>=2.1
                    torch._dynamo.config.skip_nt_for_backend_registration = True
                except Exception:
                    pass

                try:
                    # Skip output_capturing entirely — dynamo will not trace into it
                    torch._dynamo.config.skipfiles_inline_module_allowlist =                         getattr(torch._dynamo.config,
                                'skipfiles_inline_module_allowlist', set())
                    import transformers.utils.output_capturing as _oc_mod
                    _dynamo.allow_in_graph(_oc_mod)
                except Exception:
                    pass

                # Disable output_capturing hooks — they use torch from module
                # scope which dynamo cannot resolve. The hooks only collect
                # intermediate tensor stats for debugging; safe to disable.
                try:
                    import transformers.utils.output_capturing as _oc_mod
                    if hasattr(_oc_mod, 'maybe_install_capturing_hooks'):
                        _oc_mod.maybe_install_capturing_hooks = lambda *a, **kw: None
                        log.info("Chatterbox: disabled transformers output_capturing hooks")
                except Exception:
                    pass

                _orig_forward = t3.tfmr.forward
                _compiled_forward = torch.compile(
                    _orig_forward,
                    mode=t3_compile_mode,
                    fullgraph=False,
                    dynamic=True,
                )
                t3.tfmr.forward = _compiled_forward
                _tfmr = t3.tfmr
                log.info(
                    "Chatterbox: LlamaModel.forward compiled with torch.compile "
                    "mode=%s dynamic=True",
                    t3_compile_mode,
                )
            except Exception as e:
                log.warning("Chatterbox: torch.compile failed (%s) — running uncompiled", e)
                _tfmr = t3.tfmr

        # ── Capture everything needed as closure locals ───────────────────────
        _has_static_cache   = has_static_cache
        _persistent_backend = persistent_backend
        _backend_ref        = self   # for _acquire_static_cache
        _log                = log

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
            import os as _os
            import time as _time_mod
            import torch as _torch
            from transformers.generation.logits_process import (
                RepetitionPenaltyLogitsProcessor,
                TopPLogitsWarper,
                MinPLogitsWarper,
            )

            _timing = _os.environ.get("RRV_CB_TIMING", "0") == "1"
            _timing_detail = _os.environ.get("RRV_CB_TIMING_DETAIL", "0") == "1"
            _t3_total_t0 = _time_mod.perf_counter() if _timing else 0.0

            assert prepend_prompt_speech_tokens is None, "not implemented"

            text_tokens = _torch.atleast_2d(text_tokens).to(
                dtype=_torch.long, device=self_t3.device)

            if initial_speech_tokens is None:
                initial_speech_tokens = (
                    self_t3.hp.start_speech_token
                    * _torch.ones_like(text_tokens[:, :1])
                )

            _prepare_t0 = _time_mod.perf_counter() if _timing else 0.0
            embeds, len_cond = self_t3.prepare_input_embeds(
                t3_cond=t3_cond,
                text_tokens=text_tokens,
                speech_tokens=initial_speech_tokens,
                cfg_weight=cfg_weight,
            )
            _prepare_elapsed = (_time_mod.perf_counter() - _prepare_t0) if _timing else 0.0

            # ── Select / build backend (used for alignment analyzer only) ─────
            # patched_model is only needed for multilingual alignment stream.
            # For English, we call _tfmr directly in the hot loop.
            if _persistent_backend is not None:
                patched_model = _persistent_backend
                patched_model._added_cond = False
            else:
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

            bos_token = _torch.tensor(
                [[self_t3.hp.start_speech_token]], dtype=_torch.long, device=device)
            bos_embed = self_t3.speech_emb(bos_token)
            bos_embed = bos_embed + self_t3.speech_pos_emb.get_fixed_embedding(0)
            bos_embed = _torch.cat([bos_embed, bos_embed])  # CFG batch=2

            inputs_embeds = _torch.cat([embeds, bos_embed], dim=1)

            # Preallocate generated token buffer.
            # The previous loop did torch.cat(generated_ids, next_token) every step.
            # That reallocates/copies a growing tensor hundreds of times and also
            # feeds the repetition-penalty processor with a freshly allocated tensor.
            # Keep BOS at index 0, write generated tokens at 1..N, and slice views.
            generated_ids = _torch.empty(
                (1, max_new_tokens + 1), dtype=_torch.long, device=device)
            generated_ids[:, 0:1] = bos_token
            generated_count = 0

            # Avoid LearnedPositionEmbeddings.get_fixed_embedding(i) in the hot loop.
            # That helper builds a new scalar tensor every token. Index the embedding
            # table once here and slice one position per generated token.
            pos_ids = _torch.arange(
                1, max_new_tokens + 1, dtype=_torch.long, device=device).view(1, -1)
            pos_embeds = self_t3.speech_pos_emb.emb(pos_ids)

            cfg_scale = _torch.as_tensor(
                cfg_weight, device=device, dtype=inputs_embeds.dtype)

            try:
                eos_check_interval = max(
                    1, int(_os.environ.get("RRV_T3_EOS_CHECK_INTERVAL", "8")))
            except Exception:
                eos_check_interval = 8

            top_p_warper = TopPLogitsWarper(top_p=top_p)
            min_p_warper = MinPLogitsWarper(min_p=min_p)
            repetition_penalty_processor = RepetitionPenaltyLogitsProcessor(
                penalty=float(repetition_penalty))

            # ── Acquire persistent StaticCache ────────────────────────────────
            # Cache is allocated once at load time and reset() between calls.
            # cache_position tracks the write offset into the pre-allocated KV
            # buffer; without it LlamaModel writes every token to position 0.
            past = None
            cache_position = None
            context_len = inputs_embeds.size(1)

            _static_elapsed = 0.0
            if _has_static_cache:
                _static_t0 = _time_mod.perf_counter() if _timing else 0.0
                try:
                    past = _backend_ref._acquire_static_cache(
                        device=device,
                        dtype=embeds.dtype,
                        context_len=context_len,
                        max_new_tokens=max_new_tokens,
                        llama_config=self_t3.cfg,
                    )
                    if past is not None:
                        cache_position = _torch.arange(context_len, device=device)
                except Exception as e:
                    _log.warning(
                        "Chatterbox T3: StaticCache acquire failed (%s) — "
                        "falling back to DynamicCache", e)
                    past = None
                    cache_position = None
                _static_elapsed = (_time_mod.perf_counter() - _static_t0) if _timing else 0.0

            # ── Initial forward pass — full context ──────────────────────────
            # Call _tfmr (compiled LlamaModel) directly — no HF wrapper overhead.
            # speech_head applied manually to get logits.
            _initial_t0 = _time_mod.perf_counter() if _timing else 0.0
            tfmr_out = _tfmr(
                inputs_embeds=inputs_embeds,
                past_key_values=past,
                use_cache=True,
                output_attentions=False,
                output_hidden_states=True,
                return_dict=True,
                **({"cache_position": cache_position} if cache_position is not None else {}),
            )
            if tfmr_out.hidden_states is not None:
                hidden = tfmr_out.hidden_states[-1]
            else:
                hidden = tfmr_out.last_hidden_state
            logits_full = _speech_head(hidden)
            past = tfmr_out.past_key_values
            _initial_elapsed = (_time_mod.perf_counter() - _initial_t0) if _timing else 0.0

            # Advance cache_position past the context
            if cache_position is not None:
                cache_position = _torch.tensor([context_len], device=device)

            # ── Generation loop ───────────────────────────────────────────────
            _loop_t0 = _time_mod.perf_counter() if _timing else 0.0
            _detail_cfg = 0.0
            _detail_align = 0.0
            _detail_repetition = 0.0
            _detail_warp = 0.0
            _detail_sample = 0.0
            _detail_store_eos = 0.0
            _detail_embed = 0.0
            _detail_forward = 0.0
            _step_count = 0
            _sample_iter = range(max_new_tokens)
            if _os.environ.get("RRV_T3_TQDM", "0") == "1":
                try:
                    from tqdm import tqdm as _tqdm
                    _sample_iter = _tqdm(
                        _sample_iter, desc="Sampling", dynamic_ncols=True, disable=False)
                except Exception:
                    pass

            for i in _sample_iter:
                _step_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                logits_step = logits_full[:, -1, :]
                cond   = logits_step[0:1, :]
                uncond = logits_step[1:2, :]
                logits = cond + cfg_scale * (cond - uncond)

                _align_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_cfg += _align_t0 - _step_t0

                if patched_model.alignment_stream_analyzer is not None:
                    if logits.dim() == 1:
                        logits = logits.unsqueeze(0)
                    last_token = int(generated_ids[0, generated_count].item())
                    logits = patched_model.alignment_stream_analyzer.step(
                        logits, next_token=last_token)

                _rep_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_align += _rep_t0 - _align_t0

                ids_for_proc = generated_ids[:, :generated_count + 1]
                logits = repetition_penalty_processor(ids_for_proc, logits)

                _warp_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_repetition += _warp_t0 - _rep_t0

                if temperature != 1.0:
                    logits = logits / temperature

                logits = min_p_warper(ids_for_proc, logits)
                logits = top_p_warper(ids_for_proc, logits)

                _sample_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_warp += _sample_t0 - _warp_t0

                probs = _torch.softmax(logits, dim=-1)
                next_token = _torch.multinomial(probs, num_samples=1)

                _store_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_sample += _store_t0 - _sample_t0

                generated_count += 1
                generated_ids[:, generated_count:generated_count + 1] = next_token

                _step_count = i + 1

                # Avoid a CPU/GPU sync on every generated token.  A direct
                # `next_token.item()` or single-element tensor comparison forces
                # the CPU to wait for the GPU every step and showed up as
                # ~5ms/token in steady-state timing.  Check a small generated
                # window periodically instead, then trim the output back to the
                # first EOS if it appeared inside the window.
                should_check_eos = (
                    eos_check_interval <= 1
                    or ((i + 1) % eos_check_interval) == 0
                    or (i + 1) >= max_new_tokens
                )
                if should_check_eos:
                    eos_start = max(1, generated_count - eos_check_interval + 1)
                    eos_window = generated_ids[0, eos_start:generated_count + 1].detach().cpu()
                    eos_hits = (eos_window == self_t3.hp.stop_speech_token).nonzero(as_tuple=False)
                    if eos_hits.numel() > 0:
                        eos_offset = int(eos_hits[0].item())
                        generated_count = eos_start + eos_offset
                        if _timing_detail:
                            _detail_store_eos += _time_mod.perf_counter() - _store_t0
                        _log.info(
                            f"✅ EOS token detected! Stopping generation at step {i+1} (eos_index={generated_count})")
                        break

                _embed_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_store_eos += _embed_t0 - _store_t0
                next_token_embed = self_t3.speech_emb(next_token)
                next_token_embed = next_token_embed + pos_embeds[:, i:i + 1, :]
                # CFG batch=2
                next_token_embed = _torch.cat([next_token_embed, next_token_embed])

                _forward_t0 = _time_mod.perf_counter() if _timing_detail else 0.0
                if _timing_detail:
                    _detail_embed += _forward_t0 - _embed_t0

                # ── Single-token forward — hot path, compiled tfmr directly ──
                tfmr_out = _tfmr(
                    inputs_embeds=next_token_embed,
                    past_key_values=past,
                    use_cache=True,
                    output_attentions=False,
                    output_hidden_states=True,
                    return_dict=True,
                    **({"cache_position": cache_position} if cache_position is not None else {}),
                )
                if tfmr_out.hidden_states is not None:
                    hidden = tfmr_out.hidden_states[-1]
                else:
                    hidden = tfmr_out.last_hidden_state
                logits_full = _speech_head(hidden)
                past = tfmr_out.past_key_values
                if cache_position is not None:
                    cache_position = cache_position + 1
                if _timing_detail:
                    _detail_forward += _time_mod.perf_counter() - _forward_t0

            _loop_elapsed = (_time_mod.perf_counter() - _loop_t0) if _timing else 0.0
            predicted_tokens = generated_ids[:, 1:generated_count + 1].contiguous()
            if _timing:
                _total_elapsed = _time_mod.perf_counter() - _t3_total_t0
                _log.info(
                    "Chatterbox T3 timing: total=%.3fs prepare=%.3fs static_cache=%.3fs initial_forward=%.3fs loop=%.3fs steps=%d ctx=%d generated=%d eos_interval=%d",
                    _total_elapsed,
                    _prepare_elapsed,
                    _static_elapsed,
                    _initial_elapsed,
                    _loop_elapsed,
                    _step_count,
                    context_len,
                    predicted_tokens.size(1),
                    eos_check_interval,
                )
                if _timing_detail and _step_count > 0:
                    _log.info(
                        "Chatterbox T3 detail: cfg=%.3fs align=%.3fs rep=%.3fs warp=%.3fs sample=%.3fs store_eos=%.3fs embed=%.3fs tfmr_forward=%.3fs avg_step=%.2fms",
                        _detail_cfg,
                        _detail_align,
                        _detail_repetition,
                        _detail_warp,
                        _detail_sample,
                        _detail_store_eos,
                        _detail_embed,
                        _detail_forward,
                        (_loop_elapsed / _step_count) * 1000.0,
                    )
            return predicted_tokens

        # Bind the patched inference as a method on the T3 instance
        t3.inference = types.MethodType(_patched_inference, t3)
        log.info(
            "Chatterbox: T3 inference patched — "
            "static_cache=%s static_cache_env=%s compile=%s compile_mode=%s cache_len=%d",
            has_static_cache, static_cache_env, use_compile, t3_compile_mode, static_cache_len,
        )

    def _patch_mel_filters(self) -> None:
        """Force float32 through Chatterbox's pipeline. See chatterbox_backend.py for full explanation."""
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
                log.debug("Chatterbox: patched S3Tokenizer.log_mel_spectrogram")
        except Exception as e:
            log.warning("Chatterbox: could not patch S3Tokenizer: %s", e)

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
                log.debug("Chatterbox: patched VoiceEncoder.embeds_from_wavs -> float32")
        except Exception as e:
            log.warning("Chatterbox: could not patch VoiceEncoder: %s", e)

    def _patch_t3_hidden_states(self) -> None:
        """
        Patch T3HuggingfaceBackend.forward for transformers 4.57.x compatibility.

        transformers 4.57.x changed LlamaModel to return hidden_states=None in the
        output tuple even when output_hidden_states=True is passed — the final hidden
        state is now only accessible via last_hidden_state. Chatterbox's T3 backend
        indexes tfmr_out.hidden_states[-1] which raises TypeError: 'NoneType' is not
        subscriptable on 4.57.x.

        This patch wraps forward() to fall back to last_hidden_state when hidden_states
        is None, restoring compatibility without modifying the installed package.
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
                # All imports are local — torch.compile/dynamo traces this
                # function in isolation and cannot resolve closed-over module
                # references from outer scopes reliably.
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
                # transformers 4.57.x: hidden_states may be None even when requested.
                # Fall back to last_hidden_state which is always populated.
                if tfmr_out.hidden_states is not None:
                    hidden_states = tfmr_out.hidden_states[-1]
                else:
                    hidden_states = tfmr_out.last_hidden_state
                    _log.debug("Chatterbox T3: hidden_states was None, using last_hidden_state "
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
            log.info("Chatterbox: patched T3HuggingfaceBackend.forward for transformers 4.57.x compatibility")

        except Exception as e:
            log.warning("Chatterbox: could not patch T3HuggingfaceBackend.forward: %s", e)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox backend is not loaded")

        # Blend requests supply sample paths via request.blend entries — sample_path
        # is only required for single-reference synthesis.
        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        if not blend_entries and request.sample_path is None:
            raise ValueError(
                "Chatterbox requires a reference audio clip. "
                "Provide sample_id in the request, or use voice.type='blend'."
            )

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        _timing = os.environ.get("RRV_CB_TIMING", "0") == "1"
        if _timing:
            import time as _time_mod
            _total_t0 = _time_mod.perf_counter()
            _slot_t0 = _total_t0
        await self._acquire_voice_slot(voice_key)
        if _timing:
            _slot_elapsed = _time_mod.perf_counter() - _slot_t0
            _synth_t0 = _time_mod.perf_counter()
        try:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        finally:
            await self._release_voice_slot(voice_key)
        if _timing:
            _synth_elapsed = _time_mod.perf_counter() - _synth_t0
            _dur_t0 = _time_mod.perf_counter()

        duration = estimate_duration(ogg_bytes)
        if _timing:
            _dur_elapsed = _time_mod.perf_counter() - _dur_t0
            log.info(
                "Chatterbox timing: async_total=%.3fs slot_wait=%.3fs synth_executor=%.3fs duration_probe=%.3fs ogg_bytes=%d",
                _time_mod.perf_counter() - _total_t0,
                _slot_elapsed,
                _synth_elapsed,
                _dur_elapsed,
                len(ogg_bytes),
            )
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _setup_blend_generate(self, t3_cond, gen_dict: dict) -> None:
        """
        Install the generate() bypass for blend/cache-loaded conditionals.

        generate() internally checks emotion_adv and may recreate T3Cond,
        discarding our carefully prepared conditionals. This method installs
        a per-instance patched generate that skips that check entirely and
        goes straight to t3.inference() + s3gen.inference() with our conds.
        """
        import types as _types
        _t3_ref  = t3_cond
        _gen_ref = gen_dict

        def _patched_generate(self_m, text, repetition_penalty=1.2, min_p=0.05,
                               top_p=1.0, audio_prompt_path=None, exaggeration=0.5,
                               cfg_weight=0.5, temperature=0.8):
            import torch as _torch
            import torch.nn.functional as F
            from chatterbox.models.s3tokenizer import drop_invalid_tokens
            self_m.conds.t3  = _t3_ref
            self_m.conds.gen = _gen_ref
            text_proc = self_m.tokenizer.text_to_tokens(text).to(self_m.device)
            # Always double for CFG batch — chatterbox expects [2, seq] unconditionally
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
                watermarked = self_m.watermarker.apply_watermark(wav, sample_rate=self_m.sr)
            return _torch.from_numpy(watermarked).unsqueeze(0)

        self._model._rrv_blend_generate = _types.MethodType(_patched_generate, self._model)
        self._is_blend_active = True

    def _blend_conditionals_inner(self, blend: list[dict], exaggeration: float) -> None:
        """
        Blend voice conditionals from multiple reference samples.

        Two independent speaker embeddings must both be blended:

          conds.t3.speaker_emb   — 256-dim VE embedding. Projected by T3CondEnc.spkr_enc
                                   (nn.Linear) into T3's hidden dim. Conditions token generation.

          conds.gen["embedding"] — x-vector from S3Gen's CAMPPlus speaker_encoder.
                                   Conditions S3Gen vocoder — the actual waveform timbre.
                                   THIS is why blending only speaker_emb had no audible effect:
                                   the vocoder was still rendering in 100% primary's voice.

        Everything else (prompt_feat, prompt_token, cond_prompt_speech_tokens) comes
        from the primary sample untouched — blending discrete/spectrogram data causes mush.

        generate() bypass is required: its emotion_adv branch (float != tensor scalar)
        always evaluates truthy and recreates T3Cond from scratch, discarding our blend.
        """
        import torch, tempfile, os, types
        import soundfile as sf

        sample_entries = [e for e in blend if e.get("sample_path")]
        if not sample_entries:
            raise ValueError("_blend_conditionals: no sample_path entries in blend")

        total_w = sum(e["weight"] for e in sample_entries)
        entries = [(e["sample_path"], e["weight"] / total_w) for e in sample_entries]
        primary_path = max(entries, key=lambda x: x[1])[0]
        # Primary runs last — its full conds are in model.conds when loop ends
        entries_sorted = sorted(entries, key=lambda x: x[0] == primary_path)

        t3_speaker_embs = []   # (tensor, weight) — T3 VE embeddings
        gen_embeddings  = []   # (tensor, weight) — S3Gen x-vectors
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

            # ── Blend T3 speaker_emb ────────────────────────────────────────────
            blended_t3_spk = sum(emb * w for emb, w in t3_speaker_embs)
            mean_mag = sum(emb.norm() * w for emb, w in t3_speaker_embs)
            b_norm = blended_t3_spk.norm()
            if b_norm > 1e-8:
                blended_t3_spk = blended_t3_spk / b_norm * mean_mag

            # ── Blend S3Gen x-vector ─────────────────────────────────────────────
            blended_gen_emb = None
            if gen_embeddings:
                blended_gen_emb = sum(emb * w for emb, w in gen_embeddings)
                mean_mag_gen = sum(emb.norm() * w for emb, w in gen_embeddings)
                g_norm = blended_gen_emb.norm()
                if g_norm > 1e-8:
                    blended_gen_emb = blended_gen_emb / g_norm * mean_mag_gen

            # ── Build blended T3Cond — primary tokens, blended speaker_emb ───────
            from chatterbox.models.t3.modules.cond_enc import T3Cond
            _primary_t3 = self._model.conds.t3
            blended_t3 = T3Cond(
                speaker_emb=blended_t3_spk,
                cond_prompt_speech_tokens=_primary_t3.cond_prompt_speech_tokens,
                emotion_adv=torch.tensor([[[exaggeration]]],
                                         dtype=blended_t3_spk.dtype,
                                         device=blended_t3_spk.device),
            ).to(device=self._torch_device)

            # ── Patch gen dict — replace embedding only, keep all other fields ───
            blended_gen = dict(self._model.conds.gen)  # shallow copy — primary's fields
            if blended_gen_emb is not None:
                blended_gen["embedding"] = blended_gen_emb

            self._model.conds.t3  = blended_t3
            self._model.conds.gen = blended_gen
            self._setup_blend_generate(blended_t3, blended_gen)

            log.debug(
                "Chatterbox blend: t3_speaker_emb + gen[embedding] blended (%d samples)",
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
        return f"{h}|ex:{exaggeration:.3f}|prec:{self._render_precision}"

    def _cond_key_blend(self, blend_entries: list[dict], exaggeration: float) -> str:
        import hashlib
        from ..utils import compute_file_hash
        parts = sorted(
            f"{compute_file_hash(Path(e['sample_path']))[:16]}:{e['weight']:.4f}"
            for e in blend_entries if e.get("sample_path")
        )
        h = hashlib.sha256("|".join(parts).encode()).hexdigest()[:16]
        return f"blend_{h}|ex:{exaggeration:.3f}|prec:{self._render_precision}"

    def _prior_voice_key(self, base_voice_key: str, request: SynthesisRequest, *,
                         cfg_weight: float, temperature: float, top_p: float,
                         repetition_penalty: float, max_new_tokens: int) -> str:
        """Return identity key for T3 prior-token continuation.

        Conditioning cache keys only include values that affect prepare_conditionals()
        (sample/blend + exaggeration). Prior speech tokens are different: they are
        generated output context and must be keyed by every generation knob that can
        alter prosody/accent/token trajectory. Otherwise a cached tail from the same
        sample but a different seed/temp/cfg/top-p/etc. can bleed into the next
        segment.
        """
        import hashlib
        ctx = request.voice_context or ""
        ctx_hash = hashlib.sha256(ctx.encode("utf-8", "ignore")).hexdigest()[:12] if ctx else "none"
        seed = "none" if request.synthesis_seed is None else str(int(request.synthesis_seed))
        model_version = self._model_version or "unknown"
        return (
            f"{base_voice_key}"
            f"|provider:{self.provider_id}"
            f"|model:{model_version}"
            f"|prec:{self._render_precision}"
            f"|cfg:{cfg_weight:.4f}"
            f"|temp:{temperature:.4f}"
            f"|top_p:{top_p:.4f}"
            f"|rep:{repetition_penalty:.4f}"
            f"|seed:{seed}"
            f"|budget:{int(max_new_tokens)}"
            f"|ctx:{ctx_hash}"
        )

    def _cond_disk_path(self, cache_key: str) -> Path:
        safe = cache_key.replace("|", "_").replace(":", "-").replace(".", "p")
        return self._cond_cache_dir / f"{safe}.pt"

    def _cond_t3_to_cpu(self, t3_cond):
        import torch
        from chatterbox.models.t3.modules.cond_enc import T3Cond
        data = {
            k: v.detach().cpu() if torch.is_tensor(v) else v
            for k, v in t3_cond.__dict__.items()
        }
        return T3Cond(**data)

    def _cond_gen_to_cpu(self, gen_dict: dict) -> dict:
        import torch
        return {
            k: v.detach().cpu() if torch.is_tensor(v) else v
            for k, v in gen_dict.items()
        }

    def _cond_to_device(self, t3_cond, gen_dict: dict):
        import torch
        from chatterbox.models.t3.modules.cond_enc import T3Cond
        t3_dtype = self._t3_float_dtype()
        s3_dtype = self._s3gen_float_dtype()
        t3_data = {
            k: v.to(device=self._torch_device, dtype=t3_dtype) if torch.is_tensor(v) and v.is_floating_point()
            else (v.to(device=self._torch_device) if torch.is_tensor(v) else v)
            for k, v in t3_cond.__dict__.items()
        }
        gen_device = {
            k: v.to(device=self._torch_device, dtype=s3_dtype) if torch.is_tensor(v) and v.is_floating_point()
            else (v.to(device=self._torch_device) if torch.is_tensor(v) else v)
            for k, v in gen_dict.items()
        }
        return T3Cond(**t3_data).to(device=self._torch_device), gen_device

    def _cond_mem_get(self, cache_key: str):
        # Hot path: same voice as last synthesis — return CUDA tensors directly,
        # no CPU→GPU transfer needed.
        if cache_key == self._cond_hot_key and self._cond_hot_t3 is not None:
            log.debug("Cond cache: hot HIT key=%s", cache_key[:20])
            return self._cond_hot_t3, self._cond_hot_gen

        if cache_key in self._cond_mem_cache:
            self._cond_mem_cache.move_to_end(cache_key)
            t3_cond, gen_dict = self._cond_mem_cache[cache_key]
            # Memory cache stores CPU tensors so voice-sample cache does not pin VRAM.
            # Return a fresh device copy for this render; PyTorch allocator can reuse/free
            # it naturally once model.conds moves to another voice or worker exits.
            t3_device, gen_device = self._cond_to_device(t3_cond, gen_dict)
            # Promote to hot cache for next call
            self._cond_hot_key = cache_key
            self._cond_hot_t3  = t3_device
            self._cond_hot_gen = gen_device
            return t3_device, gen_device
        return None

    def _cond_mem_put(self, cache_key: str, t3_cond, gen_dict: dict) -> None:
        # Persistent memory cache is CPU-backed. Keeping prepared conditionals as CUDA
        # tensors makes every cached voice sample retain VRAM until eviction.
        self._cond_mem_cache[cache_key] = (
            self._cond_t3_to_cpu(t3_cond),
            self._cond_gen_to_cpu(gen_dict),
        )
        self._cond_mem_cache.move_to_end(cache_key)
        while len(self._cond_mem_cache) > self._COND_MEM_CACHE_SIZE:
            evicted_key, _ = self._cond_mem_cache.popitem(last=False)
            # If evicted key was the hot slot, clear it
            if evicted_key == self._cond_hot_key:
                self._cond_hot_key = ""
                self._cond_hot_t3  = None
                self._cond_hot_gen = None
        # Populate hot cache — the freshly computed conds are already on CUDA
        self._cond_hot_key = cache_key
        self._cond_hot_t3  = t3_cond
        self._cond_hot_gen = gen_dict

    def _cond_disk_save(self, cache_key: str, t3_cond, gen_dict: dict) -> None:
        import torch
        try:
            self._cond_cache_dir.mkdir(parents=True, exist_ok=True)
            data = {
                "precision": self._render_precision,
                "t3": {k: v.detach().cpu() if torch.is_tensor(v) else v
                       for k, v in t3_cond.__dict__.items()},
                "gen": {k: v.detach().cpu() if torch.is_tensor(v) else v
                        for k, v in gen_dict.items()},
            }
            tmp = self._cond_disk_path(cache_key).with_suffix(".pt.tmp")
            torch.save(data, tmp)
            tmp.rename(self._cond_disk_path(cache_key))
            log.info("Cond cache: saved to disk key=%s", cache_key[:20])
        except Exception as e:
            log.warning("Cond cache: disk save failed (%s)", e)

    def _cond_disk_load(self, cache_key: str):
        import torch
        from chatterbox.models.t3.modules.cond_enc import T3Cond
        p = self._cond_disk_path(cache_key)
        if not p.exists():
            return None
        try:
            data = torch.load(p, map_location="cpu", weights_only=True)
            file_precision = (data.get("precision") or "fp32").strip().lower() if isinstance(data, dict) else "fp32"
            if file_precision != self._render_precision:
                log.info("Cond cache: precision mismatch key=%s file=%s runtime=%s — ignoring",
                         cache_key[:20], file_precision, self._render_precision)
                return None
            t3_cond_cpu = T3Cond(**data["t3"])
            gen_dict_cpu = data["gen"]
            t3_cond, gen_dict = self._cond_to_device(t3_cond_cpu, gen_dict_cpu)
            log.info("Cond cache: disk HIT key=%s precision=%s", cache_key[:20], self._render_precision)
            # Clear any stale prior tokens for this key — disk load means
            # a new session; last session's acoustic context is irrelevant.
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
        import soundfile as sf, tempfile, os
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
        t3_cond, gen_dict = self._cond_to_device(self._model.conds.t3, self._model.conds.gen)
        self._model.conds.t3, self._model.conds.gen = t3_cond, gen_dict
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
        t3_cond, gen_dict = self._cond_to_device(self._model.conds.t3, self._model.conds.gen)
        self._model.conds.t3, self._model.conds.gen = t3_cond, gen_dict
        self._cond_mem_put(cache_key, t3_cond, gen_dict)
        self._cond_disk_save(cache_key, t3_cond, gen_dict)
        return t3_cond, gen_dict

    def _ensure_conditionals(self, sample_path: Path, sample_hash: str) -> None:
        """
        Prepare voice conditionals for the given sample if not already cached.

        On a cache hit (same sample hash as last call), the model's internal
        self.conds is already correct and we skip prepare_conditionals entirely.
        This is safe because the voice slot serialization guarantees only one
        speaker is active at a time — the cached conditionals always belong to
        the current speaker.

        On a cache miss (new speaker), we write a PCM_16 temp WAV (so librosa
        returns float32), call prepare_conditionals(), cache the new hash, and
        clean up the previous temp WAV.
        """
        if sample_hash == self._cond_sample_hash:
            log.debug(
                "Chatterbox: conditionals cache HIT — reusing voice embedding for hash=%s",
                sample_hash[:8],
            )
            return

        import soundfile as sf

        log.info(
            "Chatterbox: conditionals cache MISS — preparing new voice embedding "
            "hash=%s (was %s)",
            sample_hash[:8],
            self._cond_sample_hash[:8] if self._cond_sample_hash else "none",
        )

        # Validate minimum reference clip length
        info = sf.info(str(sample_path))
        if info.duration < 5.0:
            raise ValueError(
                f"Chatterbox requires a reference clip of at least 5 seconds. "
                f"'{sample_path.name}' is only {info.duration:.1f}s."
            )

        # Write a PCM_16 WAV — guarantees librosa returns float32 on load
        audio_data, sr = sf.read(str(sample_path), dtype='float32')
        if audio_data.ndim > 1:
            audio_data = audio_data.mean(axis=1)
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            new_tmp_path = tmp.name
        sf.write(new_tmp_path, audio_data, sr, subtype='PCM_16')

        # Prepare conditionals into model.conds
        self._model.prepare_conditionals(new_tmp_path)

        # Evict previous temp WAV
        if self._cond_tmp_wav and os.path.exists(self._cond_tmp_wav):
            try:
                os.unlink(self._cond_tmp_wav)
            except Exception as e:
                log.warning("Chatterbox: failed to delete old temp WAV %s: %s",
                            self._cond_tmp_wav, e)

        self._cond_sample_hash = sample_hash
        self._cond_tmp_wav     = new_tmp_path
        log.debug("Chatterbox: new temp WAV at %s", new_tmp_path)

    def _write_tail_sidecar_file(self, cache_dir: str | Path, cache_key: str, tail,
                                 voice_key: str, voice_context: str, timing: bool) -> None:
        """Persist tail tokens for future explicit continuation. Non-fatal."""
        try:
            import time as _time_mod
            import torch as _ts
            _sidecar_t0 = _time_mod.perf_counter() if timing else 0.0
            _sidecar_dir = Path(cache_dir) / self.provider_id
            _sidecar_dir.mkdir(parents=True, exist_ok=True)
            _sidecar_tmp = _sidecar_dir / f"{cache_key}.tokens.pt.tmp"
            _sidecar = _sidecar_dir / f"{cache_key}.tokens.pt"
            _ts.save({
                "tokens": tail.cpu(),
                "voice_key": voice_key,
                "voice_context": voice_context,
                "precision": self._render_precision,
            }, _sidecar_tmp)
            _sidecar_tmp.rename(_sidecar)
            if timing:
                log.info("Chatterbox timing: sidecar_write=%.3fs mode=%s",
                         _time_mod.perf_counter() - _sidecar_t0,
                         self._tail_sidecar_mode)
        except Exception as _e:
            log.debug("Tail token sidecar write failed (non-fatal): %s", _e)

    def _submit_tail_sidecar_write(self, cache_dir: str | Path | None, cache_key: str | None,
                                   tail, voice_key: str, voice_context: str, timing: bool) -> None:
        if not cache_key or not cache_dir or tail is None or self._tail_sidecar_mode == "off":
            return
        # Tail is already CPU-detached at call sites. Clone it so the background
        # writer owns immutable payload data even if caller updates local refs.
        try:
            tail_payload = tail.detach().cpu().clone()
        except Exception:
            tail_payload = tail
        if self._tail_sidecar_mode == "sync":
            self._write_tail_sidecar_file(cache_dir, cache_key, tail_payload, voice_key, voice_context, timing)
            return
        try:
            self._tail_sidecar_executor.submit(
                self._write_tail_sidecar_file,
                cache_dir, cache_key, tail_payload, voice_key, voice_context, timing,
            )
            if timing:
                log.info("Chatterbox timing: sidecar_write=deferred mode=defer")
        except Exception as _e:
            log.debug("Tail token sidecar defer failed (non-fatal): %s", _e)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import numpy as np
        import time as _time_mod

        _timing     = os.environ.get("RRV_CB_TIMING", "0") == "1"
        _prep_start = _time_mod.perf_counter()

        cfg_weight          = request.cfg_weight          if request.cfg_weight          is not None else 0.5
        exaggeration        = request.exaggeration        if request.exaggeration        is not None else 0.5
        # cfg_weight=0.0 is invalid — chatterbox always runs CFG batch mode and
        # zeroing the uncond row is handled internally. Clamp to valid range.
        cfg_weight          = max(0.1, min(cfg_weight, 3.0))
        exaggeration        = max(0.1, min(exaggeration, 3.0))
        temperature         = request.cb_temperature      if request.cb_temperature      is not None else 0.8
        top_p               = request.cb_top_p            if request.cb_top_p            is not None else 1.0
        repetition_penalty  = request.cb_repetition_penalty if request.cb_repetition_penalty is not None else 1.2
        _MAX_NEW_TOKENS      = int(os.environ.get("RRV_CB_MAX_NEW_TOKENS", "1000"))

        # Route: blend vs single reference — both paths go through the two-level
        # conditioning cache (memory → disk → compute).
        blend_entries = [e for e in request.blend if e.get("sample_path")] if request.blend else []
        _cond_start = _time_mod.perf_counter()
        if blend_entries:
            t3_cond, gen_dict = self._cond_get_or_compute_blend(blend_entries, exaggeration)
            # Apply to model and set up generate() bypass
            self._model.conds.t3  = t3_cond
            self._model.conds.gen = gen_dict
            self._setup_blend_generate(t3_cond, gen_dict)
        else:
            if request.sample_path is None:
                raise ValueError("Chatterbox Full requires either a reference sample or a blend.")
            t3_cond, gen_dict = self._cond_get_or_compute_single(
                request.sample_path, exaggeration)
            # Fully replace model conds — no partial state from prior request
            self._model.conds.t3  = t3_cond
            self._model.conds.gen = gen_dict
            self._is_blend_active = False
            # Remove any stale blend bypass
            if hasattr(self._model, "_rrv_blend_generate"):
                del self._model._rrv_blend_generate
        _cond_elapsed = _time_mod.perf_counter() - _cond_start

        # Conditioning cache identity: only values that affect prepare_conditionals().
        if blend_entries:
            _cond_voice_key = self._cond_key_blend(blend_entries, exaggeration)
        else:
            _cond_voice_key = self._cond_key_single(request.sample_path, exaggeration)

        # Prior-token identity: generated-token tails are context-sensitive and
        # must include all generation controls that can alter token trajectory.
        _voice_key = self._prior_voice_key(
            _cond_voice_key,
            request,
            cfg_weight=cfg_weight,
            temperature=temperature,
            top_p=top_p,
            repetition_penalty=repetition_penalty,
            max_new_tokens=_MAX_NEW_TOKENS,
        )

        # Set deterministic seed if requested
        if request.synthesis_seed is not None:
            import torch as _torch
            _torch.manual_seed(request.synthesis_seed)
            _torch.cuda.manual_seed_all(request.synthesis_seed)

        # Split into sentence-boundary chunks.
        chunks = _split_into_chunks(request.text)
        total  = len(chunks)
        _progress_cb = request.progress_callback

        _prep_elapsed = _time_mod.perf_counter() - _prep_start
        if _timing:
            log.info(
                "Chatterbox timing: prep=%.3fs (cond=%.3fs) chunks=%d chars=%d",
                _prep_elapsed, _cond_elapsed, total, len(request.text),
            )

        # Prior speech token priming.
        # Texts with ≤ _SHORT_WORD_THRESHOLD words use the last _PRIOR_TOKEN_LEN
        # tokens from the previous synthesis as cond_prompt_speech_tokens.
        # This gives T3 the acoustic rhythm of the preceding speech rather than
        # the cold reference clip, preventing single-word distortion.
        # All synthesis goes through t3.inference() directly so we can always
        # capture the generated tokens for the next request.
        _SHORT_WORD_THRESHOLD = int(
            __import__('os').environ.get('RRV_CB_PRIOR_TOKEN_WORDS', '3'))
        _PRIOR_TOKEN_LEN = int(
            __import__('os').environ.get('RRV_CB_PRIOR_TOKEN_LEN', '75'))

        import torch as _torch
        import torch.nn.functional as _F
        from chatterbox.models.t3.modules.cond_enc import T3Cond as _T3Cond
        from chatterbox.models.s3tokenizer import drop_invalid_tokens as _drop_invalid

        def _load_tail_tokens_for_cache_key(cache_key: str):
            """Load persisted prior T3 tail tokens for an explicit continuation ref.

            Maintainer note:
            This is intentionally separate from conditioning cache reuse. We only
            load the prior speech token sidecar here so an explicitly chained
            same-speaker batch segment can continue prosody/rhythm even when the
            prior segment was a cache hit or was synthesized in an earlier worker
            lifetime. Do not mix this with conditioning cache behavior.
            """
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
                    sidecar_precision = (payload.get('precision') or 'fp32').strip().lower()
                    if sidecar_precision != self._render_precision:
                        log.debug("Tail token sidecar precision mismatch for %s — ignoring", cache_key[:12])
                        return None, ""
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
            _token_t0 = _time_mod.perf_counter() if _timing else 0.0
            text_proc = self._model.tokenizer.text_to_tokens(chunk_text).to(self._model.device)
            _token_elapsed = (_time_mod.perf_counter() - _token_t0) if _timing else 0.0

            _pad_t0 = _time_mod.perf_counter() if _timing else 0.0
            # Chatterbox T3 always runs in CFG batch mode (bos_embed is unconditionally
            # doubled). text_proc must always be [2, seq] regardless of cfg_weight.
            # When cfg_weight=0.0, the uncond row is zeroed inside prepare_input_embeds.
            text_proc = _torch.cat([text_proc, text_proc], dim=0)
            sot = self._model.t3.hp.start_text_token
            eot = self._model.t3.hp.stop_text_token
            text_proc = _F.pad(text_proc, (1, 0), value=sot)
            text_proc = _F.pad(text_proc, (0, 1), value=eot)
            _pad_elapsed = (_time_mod.perf_counter() - _pad_t0) if _timing else 0.0

            with _torch.inference_mode():
                _t3_t0 = _time_mod.perf_counter() if _timing else 0.0
                speech_tokens = self._model.t3.inference(
                    t3_cond=active_t3_cond,
                    text_tokens=text_proc,
                    max_new_tokens=_MAX_NEW_TOKENS,
                    temperature=temperature,
                    cfg_weight=cfg_weight,
                    repetition_penalty=repetition_penalty,
                    min_p=0.05,
                    top_p=top_p,
                )
                _t3_elapsed = (_time_mod.perf_counter() - _t3_t0) if _timing else 0.0

                _clean_t0 = _time_mod.perf_counter() if _timing else 0.0
                clean = _drop_invalid(speech_tokens[0])
                clean = clean[clean < 6561].to(self._model.device)
                _clean_elapsed = (_time_mod.perf_counter() - _clean_t0) if _timing else 0.0

                _s3_t0 = _time_mod.perf_counter() if _timing else 0.0
                wav, _ = self._model.s3gen.inference(
                    speech_tokens=clean,
                    ref_dict=gen_dict,
                )
                _s3_elapsed = (_time_mod.perf_counter() - _s3_t0) if _timing else 0.0

                _cpu_t0 = _time_mod.perf_counter() if _timing else 0.0
                wav_np = wav.squeeze(0).detach().cpu().numpy()
                _cpu_elapsed = (_time_mod.perf_counter() - _cpu_t0) if _timing else 0.0

                _wm_t0 = _time_mod.perf_counter() if _timing else 0.0
                wav_np = self._model.watermarker.apply_watermark(
                    wav_np, sample_rate=self._model.sr)
                _wm_elapsed = (_time_mod.perf_counter() - _wm_t0) if _timing else 0.0
            if _timing:
                log.info(
                    "Chatterbox timing: tokenize=%.3fs pad=%.3fs T3=%.3fs clean=%.3fs S3Gen=%.3fs cpu_copy=%.3fs watermark=%.3fs tokens=%d samples=%d",
                    _token_elapsed,
                    _pad_elapsed,
                    _t3_elapsed,
                    _clean_elapsed,
                    _s3_elapsed,
                    _cpu_elapsed,
                    _wm_elapsed,
                    len(clean),
                    len(wav_np),
                )
            return wav_np, clean

        _last_tail_for_sidecar = None
        _last_ctx_for_sidecar = request.voice_context or ""

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
                    self._prior_speech_tokens.move_to_end(_voice_key)
                    prior_tokens, _prior_ctx = _prior_entry
                    if _prior_ctx != _cur_ctx and not explicit_continue:
                        prior_tokens = None
                        log.debug("Tail token context mismatch (%s vs %s) — using reference tokens",
                                  _prior_ctx, _cur_ctx)

                if explicit_continue and prior_tokens is None:
                    prior_tokens, _prior_ctx = _load_tail_tokens_for_cache_key(request.continue_from_cache_key)
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

                _chunk_prep_start = _time_mod.perf_counter() if _timing else 0.0
                if use_prior:
                    # Swap reference tokens for prior generation tokens
                    active_t3 = _T3Cond(
                        speaker_emb=t3_cond.speaker_emb,
                        cond_prompt_speech_tokens=prior_tokens,
                        emotion_adv=t3_cond.emotion_adv,
                    ).to(device=self._torch_device)
                    log.debug(
                        "Chatterbox Full: chunk %d/%d primed (%d words) using %d prior tokens",
                        i + 1, total, word_count, prior_tokens.shape[-1])
                else:
                    active_t3 = t3_cond
                _chunk_prep_elapsed = _time_mod.perf_counter() - _chunk_prep_start

                wav_np, clean_tokens = _run_inference(chunk_text, active_t3)
                # patch timing line to include chunk prep
                if _timing:
                    log.info(
                        "Chatterbox timing: chunk=%d/%d chunk_prep=%.3fs prior=%s",
                        i + 1, total, _chunk_prep_elapsed, use_prior,
                    )

                # Update in-memory prior context. Store tail tokens on CPU and
                # bound this cache, otherwise each new voice/sample retains CUDA VRAM.
                tail = clean_tokens[-_PRIOR_TOKEN_LEN:].unsqueeze(0).detach().cpu()
                # Store with voice_context tag to prevent cross-slot contamination
                _ctx_tag = request.voice_context or ""
                if self._prior_token_cache_size > 0:
                    self._prior_speech_tokens[_voice_key] = (tail, _ctx_tag)
                    self._prior_speech_tokens.move_to_end(_voice_key)

                    # LRU eviction only drops dictionary references. Do not force
                    # Python/CUDA cleanup here; let normal allocator behavior handle it.
                    while len(self._prior_speech_tokens) > self._prior_token_cache_size:
                        self._prior_speech_tokens.popitem(last=False)
                else:
                    self._prior_speech_tokens.clear()

                # Persist only the final chunk tail for this request. The next
                # chained segment needs the last generated tokens, not each
                # intermediate chunk. Actual disk write is submitted after all
                # chunks are synthesized so storage latency stays off hot path.
                _last_tail_for_sidecar = tail
                _last_ctx_for_sidecar = _ctx_tag

                samples = wav_np.squeeze()
                if samples.ndim > 1:
                    # Stereo or multi-channel output — mix down to mono
                    samples = samples.mean(axis=0)
                all_samples.append(samples.astype(np.float32))
                log.debug("Chatterbox Full: chunk %d/%d synthesized (%d chars)",
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
            result = pcm_to_ogg(np.zeros(self._model.sr, dtype=np.float32), self._model.sr)
            # Sidecar write is intentionally scheduled after response audio is
            # fully assembled/encoded so busy storage cannot slow concat/OGG
            # work. In defer mode this submits to a background writer and returns
            # immediately; in sync mode it preserves explicitly requested old
            # blocking behavior.
            self._submit_tail_sidecar_write(
                request.cache_dir,
                request.cache_key,
                _last_tail_for_sidecar,
                _voice_key,
                _last_ctx_for_sidecar,
                _timing,
            )
            return result

        _concat_t0 = _time_mod.perf_counter() if _timing else 0.0
        combined = np.concatenate(all_samples)
        _concat_elapsed = (_time_mod.perf_counter() - _concat_t0) if _timing else 0.0
        _enc_t0 = _time_mod.perf_counter() if _timing else 0.0
        result = pcm_to_ogg(combined, self._model.sr)
        if _timing:
            log.info(
                "Chatterbox timing: concat=%.3fs ogg_encode=%.3fs samples=%d",
                _concat_elapsed,
                _time_mod.perf_counter() - _enc_t0,
                len(combined),
            )

        # Submit sidecar write last. This keeps storage I/O out of synthesis,
        # concat, and OGG encode timings in normal defer mode.
        self._submit_tail_sidecar_write(
            request.cache_dir,
            request.cache_key,
            _last_tail_for_sidecar,
            _voice_key,
            _last_ctx_for_sidecar,
            _timing,
        )
        return result
