# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/f5tts_backend.py
#
# F5-TTS backend with BigVGAN vocoder support.
#
# Requires: pip install f5-tts torch torchaudio
#
# Model files MUST be pre-placed in RRV_MODELS_DIR/f5tts/ before starting.
# The server NEVER downloads anything at runtime.
#
# Vocos (default):
#   data/models/f5tts/F5TTS_v1_Base/model_1250000.safetensors
#   data/models/f5tts/vocos/
#
# BigVGAN (optional, higher fidelity — DIFFERENT model checkpoint):
#   data/models/f5tts/F5TTS_Base_bigvgan/model_1250000.pt
#   data/models/f5tts/bigvgan/
#
#   The BigVGAN checkpoint is a SEPARATE model trained against BigVGAN's mel
#   spec format. You cannot use BigVGAN with the v1_Base Vocos checkpoint.
#
#   Download:
#     mkdir -p data/models/f5tts/F5TTS_Base_bigvgan
#     wget -O data/models/f5tts/F5TTS_Base_bigvgan/model_1250000.pt \
#       "https://huggingface.co/SWivid/F5-TTS/resolve/main/F5TTS_Base_bigvgan/model_1250000.pt"
#
# Vocoder selection (RRV_F5_VOCODER env var):
#   "auto"    — BigVGAN if F5TTS_Base_bigvgan checkpoint staged, else Vocos
#   "bigvgan" — require BigVGAN, fail if not staged
#   "vocos"   — always use Vocos

from __future__ import annotations

import asyncio
import logging
import os
from pathlib import Path
from typing import Optional

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from ..cache import compute_file_hash
from ..samples import resolve_sample_for_provider
from .audio import pcm_to_ogg, estimate_duration

log = logging.getLogger(__name__)

_VOCODER_MODE = os.environ.get("RRV_F5_VOCODER", "auto").lower()


class F5TtsBackend(AbstractTtsBackend):

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._vocoder       = None
        self._vocoder_name  = ""     # "vocos" or "bigvgan"
        self._model_version = ""
        self._infer_lock    = asyncio.Lock()
        # Cache: str(sample_path) -> (ref_audio, processed_ref_text)
        self._ref_cache: dict[str, tuple] = {}

    # ── Identity ──────────────────────────────────────────────────────────────

    @property
    def provider_id(self) -> str:
        return "f5tts"

    @property
    def display_name(self) -> str:
        suffix = f" [{self._vocoder_name}]" if self._vocoder_name else ""
        return f"F5-TTS{suffix}"

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

    def extra_controls(self) -> dict:
        vocoder = self._vocoder_name if self._vocoder_name else "vocos"
        default_nfe = 32 if vocoder == "bigvgan" else 48
        return {
            "cfg_strength": {
                "type":        "float",
                "default":     2.0,
                "min":         0.5,
                "max":         3.0,
                "description": "Reference voice adherence. 2.0 = natural default. "
                               "Lower (0.5) = exaggerated expressive style. "
                               "Higher (3.0) = tighter voice match. Above 3.0 causes distortion.",
            },
            "nfe_step": {
                "type":        "int",
                "default":     default_nfe,
                "min":         8,
                "max":         64,
                "description": f"ODE solver steps. Higher = better quality, slower synthesis. "
                               f"Default {default_nfe} is the recommended sweet spot.",
            },
            "sway_sampling_coef": {
                "type":        "float",
                "default":     -1.0,
                "min":         -1.0,
                "max":         1.0,
                "description": "ODE time step distribution. -1.0 = sway (optimal), "
                               "0.0 = uniform. Change only for experimentation.",
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "F5-TTS loaded: model_version=%s device=%s vocoder=%s",
            self._model_version, self._torch_device, self._vocoder_name,
        )

    def _load_sync(self) -> None:
        import torch
        import inspect

        try:
            from f5_tts.infer.utils_infer import load_model, load_vocoder
        except ImportError:
            raise RuntimeError("f5-tts is not installed. Run: pip install f5-tts")

        f5_dir          = self._models_dir / "f5tts"
        vocos_model     = f5_dir / "F5TTS_v1_Base" / "model_1250000.safetensors"
        bigvgan_ckpt    = f5_dir / "F5TTS_Base_bigvgan" / "model_1250000.pt"
        vocos_dir       = f5_dir / "vocos"
        bigvgan_dir     = f5_dir / "bigvgan"

        # ── Determine vocoder and checkpoint ──────────────────────────────────
        bigvgan_available = bigvgan_ckpt.exists() and (bigvgan_dir / "config.json").exists()

        if _VOCODER_MODE == "bigvgan":
            if not bigvgan_available:
                raise RuntimeError(
                    f"RRV_F5_VOCODER=bigvgan but BigVGAN not fully staged.\n"
                    f"Need: {bigvgan_ckpt}\n"
                    f"Need: {bigvgan_dir}/config.json"
                )
            use_bigvgan = True
        elif _VOCODER_MODE == "vocos":
            use_bigvgan = False
        else:  # auto
            use_bigvgan = bigvgan_available
            log.info(
                "F5-TTS: vocoder=auto — %s",
                "BigVGAN checkpoint found, using BigVGAN" if use_bigvgan
                else "BigVGAN checkpoint not found, using Vocos"
            )

        # ── Set mel_spec_type global before loading anything ──────────────────
        # utils_infer.mel_spec_type is a module-level global that must match
        # the checkpoint being loaded. Set it before load_model so the model
        # is configured correctly at construction time.
        import f5_tts.infer.utils_infer as _utils_infer
        _utils_infer.mel_spec_type = "bigvgan" if use_bigvgan else "vocos"
        log.info("F5-TTS: mel_spec_type = '%s'", _utils_infer.mel_spec_type)

        # ── Load vocoder ───────────────────────────────────────────────────────
        if use_bigvgan:
            log.info("F5-TTS: loading BigVGAN vocoder from %s", bigvgan_dir)
            try:
                import json
                import sys
                import torch as _torch

                # Import bigvgan.py from the local source directory
                if str(bigvgan_dir) not in sys.path:
                    sys.path.insert(0, str(bigvgan_dir))
                import bigvgan as _bigvgan

                with open(bigvgan_dir / "config.json") as _f:
                    config = json.load(_f)

                try:
                    from env import AttrDict
                except ImportError:
                    class AttrDict(dict):
                        def __init__(self, *args, **kwargs):
                            super().__init__(*args, **kwargs)
                            self.__dict__ = self

                h = AttrDict(config)
                self._vocoder = _bigvgan.BigVGAN(h)

                ckpt_path = None
                for name in ("bigvgan_generator", "generator", "pytorch_model"):
                    for ext in (".pt", ".bin"):
                        candidate = bigvgan_dir / f"{name}{ext}"
                        if candidate.exists():
                            ckpt_path = candidate
                            break
                    if ckpt_path:
                        break

                if ckpt_path is None:
                    raise FileNotFoundError(f"BigVGAN vocoder checkpoint not found in {bigvgan_dir}")

                log.info("F5-TTS: loading BigVGAN vocoder checkpoint from %s", ckpt_path.name)
                state_dict = _torch.load(str(ckpt_path), map_location="cpu")
                if isinstance(state_dict, dict):
                    state_dict = state_dict.get("generator", state_dict)
                self._vocoder.load_state_dict(state_dict)
                self._vocoder.remove_weight_norm()
                self._vocoder.eval()
                self._vocoder = self._vocoder.to(self._torch_device)
                self._vocoder_name = "bigvgan"
                log.info("F5-TTS: BigVGAN vocoder loaded")

            except Exception as e:
                log.warning("F5-TTS: BigVGAN vocoder load failed (%s) — falling back to Vocos", e)
                use_bigvgan = False
                _utils_infer.mel_spec_type = "vocos"

        if not use_bigvgan:
            if not vocos_dir.exists() or not (vocos_dir / "pytorch_model.bin").exists():
                raise RuntimeError(
                    f"Vocos not found at {vocos_dir}\n"
                    f"Download from: https://huggingface.co/charactr/vocos-mel-24khz"
                )
            log.info("F5-TTS: loading Vocos from %s", vocos_dir)
            self._vocoder = load_vocoder(
                vocoder_name="vocos",
                is_local=True,
                local_path=str(vocos_dir),
                device=self._torch_device,
            )
            self._vocoder_name = "vocos"

        # ── Load transformer checkpoint ────────────────────────────────────────
        # BigVGAN uses F5TTS_Base_bigvgan/model_1250000.pt (separate checkpoint)
        # Vocos  uses F5TTS_v1_Base/model_1250000.safetensors
        if use_bigvgan:
            model_file = bigvgan_ckpt
            log.info("F5-TTS: loading BigVGAN transformer from %s", model_file)
        else:
            model_file = vocos_model
            if not model_file.exists():
                raise RuntimeError(
                    f"F5-TTS model not found: {model_file}\n"
                    f"Download from: https://huggingface.co/SWivid/F5-TTS"
                )
            log.info("F5-TTS: loading Vocos transformer from %s", model_file)

        # Load model config from yaml (follows CLI pattern)
        try:
            from omegaconf import OmegaConf
            from hydra.utils import get_class
            from importlib.resources import files as pkg_files

            model_name = "F5TTS_Base_bigvgan" if use_bigvgan else "F5TTS_v1_Base"
            # Fall back to F5TTS_Base.yaml for bigvgan (that's what the CLI uses)
            cfg_name = "F5TTS_Base" if use_bigvgan else "F5TTS_v1_Base"
            cfg_path = str(pkg_files("f5_tts").joinpath(f"configs/{cfg_name}.yaml"))
            model_cfg = OmegaConf.load(cfg_path)
            model_cls = get_class(f"f5_tts.model.{model_cfg.model.backbone}")
            model_arc = model_cfg.model.arch

            load_params = inspect.signature(load_model).parameters
            kwargs = dict(
                model_cls=model_cls,
                model_cfg=model_arc,
                ckpt_path=str(model_file),
            )
            if "mel_spec_type" in load_params: kwargs["mel_spec_type"] = self._vocoder_name
            if "vocab_file"    in load_params: kwargs["vocab_file"]    = ""
            if "device"        in load_params: kwargs["device"]        = self._torch_device

            self._model = load_model(**kwargs)

        except Exception as e:
            # Fallback: load using DiT directly (older API)
            log.warning("F5-TTS: yaml-based load failed (%s) — trying DiT fallback", e)
            from f5_tts.model import DiT
            model_cfg_dict = dict(dim=1024, depth=22, heads=16, ff_mult=2, text_dim=512, conv_layers=4)
            load_params = inspect.signature(load_model).parameters
            kwargs = dict(model_cls=DiT, model_cfg=model_cfg_dict, ckpt_path=str(model_file))
            if "mel_spec_type" in load_params: kwargs["mel_spec_type"] = self._vocoder_name
            if "vocab_file"    in load_params: kwargs["vocab_file"]    = ""
            if "device"        in load_params: kwargs["device"]        = self._torch_device
            self._model = load_model(**kwargs)

        if use_bigvgan:
            self._model = self._model.to(torch.float32)

        self._model_version = compute_file_hash(model_file)

    # ── Voices ────────────────────────────────────────────────────────────────

    def get_voices(self) -> list[VoiceInfo]:
        return []

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
                "F5-TTS requires a transcript of the reference audio clip. "
                "Create a .ref.txt sidecar alongside the sample."
            )

        if request.blend:
            raise ValueError("F5-TTS does not support voice blending.")

        loop = asyncio.get_event_loop()
        async with self._infer_lock:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import inspect
        import numpy as np
        from f5_tts.infer.utils_infer import preprocess_ref_audio_text, infer_process

        # ── Resolve provider-specific clip ─────────────────────────────────
        sample_path = request.sample_path
        ref_text    = request.ref_text

        if sample_path is not None and request.samples_dir is not None:
            provider_info = resolve_sample_for_provider(
                samples_dir=request.samples_dir,
                sample_id=request.sample_id or "",
                provider_id=self.provider_id,
            )
            if provider_info and provider_info.ref_text:
                from ..samples import _base_stem
                base = _base_stem(request.sample_id or "")
                candidate = request.samples_dir / base / provider_info.filename
                if not candidate.exists():
                    candidate = request.samples_dir / provider_info.filename
                if candidate.exists():
                    sample_path = candidate
                    ref_text    = provider_info.ref_text
                    log.debug("F5-TTS: using provider clip '%s'", candidate.name)

        # ── Preprocess reference audio (cached per path) ───────────────────
        cache_key = str(sample_path)
        if cache_key not in self._ref_cache:
            log.debug("F5-TTS: preprocessing reference '%s'", sample_path.name)
            preprocess_params = inspect.signature(preprocess_ref_audio_text).parameters
            preprocess_kwargs = dict(ref_audio_orig=str(sample_path), ref_text=ref_text)
            if "clip_short"  in preprocess_params: preprocess_kwargs["clip_short"]  = False
            if "show_info"   in preprocess_params: preprocess_kwargs["show_info"]   = lambda x: log.debug("F5 preprocess: %s", x)
            if "device"      in preprocess_params: preprocess_kwargs["device"]      = self._torch_device
            ref_audio, processed_ref_text = preprocess_ref_audio_text(**preprocess_kwargs)
            self._ref_cache[cache_key] = (ref_audio, processed_ref_text)
        else:
            ref_audio, processed_ref_text = self._ref_cache[cache_key]
            log.debug("F5-TTS: cache hit for '%s'", sample_path.name)

        # ── Synthesis parameters ───────────────────────────────────────────
        # Default nfe_step: 48 for Vocos (sweet spot — better than 32, faster than 64),
        # 32 for BigVGAN (already high quality; higher steps would be very slow).
        default_nfe = 32 if self._vocoder_name == "bigvgan" else 48
        nfe_step    = request.nfe_step if request.nfe_step is not None else default_nfe
        cfg_strength_val    = request.cfg_strength        if request.cfg_strength        is not None else 2.0
        cross_fade_duration = request.cross_fade_duration if request.cross_fade_duration is not None else 0.15

        log.debug(
            "F5-TTS synth: vocoder=%s nfe=%d cfg=%.2f xfade=%.3f speed=%.2f sample='%s'",
            self._vocoder_name, nfe_step, cfg_strength_val, cross_fade_duration,
            request.speech_rate, sample_path.name if sample_path else "none",
        )

        # ── Generate using infer_process (matches CLI) ─────────────────────
        gen_text = request.text.rstrip() + " ...  "
        _progress_cb = request.progress_callback

        infer_params = inspect.signature(infer_process).parameters
        infer_kwargs = dict(
            ref_audio=ref_audio,
            ref_text=processed_ref_text,
            gen_text=gen_text,
            model_obj=self._model,
            vocoder=self._vocoder,
        )
        if "mel_spec_type"       in infer_params: infer_kwargs["mel_spec_type"]       = self._vocoder_name
        if "target_rms"          in infer_params: infer_kwargs["target_rms"]          = 0.1
        if "cross_fade_duration" in infer_params: infer_kwargs["cross_fade_duration"] = cross_fade_duration
        if "nfe_step"            in infer_params: infer_kwargs["nfe_step"]            = nfe_step
        if "cfg_strength"        in infer_params: infer_kwargs["cfg_strength"]        = cfg_strength_val
        # sway_sampling_coef: -1.0 = optimal non-uniform steps (default), 0.0 = uniform
        # Values outside [-1, 1] cause ODE solver errors at high nfe_step.
        sway = request.sway_sampling_coef if request.sway_sampling_coef is not None else -1.0
        if "sway_sampling_coef" in infer_params: infer_kwargs["sway_sampling_coef"] = sway
        if "speed"               in infer_params: infer_kwargs["speed"]               = request.speech_rate
        if request.lang_code:
            if "lang_code" in infer_params:
                infer_kwargs["lang_code"] = request.lang_code
            elif "language" in infer_params:
                infer_kwargs["language"] = request.lang_code
            elif "lang" in infer_params:
                infer_kwargs["lang"] = request.lang_code
        if "fix_duration"        in infer_params: infer_kwargs["fix_duration"]        = None
        if "device"              in infer_params: infer_kwargs["device"]              = self._torch_device

        if _progress_cb is not None:
            try: _progress_cb(0, 1)
            except Exception: pass

        audio_segment, final_sample_rate, _ = infer_process(**infer_kwargs)

        if _progress_cb is not None:
            try: _progress_cb(1, 1)
            except Exception: pass

        samples = np.array(audio_segment, dtype=np.float32)
        if samples.ndim > 1:
            samples = samples.squeeze()

        return pcm_to_ogg(samples, final_sample_rate)


# ── Helpers ───────────────────────────────────────────────────────────────────

# OGG encoding — see backends/audio.py
