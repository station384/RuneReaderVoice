# RuneReader Voice Server — Ubuntu Installation Guide

Tested on: **Ubuntu 24.04.4 LTS** with **NVIDIA RTX 3080 (16GB)** and **CUDA 13.0**

---

## Prerequisites

### System Info
```
OS:     Ubuntu 24.04.4 LTS (Noble)
GPU:    NVIDIA GeForce RTX 3080 (16GB VRAM)
Driver: 580.105.08
CUDA:   13.0
Python: 3.12.3 (system default — do NOT use for rrv-server)
```

### Notes
- Ubuntu 24.04 ships with Python 3.12 as the system default.
  **rrv-server requires Python 3.11** — Chatterbox Turbo fails to install
  on 3.12 due to `distutils` removal. Install 3.11 alongside the system
  Python without replacing it.
- PyTorch does not yet support CUDA 13.0 directly. Use `--index-url
  https://download.pytorch.org/whl/cu124` — the cu124 wheels work
  correctly with CUDA 13.0 drivers via backward compatibility.
- If the GPU is shared with other workloads (e.g. llama-server), monitor
  VRAM usage before loading all three backends simultaneously.
  F5-TTS needs ~3-4 GB, Chatterbox Turbo ~1-2 GB, Kokoro negligible.

---

## Step 1 — Install System Dependencies

```bash
# Python 3.11 — Ubuntu 24.04 ships with 3.12, rrv-server requires 3.11
sudo add-apt-repository ppa:deadsnakes/ppa -y
sudo apt update
sudo apt install python3.11 python3.11-venv python3.11-dev -y
python3.11 --version
# Expected: Python 3.11.x

# ffmpeg — required for automatic audio/video → WAV conversion
sudo apt install ffmpeg -y
ffmpeg -version | head -1
# Confirmed: ffmpeg version 6.1.1-3ubuntu5 (already installed on Ubuntu 24.04)
# If not present: sudo apt install ffmpeg -y
```

---

## Step 2 — Clone / Copy Server Source

Copy the rrv-server project to the target machine. Recommended location:

```bash
mkdir -p ~/rrv-server
# Copy source files here (scp, rsync, USB, etc.)
cd ~/rrv-server
```

---

## Step 3 — Create Virtual Environment

Always use Python 3.11 explicitly — never the system python3.

```bash
cd ~/rrv-server
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x
```

---

## Step 4 — Install PyTorch (CUDA)

PyTorch must be installed before F5-TTS and Chatterbox to ensure the
CUDA-enabled wheels are used. Do NOT let pip install CPU-only torch
as a dependency of f5-tts.

```bash
# cu124 wheels work with CUDA 13.0 drivers (backward compatible)
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

# Verify CUDA is available
python -c "import torch; print(torch.__version__); print(torch.cuda.is_available()); print(torch.cuda.get_device_name(0))"
# Expected: True, NVIDIA GeForce RTX 3080
# Confirmed: PyTorch 2.6.0+cu124, CUDA True, NVIDIA GeForce RTX 3080 Laptop GPU```

---

## Step 5 — Install Core Dependencies

```bash
pip install -e ".[kokoro]"
```

---

## Step 6 — Install F5-TTS

```bash
pip install f5-tts
```

---

## Step 7 — Install Chatterbox Turbo

Must use --no-deps to avoid numpy downgrade conflict with kokoro-onnx.

```bash
pip install chatterbox-tts --no-deps
pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
            s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps
```

---

## Step 8 — Verify All Backends

```bash
python -c "
import torch
print(f'PyTorch: {torch.__version__}')
print(f'CUDA available: {torch.cuda.is_available()}')
print(f'GPU: {torch.cuda.get_device_name(0)}')
import numpy; print(f'numpy: {numpy.__version__}')
from kokoro_onnx import Kokoro; print('kokoro ok')
from f5_tts.api import F5TTS; print('f5tts ok')
from chatterbox.tts_turbo import ChatterboxTurboTTS; print('chatterbox ok')
"
# Confirmed output on Ubuntu 24.04 / RTX 3080 Laptop:
# PyTorch: 2.6.0+cu124
# CUDA available: True
# GPU: NVIDIA GeForce RTX 3080 Laptop GPU
# numpy: 2.4.3
# kokoro ok
# f5tts ok
# chatterbox ok
```

---

## Step 9 — Place Model Files

Copy model files to the server. The server never downloads from
HuggingFace at runtime — all files must be pre-placed.

```
data/models/
  kokoro-v1.0.onnx
  voices-v1.0.bin
  f5tts/
    F5TTS_v1_Base/
      model_1250000.safetensors
  chatterbox/
    (all files from HuggingFace ResembleAI/chatterbox-turbo)
  whisper/
    v3-turbo/   (all files from HuggingFace openai/whisper-large-v3-turbo)
    tiny/       (all files from HuggingFace openai/whisper-tiny)
```

See `models/MODELS.txt` for exact file lists.

---

## Step 10 — Configure Environment

```bash
cp .env.example .env
# Edit .env:
#   RRV_BACKENDS=kokoro,f5tts,chatterbox
#   RRV_GPU=auto
#   RRV_WHISPER_MODEL_DIR=./data/models/whisper/v3-turbo
#   RRV_SAMPLE_SCAN_INTERVAL=30
```

---

## Step 11 — Start the Server

```bash
source .venv/bin/activate
rrv-server
```

Expected startup log should show:
- `GPU: CUDA selected`
- All three backends registered
- Whisper auto-transcription enabled

---

## Known Issues / Notes

### F5-TTS Vocos vocoder
F5-TTS requires the Vocos vocoder (`charactr/vocos-mel-24khz`). On first run
with internet access it downloads automatically and caches in
`~/.cache/huggingface/`. Pre-place it for air-gapped deployments:

```bash
mkdir -p data/models/f5tts/vocos
VOCOS=$(find ~/.cache/huggingface/hub -path "*/vocos-mel-24khz/snapshots/*" -name "config.yaml" | head -1 | xargs dirname)
cp $VOCOS/config.yaml $VOCOS/pytorch_model.bin data/models/f5tts/vocos/
ls -lh data/models/f5tts/vocos/
# config.yaml (~461 bytes), pytorch_model.bin (~52 MB)
```

Download from: https://huggingface.co/charactr/vocos-mel-24khz

### Chatterbox Turbo CPU dtype patches
On CPU, Chatterbox has float64/float32 dtype mismatches throughout the
model (never tested by Resemble AI on CPU). The server applies automatic
monkey-patches at load time. On CUDA these patches are harmless — they
adapt to the model's actual dtype. No action required.

### VRAM contention
If other CUDA workloads (e.g. llama-server) are running on the same GPU,
monitor free VRAM before loading all backends. F5-TTS needs ~3-4 GB and
Chatterbox Turbo needs ~1-2 GB. If VRAM is tight, load only the backends
you need via RRV_BACKENDS.

### systemd service (optional)
To run as a background service:
```bash
# Create /etc/systemd/system/rrv-server.service
[Unit]
Description=RuneReader Voice Server
After=network.target

[Service]
Type=simple
User=mike
WorkingDirectory=/home/mike/rrv-server
ExecStart=/home/mike/rrv-server/.venv/bin/rrv-server
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target

sudo systemctl daemon-reload
sudo systemctl enable rrv-server
sudo systemctl start rrv-server
sudo journalctl -u rrv-server -f
```

---

## Confirmed Working Configuration

Tested and verified on **Ubuntu 24.04.4 LTS / RTX 3080 Laptop GPU (16GB)**:

| Component | Version | Status |
|---|---|---|
| Python | 3.11.x (via deadsnakes) | ✅ |
| PyTorch | 2.6.0+cu124 | ✅ CUDA |
| numpy | 2.4.3 | ✅ |
| kokoro-onnx | latest | ✅ CUDA |
| f5-tts | latest | ✅ CUDA |
| chatterbox-tts | 0.1.6 | ✅ CUDA |
| Vocos vocoder | local (no HuggingFace) | ✅ |
| Whisper v3-turbo | local (no HuggingFace) | ✅ |

All three backends load cleanly. No HuggingFace network calls at startup.
Startup time approximately 15-20 seconds with all three backends.

### CUDA Performance Benchmarks

Tested with 210-character input (~13 seconds of output audio):

| Backend | Audio Duration | Synth Time | Ratio |
|---|---|---|---|
| Kokoro | 13.14s | 3.42s | 3.8x realtime |
| F5-TTS | 12.72s | 5.52s | 2.3x realtime |
| Chatterbox Turbo | 12.60s | 4.06s | 3.1x realtime |

All three comfortably faster than realtime. Cache hit on second request
is sub-millisecond regardless of backend.
