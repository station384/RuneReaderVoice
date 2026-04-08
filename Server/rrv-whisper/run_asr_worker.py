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


def _is_cuda_oom(e: Exception) -> bool:
    """Return True if exception is a CUDA out-of-memory error."""
    msg = str(e).lower()
    return "cuda out of memory" in msg or "out of memory" in msg and "cuda" in msg


def _load_whisper_cpu(model_dir: Path):
    """Load Whisper on CPU. Used as OOM fallback."""
    import torch
    from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
    log.info("Whisper: loading on CPU (OOM fallback) from %s", model_dir)
    model = AutoModelForSpeechSeq2Seq.from_pretrained(
        str(model_dir), torch_dtype=torch.float32,
        low_cpu_mem_usage=True, use_safetensors=True, local_files_only=True,
    )
    processor = AutoProcessor.from_pretrained(str(model_dir), local_files_only=True)
    pipe = pipeline(
        "automatic-speech-recognition", model=model,
        tokenizer=processor.tokenizer, feature_extractor=processor.feature_extractor,
        torch_dtype=torch.float32, device="cpu",
    )
    gc = getattr(model, "generation_config", None)
    if gc is not None:
        gc.condition_on_previous_text = False
        gc.suppress_tokens = None
        gc.begin_suppress_tokens = None
    log.info("Whisper: CPU fallback loaded")
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

    duration_sec = len(audio_data) / sample_rate
    log.info("Whisper transcribing '%s' sr=%d samples=%d duration=%.1fs",
             audio_path.name, sample_rate, len(audio_data), duration_sec)

    # Route to CPU if the file is too long OR if available VRAM is too low.
    # Two failure modes require CPU routing:
    #
    # 1. Duration gate (RRV_WHISPER_MAX_GPU_SEC, default 600s):
    #    Long files cause unrecoverable CUDA device-side asserts deep inside
    #    HuggingFace's chunked pipeline — a crash that kills the entire worker
    #    process before the Python except block can run. The proactive CPU route
    #    is the only reliable defence.
    #
    # 2. VRAM pressure gate (RRV_WHISPER_MIN_FREE_VRAM_MIB, default 1500 MiB):
    #    When TTS backends (LongCat, Chatterbox, F5) are resident they consume
    #    12-14 GB of the available 15.58 GB. Even a short file can trigger the
    #    same unrecoverable CUDA crash if there isn't enough headroom for the
    #    chunked inference pass. Check free VRAM before attempting GPU and
    #    fall back to CPU proactively rather than crashing.
    _MAX_GPU_DURATION_SEC  = float(os.environ.get("RRV_WHISPER_MAX_GPU_SEC",       "600"))
    _MIN_FREE_VRAM_MIB     = float(os.environ.get("RRV_WHISPER_MIN_FREE_VRAM_MIB", "1500"))

    _route_to_cpu = False
    _route_reason = ""

    if duration_sec > _MAX_GPU_DURATION_SEC:
        _route_to_cpu = True
        _route_reason = f"duration {duration_sec:.0f}s > {_MAX_GPU_DURATION_SEC:.0f}s limit"

    if not _route_to_cpu and torch.cuda.is_available():
        try:
            _props     = torch.cuda.get_device_properties(0)
            _free_mib  = (_props.total_memory - torch.cuda.memory_reserved(0)) / (1024 * 1024)
            if _free_mib < _MIN_FREE_VRAM_MIB:
                _route_to_cpu = True
                _route_reason = f"only {_free_mib:.0f} MiB VRAM free (need {_MIN_FREE_VRAM_MIB:.0f} MiB)"
        except Exception:
            pass

    if _route_to_cpu and hasattr(pipe, "model") and \
            getattr(getattr(pipe, "model", None), "device", None) is not None and \
            str(pipe.model.device) != "cpu":
        log.warning(
            "Whisper: '%s' routing to CPU — %s",
            audio_path.name, _route_reason,
        )
        import torch
        cpu_pipe = _load_whisper_cpu(audio_path.parent.parent / "models" / "whisper" / "v3-turbo")
        result = _do_transcribe(cpu_pipe, audio_data, sample_rate, language, audio_path.name)
        del cpu_pipe
        try:
            torch.cuda.empty_cache()
        except Exception:
            pass
        return result

    return _do_transcribe(pipe, audio_data, sample_rate, language, audio_path.name)


def _do_transcribe(pipe, audio_data, sample_rate: int, language: str, name: str) -> dict:
    """Run the actual pipeline inference and return result dict."""
    # Pre-inference VRAM check — raise a catchable Python error before touching
    # CUDA rather than letting the device-side assert kill the worker process.
    # The _handle_client OOM handler can recover from a Python exception but
    # cannot recover from a CUDA device-side assert (process dies immediately).
    import torch
    pipe_device = getattr(getattr(pipe, "model", None), "device", None)
    if pipe_device is not None and str(pipe_device) != "cpu" and torch.cuda.is_available():
        try:
            free_mib = (torch.cuda.get_device_properties(0).total_memory
                        - torch.cuda.memory_reserved(0)) / (1024 * 1024)
            if free_mib < _MIN_FREE_VRAM_MIB:
                raise RuntimeError(
                    f"CUDA out of memory (pre-check): only {free_mib:.0f} MiB free, "
                    f"need {_MIN_FREE_VRAM_MIB:.0f} MiB for '{name}'"
                )
        except RuntimeError:
            raise
        except Exception:
            pass  # VRAM query failed — proceed and let the real OOM handler catch it

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
    pipe_ref: list,   # mutable [pipe] — allows CPU fallback to replace pipe in-place
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
                        "device": getattr(getattr(pipe_ref[0], "model", None), "device", "unknown") if pipe_ref else "unknown",
                        "vram_used_mib": vram,
                    }
                })

            elif cmd == "transcribe":
                audio_path = Path(msg.get("audio_path", ""))
                language = msg.get("language", "en") or "en"
                try:
                    result = _transcribe(pipe_ref[0], audio_path, language)
                    await _send_message(writer, {"status": "ok", **result})
                except Exception as e:
                    if _is_cuda_oom(e) and pipe_ref[0].device.type != "cpu":
                        # GPU OOM during inference — unload GPU model, reload on
                        # CPU, and retry. The CPU model stays loaded for the rest
                        # of this worker session so subsequent files also benefit.
                        log.warning(
                            "Whisper: CUDA OOM on '%s' — falling back to CPU and retrying",
                            audio_path.name,
                        )
                        try:
                            import torch
                            del pipe_ref[0]
                            torch.cuda.empty_cache()
                            pipe_ref[0] = _load_whisper_cpu(model_dir)
                            result = _transcribe(pipe_ref[0], audio_path, language)
                            await _send_message(writer, {"status": "ok", **result})
                        except Exception as cpu_e:
                            log.error("Whisper: CPU fallback also failed for '%s': %s", audio_path.name, cpu_e)
                            await _send_message(writer, {"status": "error", "error": str(cpu_e)})
                    else:
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
        lambda r, w: _handle_client(r, w, [pipe], model_dir, gpu_provider),
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
