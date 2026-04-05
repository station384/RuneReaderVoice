#!/usr/bin/env python3
# rrv-cosyvoice-vllm/run_worker.py
#
# CosyVoice3 + vLLM worker subprocess launcher.
#
# This launcher:
#   1. Sets LD_LIBRARY_PATH for NVIDIA libs in this venv (required for ONNX CUDA EP)
#   2. Adds cosyvoice-src and its Matcha-TTS submodule to sys.path
#   3. Registers vLLM model class before any CosyVoice imports
#   4. Redirects HuggingFace cache to data/models/hf-cache/
#   5. Hands off to server.worker
#
# IMPORTANT: Do NOT install CosyVoice's requirements.txt into this venv —
# it will downgrade torch/transformers and break vLLM 0.11.2.
# Install only the minimal deps listed in cosyvoice_vllm_backend.py.
#
# Usage (called automatically by WorkerBackend in the host process):
#   /home/mike/rrvserver/rrv-cosyvoice-vllm/.venv/bin/python run_worker.py \
#       --backend cosyvoice_vllm --socket /tmp/rrv_worker_cosyvoice_vllm_<pid>.sock \
#       --models-dir /home/mike/rrvserver/data/models \
#       --samples-dir /home/mike/rrvserver/data/samples \
#       --gpu auto

import sys
import os
from pathlib import Path

# Directory containing this launcher script
_HERE = Path(__file__).parent.resolve()

# ── Step 1: LD_LIBRARY_PATH for NVIDIA libs ────────────────────────────────
# Must be set before any CUDA / ONNX Runtime imports.
# Without this, ONNX CUDA EP fails with libcudnn_adv.so.9 not found.
_VENV = _HERE / ".venv"
_NVIDIA_LIBS = [
    str(_VENV / "lib" / "python3.11" / "site-packages" / "nvidia" / "cudnn"      / "lib"),
    str(_VENV / "lib" / "python3.11" / "site-packages" / "nvidia" / "cublas"     / "lib"),
    str(_VENV / "lib" / "python3.11" / "site-packages" / "nvidia" / "cuda_runtime" / "lib"),
    str(_VENV / "lib" / "python3.11" / "site-packages" / "nvidia" / "cuda_nvrtc" / "lib"),
]
_existing_ld = os.environ.get("LD_LIBRARY_PATH", "")
_new_ld = ":".join(_NVIDIA_LIBS + ([_existing_ld] if _existing_ld else []))
os.environ["LD_LIBRARY_PATH"] = _new_ld

# ── Step 2: cosyvoice-src paths ────────────────────────────────────────────
# cosyvoice-src is cloned inside rrv-cosyvoice-vllm/ (NOT shared with rrv-cosyvoice/)
# because the vLLM cosyvoice2.py wrapper has a local patch (Union import fix).
_COSY_SRC = _HERE / "cosyvoice-src"
_MATCHA   = _COSY_SRC / "third_party" / "Matcha-TTS"

for _p in [str(_COSY_SRC), str(_MATCHA)]:
    if _p not in sys.path:
        sys.path.insert(0, _p)

os.environ["RRV_COSYVOICE_SRC_DIR"] = str(_COSY_SRC)

# ── Step 3: HuggingFace cache redirect ────────────────────────────────────
_HF_CACHE = _HERE.parent / "data" / "models" / "hf-cache"
_HF_CACHE.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("HF_HUB_CACHE", str(_HF_CACHE))

# ── Step 4: vLLM torch compile cache redirect ──────────────────────────────
# Keep vLLM's compiled kernels inside the project rather than ~/.cache/vllm/
_VLLM_CACHE = _HERE.parent / "data" / "models" / "vllm-cache"
_VLLM_CACHE.mkdir(parents=True, exist_ok=True)
os.environ.setdefault("VLLM_CACHE_ROOT", str(_VLLM_CACHE))

# ── Step 5: Monkey-patch torchaudio.load to use soundfile ──────────────────
# torchaudio 2.9.0 uses torchcodec by default which requires FFmpeg shared
# libs not available in this environment. We replace torchaudio.load with a
# soundfile-based implementation that handles WAV files correctly.
# Must be done BEFORE cosyvoice imports torchaudio.
import torchaudio as _torchaudio
import soundfile as _sf
import torch as _torch

def _load_wav_soundfile(filepath, frame_offset=0, num_frames=-1, normalize=True,
                        channels_first=True, format=None, backend=None):
    data, sample_rate = _sf.read(str(filepath), dtype='float32', always_2d=True)
    # data shape: (frames, channels) -> convert to (channels, frames)
    tensor = _torch.from_numpy(data.T)
    if frame_offset > 0:
        tensor = tensor[:, frame_offset:]
    if num_frames > 0:
        tensor = tensor[:, :num_frames]
    if not channels_first:
        tensor = tensor.T
    return tensor, sample_rate

_torchaudio.load = _load_wav_soundfile

# ── Step 6: rrv-server on sys.path ─────────────────────────────────────────
_SERVER_ROOT = _HERE.parent / "rrv-server"
if str(_SERVER_ROOT) not in sys.path:
    sys.path.insert(0, str(_SERVER_ROOT))

# Hand off to main worker entrypoint
from server.worker import _parse_args, _main  # noqa: E402
import asyncio

if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
