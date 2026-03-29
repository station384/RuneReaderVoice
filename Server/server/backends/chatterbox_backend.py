# SPDX-License-Identifier: GPL-3.0-or-later
# server/backends/chatterbox_backend.py
#
# Chatterbox Turbo backend by Resemble AI.
# MIT licensed — safe for all use cases.
#
# Requires: pip install chatterbox-tts --no-deps
#           pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
#                       s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps
#
# Model files are downloaded from HuggingFace on first use via from_pretrained().
# For air-gapped deployments, pre-place model files — see MODELS.txt for details.
#
# Supports:
#   - Zero-shot voice cloning from a reference audio clip
#   - Paralinguistic tags in text: [laugh], [chuckle], [cough], [sigh] etc.
#   - English (primary); multilingual via ChatterboxMultilingualTTS (future)
#   - 350M parameters — lighter than F5-TTS, faster inference
#   - CPU (slow) or CUDA/ROCm GPU

from __future__ import annotations

import asyncio
import io
import logging
from pathlib import Path

from .base import AbstractTtsBackend, SynthesisRequest, SynthesisResult, VoiceInfo
from .audio import pcm_to_ogg, estimate_duration
from ..cache import compute_file_hash

log = logging.getLogger(__name__)


class ChatterboxBackend(AbstractTtsBackend):

    # Maximum concurrent synthesis requests — prevents GPU memory pressure
    # from unbounded parallel generation. 2 allows reasonable throughput
    # while keeping memory usage predictable.
    _MAX_CONCURRENT = 2

    def __init__(self, models_dir: Path, torch_device: str) -> None:
        self._models_dir    = models_dir
        self._torch_device  = torch_device
        self._model         = None
        self._model_version = ""
        self._voice_cond    = asyncio.Condition()
        self._active_voice_key: str | None = None
        self._active_count  = 0


    def _voice_group_key(self, request: SynthesisRequest) -> str:
        sample_key = str(request.sample_path.resolve()) if request.sample_path is not None else ""
        lang_key = request.lang_code or ""
        return f"{sample_key}|{lang_key}"

    async def _acquire_voice_slot(self, voice_key: str) -> None:
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
        return "chatterbox"

    @property
    def display_name(self) -> str:
        return "Chatterbox Turbo"

    @property
    def supports_base_voices(self) -> bool:
        return False   # Turbo requires a reference clip

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
        return ["en"]

    @property
    def model_version(self) -> str:
        return self._model_version

    def extra_controls(self) -> dict:
        return {
            "cfg_weight": {
                "type": "float",
                "default": 0.0,
                "min": 0.0,
                "max": 3.0,
                "description": "Classifier-free guidance weight for prompt adherence.",
            },
            "exaggeration": {
                "type": "float",
                "default": 0.0,
                "min": 0.0,
                "max": 3.0,
                "description": "Emotion and expressiveness control. 0.0=monotone, 0.5=natural, 1.0+=dramatic.",
            },
        }

    # ── Load ──────────────────────────────────────────────────────────────────

    async def load(self) -> None:
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, self._load_sync)
        log.info(
            "Chatterbox Turbo loaded: model_version=%s device=%s",
            self._model_version, self._torch_device,
        )

    def _load_sync(self) -> None:
        # Patch librosa.load BEFORE importing Chatterbox. tts_turbo.py calls
        # librosa.load() directly in prepare_conditionals() — patching the module
        # attribute here intercepts that call because Python looks up librosa.load
        # at call time, not at import time.
        import librosa
        import numpy as np
        if not getattr(librosa, '_rrv_patched', False):
            _original_load = librosa.load
            def _float32_load(path, *args, **kwargs):
                y, sr = _original_load(path, *args, **kwargs)
                return y.astype(np.float32), sr
            librosa.load = _float32_load
            librosa._rrv_patched = True
            log.info("Chatterbox: patched librosa.load to always return float32")

        try:
            from chatterbox.tts_turbo import ChatterboxTurboTTS
        except ImportError:
            raise RuntimeError(
                "chatterbox-tts is not installed. "
                "Run: pip install chatterbox-tts --no-deps"
            )

        local_model_dir = self._models_dir / "chatterbox"

        if local_model_dir.exists() and any(local_model_dir.iterdir()):
            log.info("Chatterbox Turbo: loading from local models dir: %s", local_model_dir)
            self._model = ChatterboxTurboTTS.from_local(
                str(local_model_dir),
                self._torch_device,
            )
            self._patch_mel_filters()
            import hashlib
            files = sorted(str(p) for p in local_model_dir.rglob("*.safetensors"))
            self._model_version = hashlib.sha256(
                "\n".join(files).encode()
            ).hexdigest()[:8] if files else "local"
        else:
            raise RuntimeError(
                f"Chatterbox Turbo model files not found: {local_model_dir}\n"
                f"Download from: https://huggingface.co/ResembleAI/chatterbox-turbo\n"
                f"Place all files in: {local_model_dir}"
            )

    # ── Voices ────────────────────────────────────────────────────────────────

    def _patch_mel_filters(self) -> None:
        """
        Patch Chatterbox for CPU float32 compatibility.

        On CPU, librosa.load() returns float64 numpy arrays. Chatterbox was only
        ever tested on CUDA where float16/32 is enforced by the driver. On CPU the
        float64 propagates through torch.stft() and hits hard dtype assertions in
        multiple places deep in the model (s3tokenizer, decoder, etc.).

        The cleanest fix is to patch librosa.load at the module level so it always
        returns float32, which is what all of Chatterbox's internal code expects.
        """
        import librosa
        import numpy as np

        if not getattr(librosa, '_rrv_patched', False):
            _original_load = librosa.load

            def _patched_load(path, *args, **kwargs):
                y, sr = _original_load(path, *args, **kwargs)
                return y.astype(np.float32), sr

            librosa.load = _patched_load
            librosa._rrv_patched = True
            log.info("Chatterbox: patched librosa.load to return float32 for CPU compatibility")

        # Also patch S3Tokenizer.log_mel_spectrogram as belt-and-suspenders
        try:
            import torch
            import torch.nn.functional as F
            from chatterbox.models.s3tokenizer.s3tokenizer import S3Tokenizer

            if not getattr(S3Tokenizer, '_rrv_patched', False):
                original_log_mel = S3Tokenizer.log_mel_spectrogram

                def _patched_log_mel(self_tokzr, audio, padding=0):
                    if not torch.is_tensor(audio):
                        audio = torch.from_numpy(audio)
                    audio = audio.to(self_tokzr.device)
                    if padding > 0:
                        audio = F.pad(audio, (0, padding))
                    stft = torch.stft(
                        audio, self_tokzr.n_fft,
                        original_log_mel.__globals__.get('S3_HOP', 160),
                        window=self_tokzr.window.to(self_tokzr.device),
                        return_complex=True,
                    )
                    magnitudes = stft[..., :-1].abs() ** 2
                    mel_filters = self_tokzr._mel_filters.to(self_tokzr.device)
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

        # Patch VoiceEncoder.embeds_from_wavs to cast wavs to float32 before
        # melspectrogram. librosa.resample() returns float64 on CPU which causes
        # the LSTM to fail. This patch is belt-and-suspenders alongside the
        # librosa.load patch since resample() is a separate call path.
        try:
            import numpy as np
            from chatterbox.models.voice_encoder.voice_encoder import VoiceEncoder

            if not getattr(VoiceEncoder, '_rrv_patched', False):
                _original_embeds_from_wavs = VoiceEncoder.embeds_from_wavs

                def _patched_embeds_from_wavs(self_ve, wavs, *args, **kwargs):
                    wavs = [w.astype(np.float32) if hasattr(w, 'astype') else w for w in wavs]
                    return _original_embeds_from_wavs(self_ve, wavs, *args, **kwargs)

                VoiceEncoder.embeds_from_wavs = _patched_embeds_from_wavs
                VoiceEncoder._rrv_patched = True
                log.info("Chatterbox: patched VoiceEncoder.embeds_from_wavs for CPU float32 compatibility")
        except Exception as e:
            log.warning("Chatterbox: could not patch VoiceEncoder: %s", e)


    def get_voices(self) -> list[VoiceInfo]:
        return []  # Turbo requires a reference clip — no built-in named voices

    # ── Synthesize ────────────────────────────────────────────────────────────

    async def synthesize(self, request: SynthesisRequest) -> SynthesisResult:
        if self._model is None:
            raise RuntimeError("Chatterbox Turbo backend is not loaded")

        if request.sample_path is None:
            raise ValueError(
                "Chatterbox Turbo requires a reference audio clip. "
                "Provide sample_id in the request."
            )

        if request.blend:
            raise ValueError("Chatterbox Turbo does not support voice blending.")

        loop = asyncio.get_event_loop()
        voice_key = self._voice_group_key(request)
        await self._acquire_voice_slot(voice_key)
        try:
            ogg_bytes = await loop.run_in_executor(None, self._synthesize_sync, request)
        finally:
            await self._release_voice_slot(voice_key)
        duration = estimate_duration(ogg_bytes)
        return SynthesisResult(ogg_bytes=ogg_bytes, duration_sec=duration)

    def _synthesize_sync(self, request: SynthesisRequest) -> bytes:
        import torchaudio
        import torch

        # Load and validate reference clip
        import soundfile as sf
        info = sf.info(str(request.sample_path))
        if info.duration < 5.0:
            raise ValueError(
                f"Chatterbox Turbo requires a reference clip of at least 5 seconds. "
                f"'{request.sample_path.name}' is only {info.duration:.1f}s."
            )

        # Chatterbox internally calls librosa.load() on the reference audio which
        # returns float64 on CPU. This propagates through the entire pipeline causing
        # dtype assertion failures deep in the model. The fix is to write the
        # reference as a 16-bit PCM WAV — librosa.load() will then return float32.
        import tempfile, os
        import numpy as np
        import soundfile as sf

        audio_data, sr = sf.read(str(request.sample_path), dtype='float32')
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            tmp_path = tmp.name
        try:
            # PCM_16 forces librosa to return float32 on load
            sf.write(tmp_path, audio_data, sr, subtype='PCM_16')
            wav = self._model.generate(
                text=request.text,
                audio_prompt_path=tmp_path,
                cfg_weight=request.cfg_weight if request.cfg_weight is not None else 0.0,
                exaggeration=request.exaggeration if request.exaggeration is not None else 0.0,
            )
        finally:
            os.unlink(tmp_path)

        # wav is a torch tensor — convert to numpy
        if hasattr(wav, 'numpy'):
            samples = wav.squeeze().numpy()
        else:
            samples = np.array(wav).squeeze()

        return pcm_to_ogg(samples, self._model.sr)


# ── Helpers ───────────────────────────────────────────────────────────────────
# OGG encoding — see backends/audio.py



