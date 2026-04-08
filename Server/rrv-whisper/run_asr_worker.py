#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
#
# rrv-whisper/run_asr_worker.py
#
# Subprocess entrypoint for the Whisper ASR worker.
# Launched by WorkerAsr when RRV_ASR_PROVIDER=whisper.
# Implements the same socket protocol as other ASR workers.
#
# This process loads Whisper, handles transcription requests over a Unix
# socket, then exits — releasing all GPU/CPU memory cleanly.

import sys
import os

# Ensure rrv-server/server is importable
_server_dir = os.path.join(os.path.dirname(__file__), '..', 'rrv-server')
sys.path.insert(0, os.path.abspath(_server_dir))

import asyncio
import argparse
import logging
import struct
import json
from pathlib import Path

log = logging.getLogger("rrv.whisper_asr_worker")

# ── Whisper implementation ────────────────────────────────────────────────────

_WHISPER_MIN_VRAM_MIB = 1800


def _load_whisper(model_dir: Path):
    """Load Whisper pipeline. Returns pipeline or raises."""
    try:
        import torch
        from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
    except ImportError as e:
        raise RuntimeError(f"transformers/torch not installed: {e}") from e

    _cuda_ok = False
    if torch.cuda.is_available():
        _free_mib = (
            torch.cuda.get_device_properties(0).total_memory
            - torch.cuda.memory_reserved(0)
        ) / (1024 * 1024)
        _cuda_ok = _free_mib >= _WHISPER_MIN_VRAM_MIB
        if not _cuda_ok:
            log.info(
                "Whisper: only %.0fMiB VRAM free (need %d) — loading on CPU",
                _free_mib, _WHISPER_MIN_VRAM_MIB
            )

    device = "cuda" if _cuda_ok else "cpu"
    torch_dtype = torch.float16 if _cuda_ok else torch.float32

    log.info("Loading Whisper from %s (device=%s)", model_dir, device)

    model = AutoModelForSpeechSeq2Seq.from_pretrained(
        str(model_dir),
        torch_dtype=torch_dtype,
        low_cpu_mem_usage=True,
        use_safetensors=True,
        local_files_only=True,
    )
    model.to(device)

    processor = AutoProcessor.from_pretrained(
        str(model_dir),
        local_files_only=True,
    )

    pipe = pipeline(
        "automatic-speech-recognition",
        model=model,
        tokenizer=processor.tokenizer,
        feature_extractor=processor.feature_extractor,
        torch_dtype=torch_dtype,
        device=device,
    )

    gc = getattr(model, "generation_config", None)
    if gc is not None:
        gc.condition_on_previous_text = False
        gc.suppress_tokens = None
        gc.begin_suppress_tokens = None

    log.info("Whisper loaded (device=%s)", device)
    return pipe


def _transcribe(pipe, audio_path: Path, language: str = "en") -> dict:
    """Transcribe audio. Returns {text, language, chunks}."""
    import soundfile as sf
    import numpy as np

    raw_audio, sample_rate = sf.read(str(audio_path), dtype="float32")
    if len(raw_audio.shape) > 1:
        audio_data = raw_audio.mean(axis=1)
    else:
        audio_data = raw_audio

    log.info("Whisper transcribing '%s' sr=%d samples=%d", audio_path.name, sample_rate, len(audio_data))

    result = pipe(
        {"array": audio_data, "sampling_rate": sample_rate},
        generate_kwargs={
            "task": "transcribe",
            "language": language,
            "no_repeat_ngram_size": 5,
        },
        return_timestamps="word",
    )

    text = result.get("text", "").strip() if isinstance(result, dict) else ""
    chunks = result.get("chunks", []) if isinstance(result, dict) else []

    log.info(
        "Whisper output: chars=%d chunks=%d text='%s'",
        len(text), len(chunks),
        text[:80] + ("..." if len(text) > 80 else "")
    )
    return {"text": text, "language": language, "chunks": chunks}


# ── Socket protocol (mirrors asr_worker.py) ───────────────────────────────────

async def _send_message(writer: asyncio.StreamWriter, msg: dict) -> None:
    data = json.dumps(msg).encode()
    writer.write(struct.pack(">I", len(data)) + data)
    await writer.drain()


async def _recv_message(reader: asyncio.StreamReader) -> dict:
    header = await asyncio.wait_for(reader.readexactly(4), timeout=60)
    length = struct.unpack(">I", header)[0]
    data = await asyncio.wait_for(reader.readexactly(length), timeout=300)
    return json.loads(data.decode())


async def _handle_client(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    pipe,
    model_dir: Path,
    gpu_provider: str,
) -> None:
    try:
        while True:
            try:
                msg = await _recv_message(reader)
            except (asyncio.IncompleteReadError, asyncio.TimeoutError, ConnectionResetError):
                break

            cmd = msg.get("cmd")

            if cmd == "ping":
                await _send_message(writer, {"status": "ok", "pong": True})

            elif cmd == "capabilities":
                import torch
                vram = 0.0
                try:
                    if torch.cuda.is_available():
                        vram = torch.cuda.memory_reserved(0) / (1024 * 1024)
                except Exception:
                    pass
                await _send_message(writer, {
                    "status": "ok",
                    "capabilities": {
                        "provider_id": "whisper",
                        "display_name": "Whisper",
                        "requires_gpu": False,
                        "loaded": True,
                        "vram_used_mib": vram,
                    }
                })

            elif cmd == "transcribe":
                audio_path = Path(msg.get("audio_path", ""))
                language = msg.get("language", "en") or "en"
                try:
                    result = _transcribe(pipe, audio_path, language)
                    await _send_message(writer, {"status": "ok", **result})
                except Exception as e:
                    log.error("Transcription failed: %s", e)
                    await _send_message(writer, {"status": "error", "error": str(e)})

            elif cmd == "shutdown":
                await _send_message(writer, {"status": "ok"})
                break

            else:
                await _send_message(writer, {"status": "error", "error": f"Unknown command: {cmd}"})

    finally:
        writer.close()


async def _main(args) -> None:
    logging.basicConfig(
        level=args.log_level.upper(),
        format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    # Import gpu_detect from the server package
    try:
        from server.gpu_detect import detect_gpu
        gpu_provider, _ = detect_gpu(preferred=args.gpu)
    except Exception:
        gpu_provider = "cpu"

    model_dir = Path(args.model_dir)
    log.info("Loading Whisper model from %s", model_dir)

    try:
        pipe = _load_whisper(model_dir)
    except Exception as e:
        log.error("Failed to load Whisper: %s", e)
        sys.exit(1)

    sock_path = args.socket
    server = await asyncio.start_unix_server(
        lambda r, w: _handle_client(r, w, pipe, model_dir, gpu_provider),
        path=sock_path,
    )

    # Signal ready to parent
    print(f"READY:{sock_path}", flush=True)
    log.info("Whisper ASR worker listening on %s", sock_path)

    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--socket", required=True)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--gpu", default="auto")
    parser.add_argument("--log-level", default="info")
    args, _ = parser.parse_known_args()  # ignore --provider, --models-dir etc from WorkerAsr
    asyncio.run(_main(args))
