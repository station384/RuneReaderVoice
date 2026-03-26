# SPDX-License-Identifier: GPL-3.0-or-later
# server/transcriber.py
#
# Whisper-based auto-transcription service for reference audio samples.
#
# Responsibilities:
#   - Convert non-WAV audio/video files to WAV using ffmpeg
#   - Load Whisper from a local directory (never downloads from HuggingFace)
#   - Transcribe any audio sample that lacks a .ref.txt sidecar
#   - Write the .ref.txt sidecar alongside the audio file
#   - Unload Whisper from memory after transcription is complete
#
# Only started when at least one voice-matching backend is loaded (f5tts, chatterbox).
# If RRV_WHISPER_MODEL_DIR does not exist or is empty, logs a warning and skips.
# Never blocks the synthesis request path — runs in a thread pool executor.
#
# The polling loop (run from main.py) calls scan_and_transcribe() on a
# configurable interval (RRV_SAMPLE_SCAN_INTERVAL, default 30 seconds).
#
# ── ffmpeg conversion ──────────────────────────────────────────────────────────
# Any file with a CONVERTIBLE_EXTENSION found in the samples directory is
# automatically converted to 16kHz mono PCM_16 WAV. The original file is moved
# to data/samples/originals/ for safekeeping. ffmpeg handles both audio-only
# files and video files (MP4, MKV, WEBM etc.) — video tracks are stripped and
# audio is extracted. If ffmpeg is not installed, conversion is skipped with a
# warning logged at startup.
#
# Supported input formats:
#   Audio: .mp3 .aac .m4a .flac .ogg
#   Video: .mp4 .mkv .webm .avi
#
# Target WAV format: 16kHz, mono, PCM_16
#   - 16kHz: sweet spot for all three backends (Whisper, F5-TTS, Chatterbox)
#   - Mono: reduces file size, avoids stereo handling differences
#   - PCM_16: guarantees librosa.load() returns float32 (critical for Chatterbox CPU)

from __future__ import annotations

import asyncio
import logging
import os
import shutil
import subprocess
from pathlib import Path
from typing import Optional

from .samples import VALID_EXTENSIONS, _VALID_STEM_RE, _load_sidecar
from .voice_profiler import profile_voice
from .sample_extractor import extract_clips

log = logging.getLogger(__name__)

# Extensions that can be converted to WAV via ffmpeg
CONVERTIBLE_EXTENSIONS = frozenset({
    ".mp3", ".aac", ".m4a", ".m4b", ".flac", ".ogg",   # audio (.m4b = audiobook, same as mp4)
    ".mp4", ".mkv", ".webm", ".avi",                     # video — audio track extracted
})

# Target WAV parameters
#_WAV_SAMPLE_RATE = 22000
_WAV_SAMPLE_RATE = 44100
#_WAV_CHANNELS    = 1
_WAV_CHANNELS    = 2
_WAV_CODEC       = "pcm_s16le"   # PCM_16 — guarantees float32 from librosa

# Silence padding added to every converted sample.
# F5-TTS loses pacing if the reference clip starts or ends abruptly — it needs
# a short runway of silence before the voice starts and after it ends so the
# ODE solver can stabilise at the boundaries. Without lead silence, generated
# audio is compressed/rushed at the start. Without tail silence, the model
# may clip the last phoneme or mis-time the final word.
_PAD_LEAD_SECONDS = 0.5   # 0.5s silence prepended
_PAD_TAIL_SECONDS = 1.0   # 1.0s silence appended

# Generated clip retranscription uses batched inference on the same Whisper
# pipeline rather than loading multiple Whisper model copies. This improves GPU
# utilisation without duplicating the model in VRAM. Default batch size is 2,
# which can be overridden for backfill runs.
_GENERATED_BATCH_SIZE = max(1, int(os.getenv("RRV_WHISPER_GENERATED_BATCH_SIZE", "2") or "2"))
_MASTER_BATCH_SIZE = max(1, int(os.getenv("RRV_WHISPER_MASTER_BATCH_SIZE", "2") or "2"))

_GENERATED_RETURN_TIMESTAMPS = str(os.getenv("RRV_WHISPER_GENERATED_RETURN_TIMESTAMPS", "0") or "0").strip().lower() in {"1", "true", "yes", "on"}


def check_ffmpeg() -> bool:
    """
    Check whether ffmpeg is available on PATH.
    Called once at startup. Returns True if available.
    """
    if shutil.which("ffmpeg") is None:
        log.warning(
            "ffmpeg not found on PATH — audio/video conversion disabled. "
            "Install with: sudo apt install ffmpeg  (Ubuntu) "
            "or: brew install ffmpeg  (macOS)"
        )
        return False
    log.info("ffmpeg available — audio/video conversion enabled")
    return True


def _gender_prefix(profile: str) -> str:
    """
    Parse the gender prefix from a voice profile description string.
    Profile format: "Male · Bass · ..."  /  "Female · Soprano · ..."  /  "Unknown · ..."
    Returns "M_", "F_", or "U_" (unknown/ambiguous).

    "Unknown" is emitted by voice_profiler when F0 falls in the 140-175Hz
    ambiguous overlap zone — common for lower female voices and higher male
    voices. U_ is honest about the uncertainty rather than forcing a guess.
    """
    if not profile:
        return "U_"
    first_word = profile.split()[0].lower()
    if first_word == "male":
        return "M_"
    elif first_word == "female":
        return "F_"
    return "U_"  # "Unknown" or any unexpected value


def _apply_gender_prefix(audio_path: Path, prefix: str) -> Path:
    """
    Rename audio_path and all its sidecars to add a gender prefix, then
    move all files into a subdirectory named after the new stem.

    Layout after:
        samples/<new_stem>/<new_stem>.wav
        samples/<new_stem>/<new_stem>.ref.txt
        samples/<new_stem>/<new_stem>.txt

    Only renames/moves if the stem does not already start with M_, F_, or U_.
    If already prefixed, moves into subdirectory without renaming.
    Returns the new path of the audio file.
    """
    stem   = audio_path.stem
    parent = audio_path.parent

    # Determine final stem (with prefix if not already present)
    if len(stem) > 2 and stem[1] == "_" and stem[0] in ("M", "F", "U"):
        log.debug("_apply_gender_prefix: '%s' already has prefix", audio_path.name)
        new_stem = stem
    else:
        new_stem = prefix + stem

    # Create subdirectory
    subdir = parent / new_stem
    try:
        subdir.mkdir(parents=True, exist_ok=True)
    except Exception as e:
        log.warning(
            "Failed to create subdirectory '%s': %s — keeping flat layout",
            subdir, e
        )
        return audio_path

    new_wav     = subdir / (new_stem + audio_path.suffix)
    new_ref_txt = subdir / (new_stem + ".ref.txt")
    new_txt     = subdir / (new_stem + ".txt")
    old_ref_txt = parent / (stem + ".ref.txt")
    old_txt     = parent / (stem + ".txt")

    try:
        audio_path.rename(new_wav)
        if old_ref_txt.exists():
            old_ref_txt.rename(new_ref_txt)
        if old_txt.exists():
            old_txt.rename(new_txt)

        log.info(
            "Sample organised: '%s' → '%s/' (prefix=%s)",
            audio_path.name, subdir.name, prefix if new_stem != stem else "existing"
        )
        return new_wav

    except Exception as e:
        log.warning(
            "Failed to organise '%s' into subdirectory: %s — keeping original path",
            audio_path.name, e
        )
        return audio_path


# Maximum sample duration for transcription and extraction.
# Files longer than this are rejected — they cannot produce useful provider
# clips (max 38s) and would OOM during librosa.load on the full audio array.
MAX_SAMPLE_DURATION_SEC = 1200.0  # 20 minutes — covers typical audiobook chapter segments


def _pad_wav_silence(wav_path: Path) -> None:
    """
    Prepend _PAD_LEAD_SECONDS and append _PAD_TAIL_SECONDS of silence to the
    WAV file at wav_path, in-place.  No-op if soundfile is not available.

    Called immediately after ffmpeg conversion so all downstream consumers
    (Whisper, voice_profiler, sample_extractor, F5-TTS, Chatterbox) see the
    padded file.  The silence is pure zeros at the file sample rate — no
    re-encoding artefacts.
    """
    try:
        import numpy as np
        import soundfile as sf

        data, sr = sf.read(str(wav_path), dtype="float32", always_2d=False)

        lead_samples = int(round(_PAD_LEAD_SECONDS * sr))
        tail_samples = int(round(_PAD_TAIL_SECONDS * sr))

        if data.ndim == 1:
            lead = np.zeros(lead_samples, dtype=data.dtype)
            tail = np.zeros(tail_samples, dtype=data.dtype)
        else:
            lead = np.zeros((lead_samples, data.shape[1]), dtype=data.dtype)
            tail = np.zeros((tail_samples, data.shape[1]), dtype=data.dtype)

        padded = np.concatenate([lead, data, tail], axis=0)
        sf.write(str(wav_path), padded, sr, subtype="PCM_16")

        log.info(
            "Padded '%s' — added %.2fs lead + %.2fs tail silence (%.2fs → %.2fs total)",
            wav_path.name,
            _PAD_LEAD_SECONDS, _PAD_TAIL_SECONDS,
            len(data) / sr, len(padded) / sr,
        )
    except ImportError:
        log.warning("soundfile not available — silence padding skipped for '%s'", wav_path.name)
    except Exception as e:
        log.warning("Silence padding failed for '%s': %s — continuing without padding", wav_path.name, e)



def _write_sidecar_with_debug(sidecar_path: Path, text: str, *, kind: str, source_audio: Path | None = None) -> None:
    """Write a sidecar file and emit a debug log with exact path details."""
    sidecar_path.write_text(text, encoding="utf-8")
    if source_audio is not None:
        log.info(
            "Sidecar write: kind=%s audio='%s' sidecar='%s' chars=%d",
            kind, source_audio, sidecar_path, len(text)
        )
    else:
        log.info(
            "Sidecar write: kind=%s sidecar='%s' chars=%d",
            kind, sidecar_path, len(text)
        )


def _is_cuda_oom_error(exc: Exception) -> bool:
    message = str(exc).lower()
    return "out of memory" in message and "cuda" in message


def _log_cuda_memory(prefix: str) -> None:
    try:
        import torch
        if torch.cuda.is_available():
            allocated = torch.cuda.memory_allocated() / (1024 * 1024)
            reserved = torch.cuda.memory_reserved() / (1024 * 1024)
            log.info("%s cuda_mem_allocated=%.1fMiB cuda_mem_reserved=%.1fMiB", prefix, allocated, reserved)
    except Exception:
        pass


def _transcribe_batched_clips(pipe, clips: list, language_hint: str, *, batch_size: int, queue_name: str, sidecar_kind: str, return_timestamps: bool | str = False) -> None:
    """Run Whisper over clip batches using a single loaded pipeline."""
    if not clips:
        return

    batch_size = max(1, int(batch_size or 1))
    total = len(clips)
    log.info(
        "%s starting — clips=%d batch_size=%d",
        queue_name, total, batch_size
    )

    index = 0
    while index < total:
        batch = clips[index:index + batch_size]
        try:
            results = TranscriptionService._transcribe_many_static(
                pipe,
                [clip.path for clip in batch],
                language_hint=language_hint,
                batch_size=len(batch),
                return_timestamps=return_timestamps,
            )
            for clip, (transcript, _clip_language, _chunks) in zip(batch, results):
                if not transcript:
                    log.warning(
                        "%s returned empty result: audio='%s'",
                        queue_name,
                        clip.path,
                    )
                    continue
                _write_sidecar_with_debug(
                    clip.ref_path,
                    transcript,
                    kind=sidecar_kind,
                    source_audio=clip.path,
                )
            index += len(batch)
        except Exception as e:
            if len(batch) > 1 and _is_cuda_oom_error(e):
                log.warning(
                    "%s hit CUDA OOM at batch_size=%d — falling back to serial for remaining %d clip(s)",
                    queue_name, len(batch), total - index
                )
                _log_cuda_memory(f"{queue_name} OOM:")
                try:
                    import torch
                    if torch.cuda.is_available():
                        torch.cuda.empty_cache()
                except Exception:
                    pass
                batch_size = 1
                continue

            if len(batch) > 1:
                log.warning(
                    "%s failed at batch_size=%d — retrying serial for this batch: %s",
                    queue_name, len(batch), e
                )
                for clip in batch:
                    try:
                        if return_timestamps:
                            transcript, _clip_language, _chunks = TranscriptionService._transcribe_one_static(
                                pipe, clip.path, language_hint=language_hint
                            )
                        else:
                            transcript, _clip_language, _chunks = TranscriptionService._transcribe_many_static(
                                pipe,
                                [clip.path],
                                language_hint=language_hint,
                                batch_size=1,
                                return_timestamps=False,
                            )[0]
                        if not transcript:
                            log.warning(
                                "%s returned empty result: audio='%s'",
                                queue_name,
                                clip.path,
                            )
                            continue
                        _write_sidecar_with_debug(
                            clip.ref_path,
                            transcript,
                            kind=sidecar_kind,
                            source_audio=clip.path,
                        )
                    except Exception as inner:
                        log.warning(
                            "%s failed: audio='%s' error=%s",
                            queue_name, clip.path, inner
                        )
                index += len(batch)
                continue

            clip = batch[0]
            log.warning(
                "%s failed: audio='%s' error=%s",
                queue_name, clip.path, e
            )
            index += 1


def _retranscribe_generated_clips(pipe, clips: list, language_hint: str, batch_size: int = _GENERATED_BATCH_SIZE) -> None:
    """Re-run Whisper on generated clips using batched inference on a single pipeline."""
    _transcribe_batched_clips(
        pipe,
        clips,
        language_hint,
        batch_size=batch_size,
        queue_name="Generated clip retranscription queue",
        sidecar_kind="generated_ref",
        return_timestamps="word" if _GENERATED_RETURN_TIMESTAMPS else False,
    )





def _clean_hallucinated_transcript(text: str, chunks: list) -> tuple[str, list]:
    """
    Detect and remove Whisper hallucination patterns from a transcript.

    Whisper v3-turbo has two known hallucination modes on padded/silent audio:
      1. Phrase looping: "I'm sorry. I'm sorry. I'm sorry. ..." — the model
         repeats a short phrase many times, massively inflating the text length
         relative to the audio. This corrupts F5-TTS pacing (audio/text ratio).
      2. Interleaved loops: real speech, then a loop, then real speech again.
         The corrupted ref.txt in the bug report was of this form.

    Strategy:
      - Split the transcript into sentences/phrases.
      - Count how many times each unique phrase appears.
      - If any phrase appears >= REPEAT_THRESHOLD times, the transcript is
        considered hallucinated. Remove all occurrences of the repeated phrase
        and keep only the non-repeated content.
      - Filter chunks to match the cleaned text.
      - If the cleaned text is empty or trivially short, return empty (caller
        will log a warning and skip writing ref.txt).

    This is a last-resort guard. The generate_kwargs changes to the Whisper
    pipeline should prevent hallucination from occurring in the first place.
    """
    import re

    REPEAT_THRESHOLD = 3   # phrase appearing >= this many times = hallucination

    if not text:
        return text, chunks

    # Split on sentence-ending punctuation, keeping the delimiter
    sentences = re.split(r'(?<=[.!?,])\s+', text.strip())
    sentences = [s.strip() for s in sentences if s.strip()]

    if len(sentences) < REPEAT_THRESHOLD:
        return text, chunks   # too short to analyse

    # Count occurrences of each sentence (case-insensitive)
    from collections import Counter
    counts = Counter(s.lower() for s in sentences)
    repeated = {phrase for phrase, count in counts.items() if count >= REPEAT_THRESHOLD}

    if not repeated:
        return text, chunks   # no hallucination detected

    log.warning(
        "_clean_hallucinated_transcript: detected %d repeated phrase(s) — "
        "removing hallucinated content. Repeated: %s",
        len(repeated),
        ", ".join(f"'{p[:40]}'" for p in list(repeated)[:3])
    )

    # Keep only sentences that are NOT hallucinated phrases
    clean_sentences = [s for s in sentences if s.lower() not in repeated]
    if not clean_sentences:
        log.warning(
            "_clean_hallucinated_transcript: all sentences were hallucinated — "
            "transcript cleared. Clip may need manual review."
        )
        return "", []

    clean_text = " ".join(clean_sentences)

    # Filter chunks: keep only chunks whose text is not a hallucinated phrase.
    # We check each chunk word against the hallucinated phrases — if a chunk's
    # text exactly matches one, drop it. This is approximate since chunks are
    # word-level, not sentence-level.
    clean_chunks = []
    for chunk in chunks:
        chunk_text = chunk.get("text", "").strip().lower()
        # Drop chunk if it's exactly a hallucinated phrase or a fragment of one
        is_hallucinated = any(
            chunk_text in phrase or phrase in chunk_text
            for phrase in repeated
        )
        if not is_hallucinated:
            clean_chunks.append(chunk)

    log.info(
        "_clean_hallucinated_transcript: cleaned transcript — "
        "%d/%d sentences kept, %d/%d chunks kept",
        len(clean_sentences), len(sentences),
        len(clean_chunks), len(chunks)
    )

    return clean_text, clean_chunks


class TranscriptionService:
    """
    Manages ffmpeg conversion, Whisper model lifecycle, and sample auto-transcription.
    Whisper is loaded lazily — only when transcription work is needed —
    and unloaded immediately after to free memory.
    """

    def __init__(self, whisper_model_dir: Path, samples_dir: Path) -> None:
        self._whisper_dir  = whisper_model_dir
        self._samples_dir  = samples_dir
        self._originals_dir = samples_dir / "originals"
        self._available    = False   # True once we've confirmed the model dir exists
        self._ffmpeg_ok    = False   # Set by check_availability()

    def check_availability(self) -> bool:
        """
        Check ffmpeg and Whisper availability.
        Called once at startup. Returns True if transcription is available.
        """
        # ffmpeg check — independent of Whisper
        self._ffmpeg_ok = check_ffmpeg()

        # Whisper check
        if not self._whisper_dir.exists():
            log.warning(
                "Whisper model directory not found: %s — "
                "samples without .ref.txt will not be auto-transcribed. "
                "Download from https://huggingface.co/openai/whisper-large-v3-turbo "
                "and place files in %s",
                self._whisper_dir, self._whisper_dir,
            )
            self._available = False
            return False

        if not (self._whisper_dir / "config.json").exists():
            log.warning(
                "Whisper model directory exists but appears incomplete "
                "(config.json not found): %s — auto-transcription disabled.",
                self._whisper_dir,
            )
            self._available = False
            return False

        log.info("Whisper model found at %s — auto-transcription enabled", self._whisper_dir)
        self._available = True
        return True

    async def scan_and_transcribe(self) -> int:
        """
        Scan the samples directory:
          1. Convert any CONVERTIBLE_EXTENSION files to WAV via ffmpeg
          2. Transcribe any WAV files missing a .ref.txt sidecar via Whisper
        Returns the total number of files processed (converted + transcribed) this pass.
        Runs in a thread pool executor to avoid blocking the event loop.
        """
        loop = asyncio.get_event_loop()
        count = await loop.run_in_executor(None, self._scan_sync)
        return count

    # ── Internals ─────────────────────────────────────────────────────────────

    def _scan_sync(self) -> int:
        """Synchronous scan — runs in thread pool."""
        total = 0

        # Step 1: Convert non-WAV files to WAV
        if self._ffmpeg_ok:
            total += self._convert_pending()

        # Step 2: Transcribe WAV files missing .ref.txt
        if self._available:
            total += self._transcribe_pending()

        return total

    def _convert_pending(self) -> int:
        """Find and convert any CONVERTIBLE_EXTENSION files to WAV.
        Scans both the top-level samples directory and one level of subdirectories.
        """
        if not self._samples_dir.exists():
            return 0

        # Collect all locations to scan: top level + subdirectories
        scan_dirs = [self._samples_dir]
        for entry in sorted(self._samples_dir.iterdir()):
            if entry.is_dir() and entry.name != "originals":
                scan_dirs.append(entry)

        pending = []
        for scan_dir in scan_dirs:
            for path in sorted(scan_dir.iterdir()):
                if not path.is_file():
                    continue
                if path.suffix.lower() not in CONVERTIBLE_EXTENSIONS:
                    continue
                if not _VALID_STEM_RE.match(path.stem):
                    log.warning(
                        "Skipping conversion — invalid filename "
                        "(use underscores/hyphens only): %s", path.name
                    )
                    continue
                clean_stem = path.stem.replace(" ", "_")
                wav_path = path.parent / (clean_stem + ".wav")
                if wav_path.exists():
                    log.debug("WAV already exists for %s — skipping", path.name)
                    continue
                pending.append(path)

        count = 0
        for src in pending:
            if self._convert_to_wav(src):
                count += 1

        return count

    def _convert_to_wav(self, src: Path) -> bool:
        """
        Convert src to a 16kHz mono PCM_16 WAV file alongside it.
        On success, move src to the originals directory.
        Spaces in the source filename are replaced with underscores in the output.
        Returns True on success.
        """
        # Sanitize output stem — replace spaces with underscores so the result
        # passes the _VALID_STEM_RE check and appears in the sample scanner.
        clean_stem = src.stem.replace(" ", "_")
        dst = src.parent / (clean_stem + ".wav")
        self._originals_dir.mkdir(parents=True, exist_ok=True)

        log.info("Converting '%s' → '%s'", src.name, dst.name)

        try:
            result = subprocess.run(
                [
                    "ffmpeg", "-y",           # overwrite output if exists
                    "-i", str(src),           # input file
                    "-vn",                    # strip video track
                    "-ac", str(_WAV_CHANNELS),
                    "-ar", str(_WAV_SAMPLE_RATE),
                    "-acodec", _WAV_CODEC,
                    str(dst),
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )

            if result.returncode != 0:
                log.error(
                    "ffmpeg conversion failed for '%s': %s",
                    src.name, result.stderr[-500:] if result.stderr else "no output"
                )
                return False

            # Add silence padding to the converted WAV before any downstream use
            _pad_wav_silence(dst)

            # Move original to originals directory
            archive_path = self._originals_dir / src.name
            shutil.move(str(src), str(archive_path))
            log.info(
                "Converted '%s' → '%s' (original archived to originals/)",
                src.name, dst.name,
            )
            return True

        except subprocess.TimeoutExpired:
            log.error("ffmpeg timed out converting '%s'", src.name)
            return False
        except Exception as e:
            log.error("ffmpeg conversion error for '%s': %s", src.name, e)
            return False

    def _transcribe_pending(self) -> int:
        """Find WAV files missing .ref.txt and transcribe them.
        Scans both the top-level samples directory and one level of subdirectories.
        Only transcribes master files (stem matches parent directory name) —
        provider/variant clips get their ref.txt from the extractor, not Whisper.
        """
        if not self._samples_dir.exists():
            return 0

        # Collect master WAV files: top-level flat files and subdir masters
        scan_paths = []
        for entry in sorted(self._samples_dir.iterdir()):
            if entry.name == "originals":
                continue
            if entry.is_file():
                if entry.suffix.lower() in VALID_EXTENSIONS and _VALID_STEM_RE.match(entry.stem):
                    scan_paths.append(entry)
            elif entry.is_dir():
                for ext in (".wav", ".mp3", ".flac", ".ogg"):
                    master = entry / (entry.name + ext)
                    if master.exists():
                        scan_paths.append(master)
                        break

        pending = []
        for path in scan_paths:
            if path.suffix.lower() not in VALID_EXTENSIONS:
                continue
            if not _VALID_STEM_RE.match(path.stem):
                continue
            ref_txt = path.with_name(path.stem + ".ref.txt")
            if not ref_txt.exists():
                try:
                    import soundfile as sf
                    info = sf.info(str(path))
                    if info.duration > MAX_SAMPLE_DURATION_SEC:
                        log.warning(
                            "Sample '%s' rejected — duration %.0fs exceeds %.0fs (%.0f min) limit. Trim and re-add. Writing .ref.txt stub to prevent repeated rejection.",
                            path.name, info.duration, MAX_SAMPLE_DURATION_SEC, MAX_SAMPLE_DURATION_SEC / 60,
                        )
                        stub = path.with_name(path.stem + ".ref.txt")
                        stub.write_text(
                            f"[REJECTED: duration {info.duration:.0f}s exceeds {MAX_SAMPLE_DURATION_SEC:.0f}s limit — trim and re-add]",
                            encoding="utf-8",
                        )
                        continue
                except Exception:
                    pass
                pending.append(path)

        if not pending:
            return 0

        log.info(
            "Transcription: found %d sample(s) missing .ref.txt — starting Whisper",
            len(pending),
        )

        pipe = self._load_whisper()
        if pipe is None:
            return 0

        count = 0
        generated_retranscribe_jobs: list[tuple[list, str, str]] = []
        try:
            master_batch_size = max(1, int(_MASTER_BATCH_SIZE or 1))
            log.info(
                "Master transcription batching enabled — batch_size=%d pending=%d",
                master_batch_size,
                len(pending),
            )

            batch_start = 0
            while batch_start < len(pending):
                current_batch_size = max(1, min(master_batch_size, len(pending) - batch_start))
                master_batch = pending[batch_start:batch_start + current_batch_size]

                try:
                    batch_results = self._transcribe_many_static(
                        pipe,
                        master_batch,
                        language_hint="en",
                        batch_size=len(master_batch),
                    )
                    batch_paths = master_batch
                except Exception as e:
                    if len(master_batch) > 1 and _is_cuda_oom_error(e):
                        log.warning(
                            "Master transcription batch hit CUDA OOM at batch_size=%d — falling back to serial for remaining %d master file(s)",
                            len(master_batch),
                            len(pending) - batch_start,
                        )
                        _log_cuda_memory("Master transcription OOM:")
                        try:
                            import torch
                            if torch.cuda.is_available():
                                torch.cuda.empty_cache()
                        except Exception:
                            pass
                        master_batch_size = 1
                        continue

                    if len(master_batch) > 1:
                        log.warning(
                            "Master transcription batch failed at batch_size=%d — retrying serial for this batch: %s",
                            len(master_batch), e
                        )
                        batch_results = []
                        batch_paths = []
                        for audio_path in master_batch:
                            try:
                                batch_results.append(self._transcribe_one(pipe, audio_path))
                                batch_paths.append(audio_path)
                            except Exception as inner:
                                log.error("Failed to transcribe '%s': %s", audio_path.name, inner)
                    else:
                        audio_path = master_batch[0]
                        log.error("Failed to transcribe '%s': %s", audio_path.name, e)
                        batch_start += 1
                        continue

                for audio_path, (transcript, language, chunks) in zip(batch_paths, batch_results):
                    try:
                        if not transcript:
                            log.warning("Transcription returned empty result for: %s", audio_path.name)
                            continue

                        ref_txt = audio_path.with_name(audio_path.stem + ".ref.txt")
                        _write_sidecar_with_debug(
                            ref_txt,
                            transcript,
                            kind="master_ref",
                            source_audio=audio_path,
                        )
                        log.info(
                            "Transcribed '%s': %s",
                            audio_path.name,
                            transcript[:80] + ("..." if len(transcript) > 80 else ""),
                        )

                        profile = None
                        txt_sidecar = audio_path.with_name(audio_path.stem + ".txt")
                        if not txt_sidecar.exists():
                            profile = profile_voice(
                                audio_path,
                                transcript=transcript,
                                language=language,
                            )
                            if profile:
                                _write_sidecar_with_debug(
                                    txt_sidecar,
                                    profile,
                                    kind="master_profile",
                                    source_audio=audio_path,
                                )
                                log.info("Voice profile written for '%s': %s", audio_path.name, profile)
                        else:
                            try:
                                profile = txt_sidecar.read_text(encoding="utf-8").strip()
                            except Exception:
                                pass

                        if profile:
                            prefix = _gender_prefix(profile)
                            if prefix:
                                renamed_audio = _apply_gender_prefix(audio_path, prefix)
                                if renamed_audio != audio_path:
                                    audio_path = renamed_audio
                                    ref_txt = audio_path.with_name(audio_path.stem + ".ref.txt")
                                    txt_sidecar = audio_path.with_name(audio_path.stem + ".txt")

                        extracted_profile = profile
                        if chunks:
                            try:
                                import soundfile as sf
                                master_info = sf.info(str(audio_path))
                                extraction = extract_clips(
                                    audio_path,
                                    chunks=chunks or [],
                                    master_duration=float(master_info.duration),
                                )
                                if extraction and extraction.clips:
                                    generated_retranscribe_jobs.append((list(extraction.clips), language, audio_path.name))
                                    log.info(
                                        "Queued generated clip retranscription: master='%s' clips=%d queued_masters=%d",
                                        audio_path.name,
                                        len(extraction.clips),
                                        len(generated_retranscribe_jobs),
                                    )
                                    for clip in extraction.clips:
                                        clip_txt = clip.path.with_name(clip.path.stem + ".txt")
                                        if not clip_txt.exists():
                                            clip_profile = profile_voice(
                                                clip.path,
                                                transcript=clip.ref_text,
                                                language=language,
                                            )
                                            if clip_profile:
                                                _write_sidecar_with_debug(
                                                    clip_txt,
                                                    clip_profile,
                                                    kind="generated_profile",
                                                    source_audio=clip.path,
                                                )
                                                log.info(
                                                    "Voice profile written for clip '%s': %s",
                                                    clip.path.name, clip_profile
                                                )
                            except Exception as e:
                                log.warning(
                                    "Smart extraction failed for '%s' (non-fatal): %s",
                                    audio_path.name, e
                                )
                        else:
                            log.debug(
                                "No Whisper chunks for '%s' — smart extraction skipped",
                                audio_path.name
                            )

                        count += 1
                    except Exception as e:
                        log.error("Failed to process transcription result for '%s': %s", audio_path.name, e)

                batch_start += len(master_batch)

            if generated_retranscribe_jobs:
                total_clips = sum(len(clips) for clips, _language, _master_name in generated_retranscribe_jobs)
                log.info(
                    "Generated clip retranscription queue starting — master_files=%d clips=%d batch_size=%d",
                    len(generated_retranscribe_jobs),
                    total_clips,
                    _GENERATED_BATCH_SIZE,
                )
                for clips, language, master_name in generated_retranscribe_jobs:
                    log.info(
                        "Generated clip retranscription queue dispatch — master='%s' clips=%d",
                        master_name, len(clips)
                    )
                    _retranscribe_generated_clips(pipe, clips, language, batch_size=_GENERATED_BATCH_SIZE)
        finally:
            del pipe
            try:
                import torch
                if torch.cuda.is_available():
                    torch.cuda.empty_cache()
            except Exception:
                pass

        log.info("Transcription: completed %d file(s), Whisper unloaded", count)
        return count

    def _load_whisper(self):
        """
        Load the Whisper pipeline from the local model directory.
        Returns the pipeline on success, None on failure.
        Never downloads from HuggingFace.
        """
        try:
            import torch
            from transformers import AutoModelForSpeechSeq2Seq, AutoProcessor, pipeline
        except ImportError:
            log.error(
                "transformers or torch not installed — cannot run Whisper transcription. "
                "Install with: pip install transformers torch torchaudio"
            )
            return None

        try:
            device      = "cuda" if torch.cuda.is_available() else "cpu"
            torch_dtype = torch.float16 if torch.cuda.is_available() else torch.float32

            log.info("Loading Whisper from %s (device=%s)", self._whisper_dir, device)

            model = AutoModelForSpeechSeq2Seq.from_pretrained(
                str(self._whisper_dir),
                dtype=torch_dtype,           # 'dtype' replaces deprecated 'torch_dtype'
                low_cpu_mem_usage=True,
                use_safetensors=True,
                local_files_only=True,       # NEVER download from HuggingFace
            )
            model.to(device)

            processor = AutoProcessor.from_pretrained(
                str(self._whisper_dir),
                local_files_only=True,   # NEVER download from HuggingFace
            )

            pipe = pipeline(
                "automatic-speech-recognition",
                model=model,
                tokenizer=processor.tokenizer,
                feature_extractor=processor.feature_extractor,
                dtype=torch_dtype,       # 'dtype' replaces deprecated 'torch_dtype'
                device=device,
            )

            # Patch generation_config to suppress hallucination and silence warnings.
            gc = getattr(model, "generation_config", None)
            if gc is not None:
                # Prevents "I'm sorry. I'm sorry." looping — model no longer
                # conditions each chunk on its own previously hallucinated output.
                gc.condition_on_previous_text = False

                # Clear suppress-token lists that the model's generation_config
                # carries from training. When language="en" + task="transcribe"
                # are passed as generate_kwargs, the pipeline rebuilds these lists
                # from scratch — having them pre-set in generation_config causes
                # duplicate SuppressTokensLogitsProcessor warnings. Clearing them
                # here lets the pipeline own them without conflict.
                gc.suppress_tokens     = None
                gc.begin_suppress_tokens = None

                # Clear forced_decoder_ids — deprecated and replaced by
                # language/task flags. Leaving it set causes the
                # "forced_decoder_ids is deprecated" warning.
                gc.forced_decoder_ids  = None

                log.debug("Whisper generation_config patched")

            log.info("Whisper loaded successfully")
            return pipe

        except Exception as e:
            log.error("Failed to load Whisper from %s: %s", self._whisper_dir, e)
            return None

    def _transcribe_one(self, pipe, audio_path: Path) -> tuple[str, str, list]:
        return self._transcribe_one_static(pipe, audio_path, language_hint="en")

    @staticmethod
    def _transcribe_many_static(pipe, audio_paths: list[Path], language_hint: str = "en", batch_size: int = 1, return_timestamps: bool | str = "word") -> list[tuple[str, str, list]]:
        import soundfile as sf

        audio_inputs = []
        for audio_path in audio_paths:
            raw_audio_data, sample_rate = sf.read(str(audio_path), dtype='float32')
            channels = raw_audio_data.shape[1] if getattr(raw_audio_data, 'ndim', 1) > 1 else 1
            audio_data = raw_audio_data.mean(axis=1) if channels > 1 else raw_audio_data
            log.info(
                "Whisper input: audio='%s' sample_rate=%d samples=%d channels=%d",
                audio_path, sample_rate, len(audio_data), channels
            )
            audio_inputs.append({"array": audio_data, "sampling_rate": sample_rate})

        generate_kwargs = {"task": "transcribe"}
        if language_hint:
            generate_kwargs["language"] = language_hint

        _log_cuda_memory("Whisper batch start:")
        results = pipe(
            audio_inputs,
            return_timestamps=return_timestamps,
            chunk_length_s=30,
            batch_size=max(1, int(batch_size or 1)),
            ignore_warning=True,
            generate_kwargs=generate_kwargs,
        )
        if isinstance(results, dict):
            results = [results]

        processed = []
        for audio_path, result in zip(audio_paths, results):
            text = result.get("text", "").strip()
            language = result.get("language", "")
            chunks = result.get("chunks", []) if return_timestamps else []
            text, chunks = _clean_hallucinated_transcript(text, chunks)
            log.info(
                "Whisper output: audio='%s' chars=%d chunks=%d text='%s'",
                audio_path, len(text), len(chunks), text[:160] + ("..." if len(text) > 160 else "")
            )
            if language:
                log.info("Whisper detected language '%s' for '%s'", language, audio_path.name)
            log.debug("Whisper chunks for '%s': %d chunk(s)", audio_path.name, len(chunks))
            processed.append((text, language, chunks))

        _log_cuda_memory("Whisper batch end:")
        return processed

    @staticmethod
    def _transcribe_one_static(pipe, audio_path: Path, language_hint: str = "en") -> tuple[str, str, list]:
        """
        Transcribe a single audio file.
        Returns (transcript, language, chunks).

        chunks is result["chunks"] — a list of dicts with word/phrase-level
        timestamps: [{"text": "...", "timestamp": [start_sec, end_sec]}, ...]
        These are passed to sample_extractor for smart clip extraction.

        Audio is pre-loaded with soundfile to bypass torchcodec which has
        ffmpeg shared library conflicts on some systems.
        """
        return TranscriptionService._transcribe_many_static(
            pipe, [audio_path], language_hint=language_hint, batch_size=1, return_timestamps="word"
        )[0]
