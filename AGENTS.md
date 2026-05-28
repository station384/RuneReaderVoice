# AGENTS.md — RuneReader Voice

## Project Overview

RuneReader Voice (RRV) is a three-part system for AI-generated voice acting in World of Warcraft:

1. **WoW Addon (Lua)** — Intercepts NPC/quest dialog, encodes it as a QR code on-screen
2. **Desktop Companion (C# / AvaloniaUI / .NET 8)** — Screen-captures QR via OpenCV, decodes, sends text to TTS server, plays audio
3. **TTS Server (Python / FastAPI)** — Local AI TTS via Kokoro, F5-TTS, Chatterbox, CosyVoice, Qwen, Lux, LongCat; returns OGG audio

**Data flow:** `WoW → Lua Addon (QR on screen) → C# App (OpenCV decode) → Python Server (AI TTS) → OGG → C# App (playback)`

## Tech Stack

| Component | Language/Runtime | Framework | Key Libs |
|-----------|-----------------|-----------|----------|
| **WoW Addon** | Lua 5.1 | WoW API | custom QR encoder, base45 |
| **Desktop App** | C# 12 / .NET 8 | Avalonia 11 | OpenCvSharp4, ZXing.Net, NAudio, NWaves, sqlite-net, KokoroSharp, Velopack |
| **TTS Server** | Python 3.11+ | FastAPI + Uvicorn | onnxruntime, kokoro-onnx, f5-tts, torch, chatterbox-tts, soundfile, aiosqlite |

## Project Structure (top-level)

```
├── LUAAddon/          ← WoW addon (git submodule: RuneReaderAddonVoice)
│   ├── core.lua       ← Event hooks & dialog dispatch
│   ├── payload.lua    ← Dialog segmentation & encoding
│   ├── frames_qr.lua  ← QR frame rendering
│   └── *.toc          ← Addon manifests (Retail, Cata, Mists, Vanilla)
│
├── RuneReaderVoice/   ← C# Avalonia desktop companion
│   ├── Program.cs     ← Entry point, DI bootstrap
│   ├── TTS/           ← TTS subsystem (providers, cache, DSP, audio, pronunciation, text swap)
│   ├── Session/       ← QR barcode monitoring & dialog session assembly
│   ├── Protocol/      ← Wire protocol packet models
│   ├── Platform/      ← Platform abstraction (Windows DX11, Linux X11/Wayland)
│   ├── Data/          ← SQLite database layer
│   ├── Sync/          ← Server sync, auto-update
│   ├── UI/            ← Avalonia views & dialogs
│   └── config/        ← JSON config files (voice profiles, rules, catalogs)
│
├── Server/            ← Python TTS server
│   ├── rrv-server/    ← FastAPI server (main entry, backends, routes, cache)
│   └── rrv-*/         ← Isolated worker venvs (kokoro, f5, chatterbox, etc.)
│
├── RRV Notes/         ← Obsidian vault with design docs (Design_RuneReader_Voice_v26.md, Provider Tests.md)
├── BarcodeFont/       ← Custom barcode font and spec (RuneReaderBarcode-Spec.md)
├── PROJECT_RULES.md   ← AI assistant working rules (read this first)
└── RuneReaderVoice.sln
```

## Key Architecture Rules

### PCM-first playback contract
- Provider produces PCM → Cache stores OGG → Cache decodes OGG → Player receives PCM only
- Player knows nothing about WAV/OGG/files/decoder internals

### DSP is client-side post-retrieval
- DSP applied after audio decode on client
- Current cache key does NOT include DSP

### Server is a shared render engine
- Client remains authoritative for text, provider, voice, and DSP decisions
- Server renders and caches what client requests

## Build & Test Commands

```bash
# C# Desktop App
dotnet build RuneReaderVoice.sln
# Release builds: see RuneReaderVoice/build-release.ps1 / build-release-local.ps1

# Python TTS Server
cd Server/rrv-server
pip install -e ".[kokoro,dev]"   # install with dev deps
pytest                           # run tests (pytest-asyncio, auto mode)
uvicorn server.main:app          # run server
```

## Important Conventions

- **Code beats docs** — if code and design doc disagree, code is authoritative
- **KISS** — prefer straightforward, maintainable solutions
- **Latest patched baseline is authoritative** — do not revert to older uploaded source
- **Wait-for-full-text** uses post-split reality (final segment count after name expansion)
- **Client and server normalize text differently** — be aware of cache key mismatches
- **Chatterbox-family** providers split oversized text server-side at sentence boundaries

## Key Files

| File | Purpose |
|------|---------|
| `PROJECT_RULES.md` | Full working rules for AI assistants — READ THIS FIRST |
| `RRV Notes/Design_RuneReader_Voice_v26.md` | Current design document |
| `RRV Notes/Provider Tests.md` | TTS provider test results |
| `BarcodeFont/RuneReaderBarcode-Spec.md` | Custom barcode font specification |
| `RuneReaderVoice/Program.cs` | DI bootstrap and app composition |
| `RuneReaderVoice/TTS/Providers/ITtsProvider.cs` | TTS provider contract |
| `RuneReaderVoice/TTS/Cache/TtsAudioCache.cs` | Client audio cache |
| `Server/rrv-server/server/main.py` | TTS server entry point |
| `Server/rrv-server/server/backends/` | All TTS backend implementations |

## Git Notes

- `.onnx` model files tracked via Git LFS
- `LUAAddon/` is a git submodule
- Solution targets .NET 8 (`net8.0-windows`)
