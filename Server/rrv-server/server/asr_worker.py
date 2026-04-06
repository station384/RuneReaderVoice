# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# server/asr_worker.py
#
# Standalone ASR worker process entrypoint.
#
# Each ASR provider runs as an isolated worker subprocess in its own venv.
# Mirrors the architecture of server/worker.py for TTS backends.
#
# Communication with the host process is via a Unix domain socket using
# the same length-prefixed JSON protocol as TTS workers.
#
# Supported commands:
#   ping          — liveness check
#   capabilities  — returns provider capability dict
#   transcribe    — transcribe audio file; returns text + chunks
#
# The worker exits cleanly on SIGTERM or when the host closes the connection.

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import signal
import struct
import sys
from pathlib import Path
from typing import Optional

log = logging.getLogger("rrv.asr_worker")


async def _recv_message(reader: asyncio.StreamReader) -> Optional[dict]:
    try:
        length_bytes = await reader.readexactly(4)
    except asyncio.IncompleteReadError:
        return None
    length = struct.unpack(">I", length_bytes)[0]
    body = await reader.readexactly(length)
    return json.loads(body.decode("utf-8"))


async def _send_message(writer: asyncio.StreamWriter, obj: dict) -> None:
    body = json.dumps(obj).encode("utf-8")
    writer.write(struct.pack(">I", len(body)))
    writer.write(body)
    await writer.drain()


async def _handle_connection(
    reader: asyncio.StreamReader,
    writer: asyncio.StreamWriter,
    provider,
) -> None:
    """Handle one persistent connection from the host WorkerAsr proxy."""
    log.debug("ASR worker connection established")

    try:
        while True:
            msg = await _recv_message(reader)
            if msg is None:
                log.info("Host closed connection — ASR worker exiting")
                break

            cmd = msg.get("cmd")

            if cmd == "ping":
                await _send_message(writer, {
                    "status": "ok",
                    "provider": provider.provider_id,
                })

            elif cmd == "capabilities":
                caps = {
                    "provider_id":   provider.provider_id,
                    "display_name":  provider.display_name,
                    "requires_gpu":  provider.requires_gpu,
                    "loaded":        True,
                }
                # Self-report VRAM usage — vendor-agnostic
                try:
                    import torch
                    if torch.cuda.is_available():
                        caps["vram_used_mib"] = torch.cuda.memory_reserved(0) / (1024 * 1024)
                    elif hasattr(torch, "hip") and torch.hip.is_available():
                        caps["vram_used_mib"] = torch.cuda.memory_reserved(0) / (1024 * 1024)
                    else:
                        caps["vram_used_mib"] = 0.0
                except Exception:
                    caps["vram_used_mib"] = 0.0
                await _send_message(writer, {"status": "ok", "capabilities": caps})

            elif cmd == "transcribe":
                try:
                    from server.asr.base import TranscriptionRequest
                    request = TranscriptionRequest(
                        audio_path=Path(msg["audio_path"]),
                        language_hint=msg.get("language_hint", "en"),
                        return_timestamps=bool(msg.get("return_timestamps", False)),
                    )
                    result = await provider.transcribe(request)

                    chunks = [
                        {
                            "text":  c.text,
                            "start": c.start,
                            "end":   c.end,
                        }
                        for c in result.chunks
                    ]

                    await _send_message(writer, {
                        "status":   "ok",
                        "text":     result.text,
                        "language": result.language,
                        "chunks":   chunks,
                    })
                except (ValueError, RuntimeError) as exc:
                    log.warning("Transcription error: %s", exc)
                    await _send_message(writer, {"status": "error", "message": str(exc)})
                except Exception as exc:
                    log.exception("Unexpected transcription error")
                    await _send_message(writer, {"status": "error", "message": repr(exc)})

            else:
                await _send_message(writer, {
                    "status":  "error",
                    "message": f"Unknown command: {cmd!r}",
                })

    except Exception:
        log.exception("ASR worker connection handler error")
    finally:
        writer.close()
        try:
            await writer.wait_closed()
        except Exception:
            pass


async def _main(args: argparse.Namespace) -> None:
    logging.basicConfig(
        level=args.log_level.upper(),
        format=f"[asr_worker:{args.provider}] %(levelname)-8s %(name)s — %(message)s",
        stream=sys.stdout,
    )

    # GPU detection
    try:
        from server.gpu_detect import detect as detect_gpu
        gpu = detect_gpu(args.gpu)
        gpu_provider = gpu.provider
    except ImportError:
        log.warning("gpu_detect not available in ASR worker venv — defaulting to cpu")
        gpu_provider = "cpu"

    models_dir = Path(args.models_dir)
    provider = _instantiate_provider(args.provider, models_dir, gpu_provider, args)

    log.info("Loading ASR provider '%s'...", args.provider)
    await provider.load()
    log.info("ASR provider '%s' loaded", args.provider)

    loop = asyncio.get_running_loop()
    stop_event = asyncio.Event()

    def _on_sigterm():
        log.info("SIGTERM received — shutting down ASR worker")
        stop_event.set()

    loop.add_signal_handler(signal.SIGTERM, _on_sigterm)

    socket_path = args.socket
    try:
        os.unlink(socket_path)
    except FileNotFoundError:
        pass

    server = await asyncio.start_unix_server(
        lambda r, w: _handle_connection(r, w, provider),
        path=socket_path,
    )

    log.info("ASR worker '%s' listening on %s", args.provider, socket_path)
    print(f"READY:{args.provider}:{socket_path}", flush=True)

    async with server:
        await stop_event.wait()

    log.info("ASR worker '%s' shut down cleanly", args.provider)
    try:
        os.unlink(socket_path)
    except FileNotFoundError:
        pass


def _instantiate_provider(name: str, models_dir: Path, gpu_provider: str, args: argparse.Namespace):
    """Instantiate the correct ASR provider class for this worker."""
    if name == "qwen_asr":
        from server.asr.qwen_asr_provider import QwenAsrProvider
        size = getattr(args, "model_size", "1.7b") or "1.7b"
        return QwenAsrProvider(models_dir=models_dir, gpu_provider=gpu_provider, size=size)

    elif name == "crisper_whisper":
        from server.asr.crisper_whisper_provider import CrisperWhisperProvider
        return CrisperWhisperProvider(models_dir=models_dir, gpu_provider=gpu_provider)

    elif name == "cohere_transcribe":
        from server.asr.cohere_transcribe_provider import CohereTranscribeProvider
        return CohereTranscribeProvider(models_dir=models_dir, gpu_provider=gpu_provider)

    else:
        raise ValueError(f"Unknown ASR provider: {name!r}")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="rrv-asr-worker",
        description="RuneReader Voice ASR worker subprocess",
    )
    parser.add_argument("--provider",   required=True, help="ASR provider name")
    parser.add_argument("--socket",     required=True, help="Unix socket path to bind")
    parser.add_argument("--models-dir", dest="models_dir", required=True, help="Model files directory")
    parser.add_argument("--gpu",        default="auto", choices=["auto", "cuda", "rocm", "cpu"])
    parser.add_argument("--model-size", dest="model_size", default="1.7b",
                        help="Model size for providers that offer multiple sizes (e.g. qwen_asr: 1.7b, 0.6b)")
    parser.add_argument("--log-level",  dest="log_level", default="info",
                        choices=["debug", "info", "warning", "error"])
    return parser.parse_args()


if __name__ == "__main__":
    args = _parse_args()
    try:
        asyncio.run(_main(args))
    except KeyboardInterrupt:
        pass
