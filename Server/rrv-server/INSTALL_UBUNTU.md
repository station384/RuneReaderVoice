# RuneReader Voice Server — Ubuntu Installation Guide

Tested on: **Ubuntu 24.04.4 LTS** with **NVIDIA RTX 3080 Laptop GPU (16GB)** and **CUDA 13.0**

---

## Overview

The server uses a **host/worker architecture**. Each TTS backend runs in its own
isolated Python virtual environment to avoid dependency conflicts (notably,
Chatterbox and Qwen require different incompatible versions of `transformers`).

**Directory layout after setup:**

```
~/rrvserver/                        ← deployment root (clone / unzip here)
├── .gitignore
├── data/                           ← shared by all processes
│   ├── models/                     ← TTS model files (pre-placed manually)
│   ├── samples/                    ← reference audio clips for voice matching
│   ├── cache/                      ← generated OGG audio (per-provider subdirs)
│   │   ├── kokoro/
│   │   ├── chatterbox/
│   │   ├── chatterbox_full/
│   │   └── f5tts/
│   ├── defaults/                   ← community seed data
│   ├── community.db                ← NPC override crowd-source database
│   └── server-cache.db             ← audio cache manifest
├── rrv-server/                     ← host: FastAPI + Whisper
│   ├── .env                        ← live config (copy from .env.example, never committed)
│   ├── .env.example
│   ├── pyproject.toml
│   ├── .venv/                      ← host venv (gitignored)
│   └── server/
├── rrv-kokoro/                     ← Kokoro worker (primary backend)
│   ├── run_worker.py               ← thin launcher (do not edit)
│   └── .venv/                      ← gitignored
├── rrv-chatterbox/                 ← Chatterbox Turbo + Full worker
│   ├── run_worker.py
│   └── .venv/
├── rrv-f5/                         ← F5-TTS worker
│   ├── run_worker.py
│   └── .venv/
└── rrv-qwen/                       ← Qwen worker (optional, not yet enabled)
    ├── run_worker.py
    └── .venv/
```

Each worker directory has exactly one `run_worker.py` thin launcher and one `.venv/`.
The host spawns workers automatically at startup based on which `RRV_WORKER_VENV_*`
entries are configured in `rrv-server/.env`.

Cache files are stored in per-provider subdirectories under `data/cache/`. To clear
a single provider's cache without affecting others:

```bash
rm -rf ~/rrvserver/data/cache/kokoro/
```

---

## Prerequisites

### System Info

```
OS:     Ubuntu 24.04.4 LTS (Noble)
GPU:    NVIDIA GeForce RTX 3080 Laptop GPU (16GB VRAM)
Driver: 580.105.08
CUDA:   13.0
Python: 3.12.3 (system default — do NOT use for any rrv venv)
```

### Notes

- Ubuntu 24.04 ships with Python 3.12 as the system default.
  **All rrv venvs require Python 3.11** — Chatterbox fails to install on 3.12
  due to `distutils` removal. Install 3.11 alongside the system Python without
  replacing it.
- PyTorch does not yet support CUDA 13.0 directly. Use
  `--index-url https://download.pytorch.org/whl/cu124` — the cu124 wheels
  work correctly with CUDA 13.0 drivers via backward compatibility.
- GPU detection in the host uses `nvidia-smi` (no torch needed in host venv).
  Each worker detects GPU independently inside its own venv.
- If the GPU is shared with other workloads (e.g. llama-server), monitor VRAM
  before loading all backends. F5-TTS needs ~3–4 GB, Chatterbox Turbo ~1–2 GB,
  Kokoro negligible.

---

## Step 1 — System Dependencies

```bash
# Python 3.11 — Ubuntu 24.04 ships with 3.12; all rrv venvs need 3.11
sudo add-apt-repository ppa:deadsnakes/ppa -y
sudo apt update
sudo apt install python3.11 python3.11-venv python3.11-dev -y
python3.11 --version
# Expected: Python 3.11.x

# ffmpeg — required for automatic audio/video to WAV conversion in sample pipeline
sudo apt install ffmpeg -y
ffmpeg -version | head -1
# Confirmed working: ffmpeg version 6.1.1-3ubuntu5
```

---

## Step 2 — Extract / Clone Source

```bash
cd ~
# Option A: extract zip
unzip rrvserver.zip -d rrvserver

# Option B: git clone (when repo is available)
# git clone <repo-url> rrvserver

cd ~/rrvserver
ls
# Expected: data/  rrv-chatterbox/  rrv-f5/  rrv-kokoro/  rrv-qwen/  rrv-server/
```

---

## Step 3 — Host Venv (rrv-server)

The host venv runs FastAPI, Uvicorn, and Whisper. TTS inference runs in worker
venvs, but the host needs CUDA-enabled PyTorch to run Whisper on the GPU.

```bash
cd ~/rrvserver/rrv-server
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x

# Core host dependencies (FastAPI, Uvicorn, aiosqlite, soundfile, etc.)
pip install -e "."

# PyTorch with CUDA — required for Whisper GPU transcription of reference samples
# cu124 wheels work with CUDA 13.0 drivers (backward compatible)
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

# Verify CUDA available to host
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
# Expected: True  NVIDIA GeForce RTX 3080 Laptop GPU

deactivate
```

---

## Step 4 — Kokoro Worker Venv (rrv-kokoro)

Kokoro is the primary backend. ONNX only — no PyTorch needed.

```bash
cd ~/rrvserver/rrv-kokoro
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x

pip install kokoro-onnx onnxruntime soundfile
# For CUDA-accelerated ONNX inference use onnxruntime-gpu instead:
# pip install kokoro-onnx onnxruntime-gpu soundfile

# Verify
python -c "from kokoro_onnx import Kokoro; print('kokoro ok')"

deactivate
```

Note: `soundfile` is not pulled in as a transitive dependency of `kokoro-onnx`
and must be installed explicitly.

---

## Step 5 — Chatterbox Worker Venv (rrv-chatterbox)

Shared by `chatterbox` (Turbo), `chatterbox_full`, and `chatterbox_multilingual`.
Requires `transformers==4.46.3` — do not upgrade.

```bash
cd ~/rrvserver/rrv-chatterbox
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x

# PyTorch with CUDA must be installed before chatterbox-tts
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

# Verify CUDA
python -c "import torch; print(torch.cuda.is_available(), torch.cuda.get_device_name(0))"
# Expected: True  NVIDIA GeForce RTX 3080 Laptop GPU

# Chatterbox — s3tokenizer is now pulled in automatically as a dependency
pip install chatterbox-tts
pip install "onnx>=1.16.0"

# Verify all three Chatterbox backends
python -c "
from chatterbox.tts_turbo import ChatterboxTurboTTS; print('chatterbox turbo ok')
from chatterbox.tts import ChatterboxTTS;            print('chatterbox full ok')
from chatterbox.mtl_tts import ChatterboxMultilingualTTS; print('chatterbox multilingual ok')
"

deactivate
```

Note: `soundfile` is pulled in automatically by `chatterbox-tts`. No separate
install needed.

---

## Step 6 — F5-TTS Worker Venv (rrv-f5)

```bash
cd ~/rrvserver/rrv-f5
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x

# PyTorch with CUDA first
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

pip install f5-tts

# Verify
python -c "from f5_tts.api import F5TTS; print('f5tts ok')"

deactivate
```

Note: `soundfile` is pulled in automatically by `f5-tts`. No separate install
needed.

---

## Step 7 — Place Model Files

The server **never downloads models at runtime**. All model files must be
pre-placed manually in `~/rrvserver/data/models/` before the corresponding
backend is started.

```
data/models/
  kokoro-v1.0.onnx
  voices-v1.0.bin
  f5tts/
    F5TTS_v1_Base/
      model_1250000.safetensors
  chatterbox/
    (all files from HuggingFace ResembleAI/chatterbox-turbo)
  chatterbox-hf/
    (all files from HuggingFace ResembleAI/chatterbox-hf)
  chatterbox-ml/
    (all files from HuggingFace ResembleAI/chatterbox-multilingual)
  whisper/
    v3-turbo/   (all files from HuggingFace openai/whisper-large-v3-turbo)
    tiny/       (all files from HuggingFace openai/whisper-tiny)
```

See `data/models/MODELS.txt` for exact file lists and HuggingFace source URLs.

### F5-TTS Vocos vocoder

F5-TTS requires the Vocos vocoder. On first run with internet access it downloads
automatically into `~/.cache/huggingface/`. For air-gapped deployments:

```bash
mkdir -p ~/rrvserver/data/models/f5tts/vocos
VOCOS=$(find ~/.cache/huggingface/hub -path "*/vocos-mel-24khz/snapshots/*" \
        -name "config.yaml" | head -1 | xargs dirname)
cp $VOCOS/config.yaml $VOCOS/pytorch_model.bin \
   ~/rrvserver/data/models/f5tts/vocos/
```

Download from: https://huggingface.co/charactr/vocos-mel-24khz

---

## Step 8 — Configure

```bash
cd ~/rrvserver/rrv-server
cp .env.example .env
```

Edit `.env`. Minimum required settings:

```bash
# Which backends to load
RRV_BACKENDS=kokoro,chatterbox,chatterbox_full,f5tts

# Shared data directory — paths relative to working directory at startup (rrv-server/)
RRV_MODELS_DIR=../data/models
RRV_SAMPLES_DIR=../data/samples
RRV_CACHE_DIR=../data/cache
RRV_DB_PATH=../data/server-cache.db
RRV_COMMUNITY_DB_PATH=../data/community.db
RRV_DEFAULTS_DIR=../data/defaults
RRV_WHISPER_MODEL_DIR=../data/models/whisper/v3-turbo

# Worker venv paths — one entry per backend listed in RRV_BACKENDS
RRV_WORKER_VENV_kokoro=../rrv-kokoro/.venv
RRV_WORKER_VENV_chatterbox=../rrv-chatterbox/.venv
RRV_WORKER_VENV_chatterbox_full=../rrv-chatterbox/.venv
RRV_WORKER_VENV_f5tts=../rrv-f5/.venv

RRV_GPU=auto
```

**Important:** All paths are resolved relative to the **working directory when
the server starts**, not relative to `.env`. Always start the server from
`~/rrvserver/rrv-server/` so that `../data/` and `../rrv-kokoro/.venv` resolve
correctly. The systemd service below sets `WorkingDirectory` for this.

---

## Step 9 — Start the Server

```bash
cd ~/rrvserver/rrv-server
source .venv/bin/activate
rrv-server
```

Expected startup log:

```
INFO  GPU: CUDA selected — NVIDIA GeForce RTX 3080 Laptop GPU
INFO  Backend 'kokoro' configured as worker subprocess (venv: ../rrv-kokoro/.venv)
INFO  Spawning worker 'kokoro' — launcher: ../rrv-kokoro/run_worker.py
INFO  Worker 'kokoro' ready — capabilities={... 'execution_provider': 'cuda' ...}
INFO  Worker 'chatterbox' ready — capabilities={... 'execution_provider': 'cuda' ...}
INFO  Worker 'chatterbox_full' ready — capabilities={... 'execution_provider': 'cuda' ...}
INFO  Worker 'f5tts' ready — capabilities={... 'execution_provider': 'cuda' ...}
INFO  Loaded 4 backend(s): chatterbox, chatterbox_full, f5tts, kokoro
INFO  Whisper model found at ../data/models/whisper/v3-turbo — auto-transcription enabled
INFO  RuneReader Voice Server ready — 0.0.0.0:8765 | backends: chatterbox, chatterbox_full, f5tts, kokoro | gpu: cuda
```

---

## Step 10 — Verify

```bash
# Health check
curl http://localhost:8765/api/v1/health

# Loaded backends and capabilities
curl http://localhost:8765/api/v1/providers | python3 -m json.tool

# Quick synthesis test (Kokoro — no sample file needed)
curl -X POST http://localhost:8765/api/v1/synthesize \
  -H "Content-Type: application/json" \
  -d '{
    "provider_id": "kokoro",
    "text": "Strangers from distant lands, friends of old.",
    "voice": {"type": "base", "voice_id": "am_michael"},
    "lang_code": "en-us",
    "speech_rate": 1.0
  }' \
  --output test_kokoro.ogg
ls -lh test_kokoro.ogg
# Expected: non-zero .ogg file
```

---

## systemd Service (optional)

```bash
sudo nano /etc/systemd/system/rrv-server.service
```

```ini
[Unit]
Description=RuneReader Voice Server
After=network.target

[Service]
Type=simple
User=mike
WorkingDirectory=/home/mike/rrvserver/rrv-server
ExecStart=/home/mike/rrvserver/rrv-server/.venv/bin/rrv-server
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable rrv-server
sudo systemctl start rrv-server
sudo journalctl -u rrv-server -f
```

`WorkingDirectory` must be `rrv-server/` so relative paths in `.env` resolve
correctly.

---

## Known Issues / Notes

### Chatterbox — LoRACompatibleLinear FutureWarning

On startup the Chatterbox worker logs:

```
FutureWarning: LoRACompatibleLinear is deprecated and will be removed in version 1.0.0.
```

This comes from the `diffusers` package inside the chatterbox venv. It is harmless
and does not affect synthesis quality or performance. No action required.

### Chatterbox Turbo — exaggeration behaviour

`exaggeration` is applied during `prepare_conditionals()` in Turbo, not at
generation time. Passing it to `generate()` logs a warning and is silently
ignored. The server handles this correctly — no action required.

### Chatterbox Multilingual — short input instability

The multilingual model has end-of-clip distortion on short sequences and
hallucinates frequently on short inputs. Not enabled in `RRV_BACKENDS` by
default. Enable only for longer dialog segments where needed.

### VRAM contention

Each worker process holds its own VRAM allocation independently. If other CUDA
workloads are running on the same GPU, monitor free VRAM before loading all
backends. F5-TTS needs ~3–4 GB, Chatterbox Turbo ~1–2 GB, Kokoro negligible.

### transformers version isolation

Chatterbox requires `transformers==4.46.3`. Qwen requires `transformers==4.57.3`.
These cannot coexist in one venv — the worker architecture keeps them isolated.
Never attempt to merge `rrv-chatterbox` and `rrv-qwen` into a single venv.

### Stale rrv-server/data/ directory

If the server was started before `RRV_COMMUNITY_DB_PATH` and `RRV_DEFAULTS_DIR`
were set in `.env`, a stale `rrv-server/data/` directory may have been created.
Once both env vars are correctly pointing to `../data/`, delete it:

```bash
rm -rf ~/rrvserver/rrv-server/data
```

### Adding a new backend

Create a new sibling directory, e.g. `~/rrvserver/rrv-newprovider/`. Copy
`run_worker.py` from any existing worker dir into it. Set up a `.venv/` with
the new provider's dependencies. Add
`RRV_WORKER_VENV_newprovider=../rrv-newprovider/.venv` to `rrv-server/.env`,
add the backend name to `RRV_BACKENDS`, and restart.

---

## Confirmed Working Configuration

Tested on **Ubuntu 24.04.4 LTS / RTX 3080 Laptop GPU (16GB)**:

| Component | Version | Status |
|---|---|---|
| Python | 3.11.x (via deadsnakes) | ✅ |
| PyTorch (host) | 2.6.0+cu124 | ✅ CUDA (Whisper) |
| PyTorch (workers) | 2.6.0+cu124 | ✅ CUDA (inference) |
| numpy | 2.4.x | ✅ |
| kokoro-onnx | 0.5.0+ | ✅ CUDA |
| f5-tts | latest | ✅ CUDA |
| chatterbox-tts | 0.1.7+ | ✅ CUDA |
| soundfile | 0.13.1 | ✅ all venvs |
| Vocos vocoder | local (no HuggingFace at runtime) | ✅ |
| Whisper v3-turbo | local (no HuggingFace at runtime) | ✅ |

All four backends load as isolated worker subprocesses on CUDA.
Cache files correctly stored in per-provider subdirectories under `data/cache/`.
Full synthesis round-trip confirmed for all providers.

### CUDA Performance Benchmarks

Tested with 210-character input (~13 seconds of output audio):

| Backend | Audio Duration | Synth Time | Ratio |
|---|---|---|---|
| Kokoro | 13.14s | 3.42s | 3.8× realtime |
| F5-TTS | 12.72s | 5.52s | 2.3× realtime |
| Chatterbox Turbo | 12.60s | 4.06s | 3.1× realtime |

All three comfortably faster than realtime on CUDA.
Cache hit on second request is sub-millisecond regardless of backend.
