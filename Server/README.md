# RuneReader Voice Server (`rrv-server`)

> Local AI TTS synthesis server for the RuneReader Voice ecosystem — GPU-accelerated NPC voice generation for World of Warcraft.

`rrv-server` is the optional Phase 5 backend for the [RuneReader Voice](https://github.com/station384/RuneReaderAddonVoice) system. It exposes a FastAPI HTTP service that the RuneReader Voice C# desktop companion calls instead of synthesizing locally, enabling higher-quality GPU-accelerated voice synthesis, multi-LAN-client support, and a shared render cache that makes repeated lines instant regardless of which client first requested them.

**Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming**  
Licensed under the [GNU General Public License v3.0 or later](LICENSE).

---

## Overview

The server hosts one or more TTS backends behind a unified REST API. The desktop client sends a synthesis request — text, provider, voice identity — and gets back an OGG audio file. Synthesized files are cached on disk via an LRU manifest; identical requests return the cached OGG in sub-millisecond time.

```
RuneReader Voice Client (Windows/Linux)
        │
        │  POST /api/v1/synthesize
        │  { provider_id, text, voice, ... }
        ▼
  ┌─────────────────────────────────────────────┐
  │           rrv-server (FastAPI)              │
  │                                             │
  │  ┌──────────┐  ┌──────────┐  ┌───────────┐ │
  │  │  Kokoro  │  │  F5-TTS  │  │Chatterbox │ │
  │  │  (ONNX)  │  │          │  │  Turbo    │ │
  │  └──────────┘  └──────────┘  └───────────┘ │
  │                                             │
  │         LRU OGG Disk Cache                  │
  └─────────────────────────────────────────────┘
        │
        │  Response: audio/ogg
        ▼
  Audio playback in WoW
```

---

## Backends

| Backend | Voice Matching | GPU | Quality | Notes |
|---|---|---|---|---|
| **Kokoro** | No (preset voices) | Optional (ONNX) | Good | Fastest; 54 built-in voices; CPU viable |
| **F5-TTS** | Yes (reference audio) | Recommended | Excellent | Voice-clones from a short reference clip |
| **Chatterbox Turbo** | Yes (reference audio) | Recommended | Excellent | Expressive; includes Resemble AI PerTh watermark |
| **Chatterbox Full** | Yes (reference audio) | Recommended | Excellent | Full Chatterbox model; tighter chunking limits |

> The server **never downloads models from HuggingFace at runtime.** All model files must be pre-placed before the server starts.

---

## Requirements

- **Python 3.11** — Chatterbox Turbo fails on 3.12 due to `distutils` removal. Ubuntu 24.04 ships Python 3.12 as the system default; install 3.11 alongside it (see Installation).
- **ffmpeg** — required for automatic reference audio conversion (WAV/MP3/FLAC/OGG → normalized WAV)
- **4 GB RAM** minimum; **8 GB** recommended when loading F5-TTS or Chatterbox
- GPU: NVIDIA CUDA 12.x / AMD ROCm 6.x recommended; CPU fallback available (slow for F5/Chatterbox)
- Developed and tested on **Linux**. Windows is untested.

---

## Installation

### Quick Start

```bash
# 1. Create Python 3.11 virtual environment
python3.11 -m venv .venv
source .venv/bin/activate

# 2. Install core + Kokoro backend (always required)
pip install -e ".[kokoro]"

# 3. Install GPU PyTorch (choose one — must be done before F5/Chatterbox)

# NVIDIA CUDA (cu124 works with CUDA 12.x and 13.x drivers)
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

# AMD ROCm
pip install torch torchaudio --index-url https://download.pytorch.org/whl/rocm6.1

# CPU only (skip this step for CPU-only Kokoro)
# pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu

# 4. (Optional) F5-TTS backend
pip install f5-tts

# 5. (Optional) Chatterbox Turbo — must use --no-deps to avoid numpy conflict
pip install chatterbox-tts --no-deps
pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
            s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps
```

### Ubuntu 24.04 Full Installation

Ubuntu 24.04 ships with Python 3.12 as the system default. Install 3.11 alongside it without replacing it.

```bash
# Install Python 3.11 from deadsnakes PPA
sudo add-apt-repository ppa:deadsnakes/ppa -y
sudo apt update
sudo apt install python3.11 python3.11-venv python3.11-dev -y
python3.11 --version
# Expected: Python 3.11.x

# Install ffmpeg
sudo apt install ffmpeg -y

# Create the virtual environment — always use python3.11 explicitly
mkdir -p ~/rrv-server
cd ~/rrv-server
python3.11 -m venv .venv
source .venv/bin/activate
python --version
# Must show: Python 3.11.x

# Install PyTorch CUDA (cu124 wheels are backward-compatible with CUDA 13.0)
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124

# Verify CUDA
python -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0))"
# Expected: True, <your GPU name>

# Install core + Kokoro
pip install -e ".[kokoro]"

# Install F5-TTS
pip install f5-tts

# Install Chatterbox Turbo
pip install chatterbox-tts --no-deps
pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
            s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps

# Verify all backends
python -c "
from kokoro_onnx import Kokoro; print('kokoro ok')
from f5_tts.api import F5TTS; print('f5tts ok')
from chatterbox.tts_turbo import ChatterboxTurboTTS; print('chatterbox ok')
"
```

See [`INSTALL_UBUNTU.md`](INSTALL_UBUNTU.md) for the full step-by-step walkthrough with verified output for each step.

---

## Model Files

Place all model files under `data/models/` before starting the server. The server will refuse to start a backend whose required model files are missing.

```
data/models/
│
├── kokoro-v1.0.onnx                (~310 MB)
├── voices-v1.0.bin                 (~10 MB)
│   └── Download: https://github.com/thewh1teagle/kokoro-onnx/releases
│
├── f5tts/
│   └── F5TTS_v1_Base/
│       └── model_1250000.safetensors   (~1.2 GB)
│           └── Download: https://huggingface.co/SWivid/F5-TTS
│               (ckpts/F5TTS_v1_Base/model_1250000.safetensors)
│
├── chatterbox/
│   └── (all files from HuggingFace ResembleAI/chatterbox-turbo)
│       Download: https://huggingface.co/ResembleAI/chatterbox-turbo
│
└── whisper/
    ├── v3-turbo/    (~1.6 GB)  ← recommended for accuracy
    │   Download: https://huggingface.co/openai/whisper-large-v3-turbo
    └── tiny/        (~150 MB)  ← faster, lower accuracy
        Download: https://huggingface.co/openai/whisper-tiny
```

> See [`data/models/MODELS.txt`](data/models/MODELS.txt) for exact file lists and air-gapped pre-download instructions.

**Kokoro model files are shared** with the RuneReader Voice C# desktop client. If both run on the same machine, point `RRV_MODELS_DIR` at the same directory.

---

## Reference Samples (Voice Matching)

F5-TTS and Chatterbox Turbo synthesize speech by cloning the voice character from a short reference audio clip. Place reference clips in `data/samples/`.

### Naming Convention

```
<race>-<gender>-<role>[-variant].<ext>
```

Examples:
```
orc-male-warrior.wav
human-female-mage.wav
undead-male-vendor-raspy.wav
troll-female-shaman-slow.wav
```

Supported input formats: `.wav`, `.mp3`, `.flac`, `.ogg` — ffmpeg converts them all to a normalized master WAV automatically.

### Transcript Sidecars

Each clip needs a `.ref.txt` sidecar containing the transcript of what is spoken in the clip. If Whisper is configured via `RRV_WHISPER_MODEL_DIR`, the server auto-generates missing sidecars whenever it detects a new file in the samples directory. The background watcher polls at the interval set by `RRV_SAMPLE_SCAN_INTERVAL`.

```
data/samples/
  orc-male-warrior.wav
  orc-male-warrior.ref.txt       ← auto-generated by Whisper, or provide manually
```

> See [`data/samples/SAMPLES.txt`](data/samples/SAMPLES.txt) for full naming conventions and sidecar format details.

---

## Configuration

Copy `.env.example` to `.env` and edit as needed. Environment variables are loaded at startup; CLI flags take precedence over `.env` values.

```bash
cp .env.example .env
```

### Full Environment Variable Reference

| Variable | Default | Description |
|---|---|---|
| `RRV_HOST` | `0.0.0.0` | Bind address. Use `0.0.0.0` to accept LAN connections. |
| `RRV_PORT` | `8765` | Listening port. |
| `RRV_BACKENDS` | `kokoro` | Comma-separated backends to load: `kokoro`, `f5tts`, `chatterbox`, `chatterbox_full`. |
| `RRV_GPU` | `auto` | GPU provider: `auto` (CUDA → ROCm → CPU), `cuda`, `rocm`, `cpu`. |
| `RRV_MODELS_DIR` | `./data/models` | Directory containing TTS model files. |
| `RRV_SAMPLES_DIR` | `./data/samples` | Directory containing reference audio clips. |
| `RRV_WHISPER_MODEL_DIR` | `./data/models/whisper` | Whisper model directory for auto-transcription. Only active when a voice-matching backend is loaded. |
| `RRV_SAMPLE_SCAN_INTERVAL` | `30` | Seconds between new-sample scans. |
| `RRV_F5_SAMPLE_RATE` | `22050` | Output sample rate for F5-TTS generated clips (Hz). |
| `RRV_F5_SAMPLE_CHANNELS` | `1` | Output channels for F5-TTS generated clips (1=mono, 2=stereo). |
| `RRV_CHATTERBOX_SAMPLE_RATE` | `44100` | Output sample rate for Chatterbox generated clips (Hz). |
| `RRV_CHATTERBOX_SAMPLE_CHANNELS` | `2` | Output channels for Chatterbox generated clips (1=mono, 2=stereo). |
| `RRV_F5_VOCODER` | `auto` | F5-TTS vocoder: `auto` (BigVGAN if staged, else Vocos), `bigvgan`, `vocos`. |
| `RRV_CACHE_DIR` | `./data/cache` | Directory for generated OGG cache files. |
| `RRV_DB_PATH` | `./data/server-cache.db` | SQLite cache manifest database path. |
| `RRV_CACHE_MAX_MB` | `2048` | Maximum LRU cache size in MB. Least-recently-used files are evicted when exceeded. |
| `RRV_API_KEY` | *(empty)* | Bearer token for API authentication. Empty = auth disabled (LAN mode). |
| `RRV_TRUSTED_PROXY_IPS` | `127.0.0.1` | Trusted proxy IPs/CIDRs for `X-Forwarded-For`. `0.0.0.0/0` = trust all (private LAN only). |
| `RRV_CONTRIBUTE_KEY` | *(empty)* | Shared key for crowd-source NPC override contributions. Empty = open. |
| `RRV_ADMIN_KEY` | *(empty)* | Admin key for confirming NPC overrides and updating defaults. Empty = open. |
| `RRV_LOG_LEVEL` | `info` | Log verbosity: `debug`, `info`, `warning`, `error`. |

---

## Starting the Server

```bash
source .venv/bin/activate

# Start with defaults from .env
rrv-server

# Override specific settings from the CLI
rrv-server --port 8765 --backends kokoro,f5tts,chatterbox --gpu cuda

# Full CLI reference
rrv-server --help
```

### CLI Flags

| Flag | Equivalent Env Var |
|---|---|
| `--host` | `RRV_HOST` |
| `--port` | `RRV_PORT` |
| `--backends` | `RRV_BACKENDS` |
| `--gpu` | `RRV_GPU` |
| `--models-dir` | `RRV_MODELS_DIR` |
| `--samples-dir` | `RRV_SAMPLES_DIR` |
| `--whisper-model-dir` | `RRV_WHISPER_MODEL_DIR` |
| `--sample-scan-interval` | `RRV_SAMPLE_SCAN_INTERVAL` |
| `--cache-dir` | `RRV_CACHE_DIR` |
| `--db-path` | `RRV_DB_PATH` |
| `--cache-max-mb` | `RRV_CACHE_MAX_MB` |
| `--api-key` | `RRV_API_KEY` |
| `--log-level` | `RRV_LOG_LEVEL` |

### Expected Startup Log

A healthy startup shows GPU selection, all requested backends registered, and (if a voice-matching backend is loaded) the sample watcher started:

```
2026-04-01 12:00:00 INFO     server.gpu_detect — GPU: CUDA selected (RTX 3080)
2026-04-01 12:00:03 INFO     server.backends.kokoro_backend — Kokoro loaded
2026-04-01 12:00:09 INFO     server.backends.f5tts_backend — F5-TTS loaded (vocos)
2026-04-01 12:00:13 INFO     server.backends.chatterbox_backend — Chatterbox Turbo loaded
2026-04-01 12:00:13 INFO     server.main — Sample watcher started — polling every 30s
2026-04-01 12:00:13 INFO     server.main — RuneReader Voice Server ready — 0.0.0.0:8765 | backends: chatterbox, f5tts, kokoro | gpu: CUDA
```

### API Explorer

With the server running, the interactive Swagger UI is available at:

```
http://<host>:8765/docs
```

---

## Running as a systemd Service

```ini
# /etc/systemd/system/rrv-server.service
[Unit]
Description=RuneReader Voice Server
After=network.target

[Service]
Type=simple
User=<your-username>
WorkingDirectory=/home/<your-username>/rrv-server
ExecStart=/home/<your-username>/rrv-server/.venv/bin/rrv-server
Restart=on-failure
RestartSec=5
EnvironmentFile=/home/<your-username>/rrv-server/.env

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable rrv-server
sudo systemctl start rrv-server

# Follow logs
sudo journalctl -u rrv-server -f
```

---

## Docker

A `Dockerfile` and `docker-compose.yml` are included. Mount `data/models` and `data/samples` as read-only volumes so model files survive container rebuilds. Mount `data/cache` and `data/` read-write for the OGG cache and SQLite manifest.

```bash
# Kokoro CPU only (default)
docker build -t rrv-server .

# All backends, NVIDIA CUDA
docker build \
  --build-arg BACKENDS=kokoro,f5tts,chatterbox \
  --build-arg GPU=gpu-cuda \
  -t rrv-server:cuda .

# All backends, AMD ROCm
docker build \
  --build-arg BACKENDS=kokoro,f5tts,chatterbox \
  --build-arg GPU=gpu-rocm \
  -t rrv-server:rocm .

# Run
docker-compose up -d
```

Edit `docker-compose.yml` to mount your model and sample directories before running.

---

## API Reference

All endpoints are prefixed with `/api/v1/`.

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/v1/health` | Health check — always returns 200, no auth required |
| `GET` | `/api/v1/capabilities` | Lists loaded backends and their capabilities |
| `GET` | `/api/v1/providers` | Lists all providers with voice lists |
| `GET` | `/api/v1/providers/{id}/samples` | Lists available reference samples for a provider |
| `POST` | `/api/v1/synthesize` | Synthesize text — returns `audio/ogg` |
| `POST` | `/api/v1/synthesize/v2` | Async synthesis — returns a progress key immediately |
| `GET` | `/api/v1/synthesize/v2/{key}/progress` | SSE stream of synthesis progress events |
| `GET` | `/api/v1/defaults` | Pull server-side voice/NPC default seed data |
| `GET` | `/api/v1/npc-overrides` | List community NPC voice override records |
| `POST` | `/api/v1/npc-overrides` | Submit a crowd-sourced NPC voice override |

Full request/response schemas are documented in the Swagger UI at `/docs`.

---

## Performance Reference

Tested on Ubuntu 24.04.4 LTS / NVIDIA RTX 3080 Laptop GPU (16 GB), 210-character input (~13 seconds of audio output):

| Backend | Audio Duration | Synthesis Time | Realtime Ratio |
|---|---|---|---|
| Kokoro | 13.14 s | 3.42 s | **3.8×** |
| F5-TTS | 12.72 s | 5.52 s | **2.3×** |
| Chatterbox Turbo | 12.60 s | 4.06 s | **3.1×** |

Cache hit on any repeated line: **sub-millisecond**.

### VRAM Usage

| Backend | Approximate VRAM |
|---|---|
| Kokoro | Negligible (ONNX CPU or ORT-GPU) |
| F5-TTS | ~3–4 GB |
| Chatterbox Turbo | ~1–2 GB |

If running alongside other GPU workloads (e.g. a local LLM), monitor VRAM and load only the backends you need via `RRV_BACKENDS`.

---

## Known Issues

### Chatterbox Turbo on CPU

Chatterbox Turbo was never tested on CPU by Resemble AI and has latent float64/float32 dtype mismatches throughout the model. The server applies monkey-patches at load time: `librosa.load` is patched to always return float32, `S3Tokenizer.log_mel_spectrogram` casts magnitudes before matmul, and `VoiceEncoder.embeds_from_wavs` casts input wavs to float32. These patches are GPU-safe and are only applied when Chatterbox loads. Expect 30–120 seconds per synthesis on CPU.

### F5-TTS Vocos Vocoder

F5-TTS requires the Vocos vocoder (`charactr/vocos-mel-24khz`). On first run with internet access it downloads automatically to `~/.cache/huggingface/`. For air-gapped deployments, pre-stage it:

```bash
mkdir -p data/models/f5tts/vocos
VOCOS=$(find ~/.cache/huggingface/hub -path "*/vocos-mel-24khz/snapshots/*" -name "config.yaml" | head -1 | xargs dirname)
cp $VOCOS/config.yaml $VOCOS/pytorch_model.bin data/models/f5tts/vocos/
```

Download from: https://huggingface.co/charactr/vocos-mel-24khz

### Intel Arc (Linux)

Intel Arc on Linux uses CPU for PyTorch-based models (F5-TTS, Chatterbox Turbo) — IPEX support is experimental and not relied upon. Kokoro via ONNX Runtime uses ORT-CPU on Arc, which is fast enough given Kokoro's model size.

### Chatterbox PerTh Watermark

Chatterbox audio output includes an imperceptible PerTh watermark embedded by Resemble AI. This is a feature of the MIT-licensed model and cannot be disabled. It does not affect audio quality or playback in any way.

---

## Confirmed Working Configuration

Tested and verified on **Ubuntu 24.04.4 LTS / RTX 3080 Laptop GPU (16 GB)**:

| Component | Version | Status |
|---|---|---|
| Python | 3.11.x (deadsnakes) | ✅ |
| PyTorch | 2.6.0+cu124 | ✅ CUDA |
| numpy | 2.4.3 | ✅ |
| kokoro-onnx | latest | ✅ |
| f5-tts | latest | ✅ CUDA |
| chatterbox-tts | 0.1.6 | ✅ CUDA |
| Vocos vocoder | local (no HuggingFace) | ✅ |
| Whisper v3-turbo | local (no HuggingFace) | ✅ |

All three backends load cleanly. No HuggingFace network calls at startup. Startup time approximately 15–20 seconds with all three backends.

---

## License

RuneReader Voice Server is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License v3.0 or later (GPL-3.0-or-later)**, as published by the Free Software Foundation.

See [LICENSE](LICENSE) and [COPYING](COPYING) for the full license text.

### A Note on License Choice

The server uses **GPL v3** (the same license as the addon and desktop client) rather than AGPL v3. AGPL adds a network-use clause that would require you to publish source code if you run a modified version as a public service. Since `rrv-server` is intended for **personal LAN use** — running on your own machine for your own WoW session — AGPL's network clause would be an unnecessary burden with no practical benefit. GPL v3 provides the same copyleft protections for any distributed binaries or source modifications while remaining appropriate for a private self-hosted service.

### Third-Party Models

The TTS models used by this server have their own licenses:
- **Kokoro-82M** — Apache 2.0
- **F5-TTS** — MIT
- **Chatterbox Turbo** — MIT (includes Resemble AI PerTh watermark)
- **Whisper** — MIT

Model files are not distributed with this source code and must be obtained separately.

---

*Tanstaafl Gaming — There Ain't No Such Thing As A Free Lunch.*
