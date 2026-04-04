# RuneReader Voice Server — Setup Guide

## Requirements

- Python 3.11
- **ffmpeg** — required for automatic audio/video conversion of reference samples
- 4 GB RAM minimum (8 GB recommended with F5-TTS or Chatterbox loaded)
- For F5-TTS / Chatterbox Turbo: NVIDIA CUDA 12.x, AMD ROCm 6.x, or CPU (slow)

## Installation

```bash
python3.11 -m venv .venv
source .venv/bin/activate

# Core install
pip install -e ".[kokoro]"

# F5-TTS backend (optional)
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
pip install f5-tts

# Chatterbox Turbo backend (optional)
# Must install with --no-deps to avoid numpy conflict with kokoro-onnx
pip install chatterbox-tts --no-deps
pip install conformer diffusers pykakasi pyloudnorm resemble-perth \
            s3tokenizer spacy-pkuseg onnx ml_dtypes --no-deps
```

## GPU Variants

### NVIDIA CUDA
```bash
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu121
pip install f5-tts chatterbox-tts --no-deps
# RRV_BACKENDS=kokoro,f5tts,chatterbox
```

### AMD ROCm
```bash
pip install torch torchaudio --index-url https://download.pytorch.org/whl/rocm6.1
pip install f5-tts chatterbox-tts --no-deps
# RRV_BACKENDS=kokoro,f5tts,chatterbox
```

## Model Files

Place model files before starting the server. The server **never** downloads
from HuggingFace at runtime — all files must be pre-placed by the admin.

```
data/models/
  kokoro-v1.0.onnx
  voices-v1.0.bin
  f5tts/
    F5TTS_v1_Base/
      model_1250000.safetensors
  chatterbox/
    (files from https://huggingface.co/ResembleAI/chatterbox-turbo)
  whisper/
    v3-turbo/   (files from https://huggingface.co/openai/whisper-large-v3-turbo)
    tiny/       (files from https://huggingface.co/openai/whisper-tiny)
```

See `models/MODELS.txt` for full file lists and download instructions.

## Reference Samples

For voice matching synthesis (F5-TTS, Chatterbox Turbo), place reference audio
clips in `data/samples/`. Each clip needs a `.ref.txt` transcript sidecar.
The server auto-transcribes new clips using Whisper if configured.

See `samples/SAMPLES.txt` for naming conventions and sidecar format.

## Configuration

Copy `.env.example` to `.env` and edit as needed:

```bash
cp .env.example .env
```

Key settings:
```dotenv
RRV_BACKENDS=kokoro,f5tts,chatterbox
RRV_GPU=auto
RRV_WHISPER_MODEL_DIR=./data/models/whisper/v3-turbo
RRV_SAMPLE_SCAN_INTERVAL=30
```

## Starting the Server

```bash
rrv-server
# or with overrides:
rrv-server --port 8765 --backends kokoro,f5tts,chatterbox
```

## Known CPU Limitations

F5-TTS and Chatterbox Turbo were designed primarily for GPU inference.
On CPU expect 30–120 seconds per synthesis request.

**Chatterbox Turbo CPU fix:** Chatterbox Turbo was never tested on CPU by
Resemble AI and has latent float64/float32 dtype mismatches throughout the
model. The server applies monkey-patches at load time to fix these:
- `librosa.load` is patched to always return float32
- `S3Tokenizer.log_mel_spectrogram` is patched to cast magnitudes to match
  `_mel_filters` dtype before matmul
- `VoiceEncoder.embeds_from_wavs` is patched to cast input wavs to float32

These patches are GPU-safe and are only applied when Chatterbox loads.

## Intel Arc (Linux)

Intel Arc on Linux uses CPU for PyTorch-based models (F5-TTS, Chatterbox Turbo)
— IPEX support is experimental and not relied upon. Kokoro via ONNX Runtime
uses ORT-CPU on Arc, which is fast enough given Kokoro's model size.
