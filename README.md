# RuneReader Voice

> *Bring Azeroth to life — AI-generated voice acting for every NPC and quest dialog in World of Warcraft.*

RuneReader Voice (RRV) is a three-part system that hooks into WoW's dialog and quest text events, captures NPC speech in real time, and synthesizes it using local AI text-to-speech — giving every character in the game a unique, generated voice with no cloud dependency and no subscription required.

Part of the **Tanstaafl Gaming** RuneReader ecosystem.  
*There Ain't No Such Thing As A Free Lunch.*

---

## How It Works

```
WoW Client                  Desktop (Windows/Linux)             TTS Server (Linux)
──────────────              ───────────────────────             ──────────────────
NPC dialog fires   ──────►  RuneReader Voice C# app  ────────►  rrv-server (FastAPI)
Lua addon encodes           reads QR via screen capture          Kokoro / F5-TTS /
dialog as QR code           decodes payload, selects voice       Chatterbox Turbo
displayed on screen         sends synthesis request              synthesizes audio
                            plays back in sequence      ◄──────  returns OGG
```

1. The **WoW Lua addon** hooks into gossip, quest, and item-text events. It segments multi-part dialog, attaches NPC race/gender/session metadata, encodes the payload as a Base45 QR code, and renders it on-screen.
2. The **C# desktop companion** (AvaloniaUI, Windows and Linux) watches the screen for QR codes via OpenCV, decodes the payload, resolves the NPC's voice profile, applies pronunciation and text-swap rules, and requests synthesis from the server.
3. The **Python TTS server** (`rrv-server`) is a FastAPI service that hosts one or more synthesis backends — Kokoro (ONNX, fast), F5-TTS (voice-matched), and Chatterbox Turbo (expressive, voice-matched) — with an LRU disk cache so repeated lines play instantly.

---

## Repository Structure

```
RuneReaderVoice/
├── LUAAddon/               ← Git submodule → RuneReaderAddonVoice repo
│   ├── core.lua            ← Event hooks, dialog dispatch
│   ├── payload.lua         ← Dialog segmentation and encoding
│   ├── frames_qr.lua       ← On-screen QR frame rendering
│   ├── config.lua          ← SavedVariables schema
│   ├── config_panel.lua    ← In-game options UI
│   ├── base45.lua          ← Base45 encoder
│   ├── QRcodeEncoder.lua   ← QR code generator
│   └── RuneReaderVoice.toc ← Addon manifest
│
├── RuneReaderVoice/        ← C# AvaloniaUI desktop companion
│   ├── TTS/                ← Providers, cache, DSP, playback coordinator
│   ├── Session/            ← Barcode monitor, session assembler
│   ├── UI/Views/           ← MainWindow, voice profile editor, workbenches
│   ├── Platform/           ← Windows (WASAPI/DX11) and Linux (GStreamer/Wayland)
│   └── config/             ← Voice profiles, pronunciation rules, text-swap rules
│
└── Server/                 ← Python FastAPI TTS server
    ├── server/
    │   ├── backends/       ← kokoro_backend, f5tts_backend, chatterbox_backend
    │   ├── routes/         ← /synthesize, /capabilities, /providers, /health
    │   ├── main.py         ← Entry point (rrv-server)
    │   ├── cache.py        ← LRU disk cache with SQLite manifest
    │   ├── voice_profiler.py
    │   └── transcriber.py  ← Whisper auto-transcription for reference samples
    ├── data/
    │   ├── models/         ← Place model files here (not in git — see below)
    │   └── samples/        ← Reference audio clips for F5-TTS / Chatterbox
    ├── SETUP.md            ← Quick-start setup guide
    ├── INSTALL_UBUNTU.md   ← Detailed Ubuntu 24.04 installation walkthrough
    └── .env.example        ← All configuration options with documentation
```

---

## TTS Backends

| Backend | Quality | Speed (RTX 3080) | Voice Matching | GPU Required |
|---|---|---|---|---|
| **Kokoro** | Good | 3.8× realtime | No (preset voices) | No — ONNX CPU |
| **F5-TTS** | Excellent | 2.3× realtime | Yes (reference audio) | Recommended |
| **Chatterbox Turbo** | Excellent | 3.1× realtime | Yes (reference audio) | Recommended |

> All three are faster than realtime on a modern GPU. Cache hits on repeated lines are sub-millisecond regardless of backend. CPU fallback is available but expect 30–120 seconds per synthesis on F5-TTS and Chatterbox.

---

## Cloning

> ⚠️ **Git LFS required.** The `.onnx` model files are tracked via Git LFS. Install [Git LFS](https://git-lfs.github.com) before cloning or LFS-tracked files will appear as pointer stubs.

```bash
# Install Git LFS (one-time per machine)
# Windows: download installer from https://git-lfs.github.com  or  winget install GitHub.GitLFS
# Linux:   sudo apt install git-lfs  or  sudo pacman -S git-lfs
git lfs install

# Clone with submodules in one step
git clone --recurse-submodules https://github.com/station384/RuneReaderAddonVoice.git

# If you already cloned without --recurse-submodules:
git submodule update --init
```

The `LUAAddon/` directory is a Git submodule pointing to the [RuneReaderAddonVoice](https://github.com/station384/RuneReaderAddonVoice) repository. It is an independent repo so that CurseForge/WoWInterface packaging automation can operate directly on the addon source with its own push/tag/release cycle.

---

## Server Setup

The TTS server runs on Linux. It has been tested on Ubuntu 24.04 with an NVIDIA RTX 3080. Windows is untested. See [`Server/INSTALL_UBUNTU.md`](Server/INSTALL_UBUNTU.md) for a complete step-by-step walkthrough; what follows is the quick path.

### Requirements

- Python **3.11** (Chatterbox Turbo fails on 3.12 due to `distutils` removal)
- `ffmpeg` — for automatic reference audio conversion
- 4 GB RAM minimum, 8 GB recommended
- NVIDIA CUDA 12.x, AMD ROCm 6.x, or CPU (slow for F5/Chatterbox)

### Install

```bash
# 1. Create a Python 3.11 virtual environment
python3.11 -m venv .venv
source .venv/bin/activate

# 2. Install core + Kokoro backend
pip install -e ".[kokoro]"

# 3. (Optional) F5-TTS — NVIDIA CUDA
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu124
pip install f5-tts

# 3. (Optional) F5-TTS — AMD ROCm
pip install torch torchaudio --index-url https://download.pytorch.org/whl/rocm6.1
pip install f5-tts

# 4. (Optional) Chatterbox Turbo — must use --no-deps to avoid numpy conflict
pip install chatterbox-tts --no-deps
pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
            s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps
```

### Model Files

The server **never downloads from HuggingFace at runtime** — all model files must be pre-placed before starting. Put them in `Server/data/models/` using this layout:

```
data/models/
  kokoro-v1.0.onnx                          (~310 MB)  ← https://github.com/thewh1teagle/kokoro-onnx/releases
  voices-v1.0.bin                           (~10 MB)   ← same release
  f5tts/
    F5TTS_v1_Base/
      model_1250000.safetensors             (~1.2 GB)  ← https://huggingface.co/SWivid/F5-TTS
  chatterbox/                                          ← https://huggingface.co/ResembleAI/chatterbox-turbo
    (all files from HuggingFace)
  whisper/
    v3-turbo/                               (~1.6 GB)  ← https://huggingface.co/openai/whisper-large-v3-turbo
    tiny/                                   (~150 MB)  ← https://huggingface.co/openai/whisper-tiny
```

See [`Server/data/models/MODELS.txt`](Server/data/models/MODELS.txt) for exact file lists and air-gapped deployment instructions.

### Configure

```bash
cd Server
cp .env.example .env
# Edit .env — key settings:
```

```dotenv
RRV_BACKENDS=kokoro,f5tts,chatterbox   # load only the backends you need
RRV_GPU=auto                           # auto-detects CUDA → ROCm → CPU
RRV_HOST=0.0.0.0                       # accept connections from LAN clients
RRV_PORT=8765
RRV_WHISPER_MODEL_DIR=./data/models/whisper/v3-turbo
RRV_SAMPLE_SCAN_INTERVAL=30            # seconds between new-sample scans
RRV_CACHE_MAX_MB=2048                  # LRU disk cache size
RRV_API_KEY=                           # optional Bearer token auth
```

### Start

```bash
source .venv/bin/activate
rrv-server

# With overrides:
rrv-server --port 8765 --backends kokoro,f5tts,chatterbox
```

Expected startup log confirms GPU selection, all requested backends registered, and Whisper auto-transcription enabled if a voice-matching backend is active.

### Run as a systemd Service (Optional)

```ini
# /etc/systemd/system/rrv-server.service
[Unit]
Description=RuneReader Voice Server
After=network.target

[Service]
Type=simple
User=<your-user>
WorkingDirectory=/home/<your-user>/rrv-server
ExecStart=/home/<your-user>/rrv-server/.venv/bin/rrv-server
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

### Docker

A `Dockerfile` and `docker-compose.yml` are included in `Server/`. Mount `data/models` and `data/samples` as volumes so model files and the audio cache persist across container restarts.

---

## Building the Desktop Companion (C#)

### Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8) (x64)
- Windows 10 1904 or later **or** Linux (x64) with GStreamer and the `gstreamer1.0-plugins-*` packages
- The project targets `net8.0-windows10.0.19041.0` on Windows and `net8.0` on Linux via `#if WINDOWS / #if LINUX` compile-time constants that are set automatically based on the build OS

### Build

```bash
# Clone the repo first (see Cloning section above)
cd RuneReaderVoice

# Debug build
dotnet build RuneReaderVoice.sln -c Debug

# Release build
dotnet build RuneReaderVoice.sln -c Release

# Run directly
dotnet run --project RuneReaderVoice/RuneReaderVoice.csproj -c Debug
```

### Publish (self-contained executable)

```powershell
# Windows — single self-contained exe
dotnet publish RuneReaderVoice/RuneReaderVoice.csproj -c Release -r win-x64 --self-contained

# Linux
dotnet publish RuneReaderVoice/RuneReaderVoice.csproj -c Release -r linux-x64 --self-contained
```

### Key Dependencies (auto-restored by NuGet)

| Package | Purpose |
|---|---|
| Avalonia 11.x | Cross-platform UI framework |
| OpenCvSharp4 | Screen capture and QR detection |
| ZXing.Net | QR code decoding |
| KokoroSharp.CPU | In-process Kokoro TTS (no server required for local Kokoro) |
| NAudio / GStreamer | Audio playback (Windows / Linux respectively) |
| NWaves | DSP post-processing pipeline |
| OggVorbisEncoder | WAV → OGG transcoding for cache |

---

## Installing the WoW Addon

The addon source lives in `LUAAddon/` (or the standalone [RuneReaderAddonVoice](https://github.com/station384/RuneReaderAddonVoice) repo).

1. Locate your WoW AddOns directory:
   ```
   Windows: C:\Program Files (x86)\World of Warcraft\_retail_\Interface\AddOns\
   ```

2. Copy or symlink the `LUAAddon/` folder into AddOns, renaming it to match the addon:
   ```
   Interface\AddOns\RuneReaderVoice\
     core.lua
     payload.lua
     frames_qr.lua
     config.lua
     config_panel.lua
     base45.lua
     QRcodeEncoder.lua
     RuneReaderVoice.toc
   ```

3. Launch WoW. At the character select screen click **AddOns** and confirm **RuneReaderVoice** is enabled.

4. In-game, open the addon options panel via `/rrv` or through the standard Interface → AddOns menu to configure the QR display position and behavior.

> The addon renders an on-screen QR frame whenever NPC or quest dialog fires. The RuneReader Voice desktop companion must be running and pointed at your screen for synthesis to occur — the addon itself produces no audio.

---

## Reference Samples (Voice Matching)

F5-TTS and Chatterbox Turbo synthesize using a short reference audio clip to match the voice character. Place `.wav`, `.mp3`, `.flac`, or `.ogg` clips in `Server/data/samples/`. Naming follows the convention:

```
<race>-<gender>-<role>[-variant].<ext>
# Examples:
orc-male-warrior.wav
human-female-mage.wav
undead-male-vendor-raspy.wav
```

Each clip needs a `.ref.txt` sidecar with the transcript of what is spoken in the clip. If Whisper is configured, the server auto-generates missing sidecars whenever it detects a new file in the samples directory.

---

## Performance Reference

Tested on Ubuntu 24.04.4 LTS / NVIDIA RTX 3080 Laptop GPU (16 GB), 210-character input (~13 seconds of audio output):

| Backend | Audio Duration | Synth Time | Ratio |
|---|---|---|---|
| Kokoro | 13.14 s | 3.42 s | **3.8× realtime** |
| F5-TTS | 12.72 s | 5.52 s | **2.3× realtime** |
| Chatterbox Turbo | 12.60 s | 4.06 s | **3.1× realtime** |

Cache hit on any repeated line: **sub-millisecond**.

---

## License

RuneReader Voice is licensed under the **GNU General Public License v3.0 or later (GPL-3.0-or-later)**.  
See [`LICENSE`](LICENSE) and [`COPYING`](COPYING) for the full text.

---

*Tanstaafl Gaming — There Ain't No Such Thing As A Free Lunch.*
