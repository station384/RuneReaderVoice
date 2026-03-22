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
_WAV_SAMPLE_RATE = 16000
_WAV_CHANNELS    = 1
_WAV_CODEC       = "pcm_s16le"   # PCM_16 — guarantees float32 from librosa


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
                # Only transcribe the master file (stem == dir name)
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
                # Duration gate — reject files longer than MAX_SAMPLE_DURATION_SEC
                # before attempting Whisper or librosa to avoid OOM and wasted time.
                try:
                    import soundfile as sf
                    info = sf.info(str(path))
                    if info.duration > MAX_SAMPLE_DURATION_SEC:
                        log.warning(
                            "Sample '%s' rejected — duration %.0fs exceeds "
                            "%.0fs (%.0f min) limit. Trim and re-add. "
                            "Writing .ref.txt stub to prevent repeated rejection.",
                            path.name, info.duration, MAX_SAMPLE_DURATION_SEC,
                            MAX_SAMPLE_DURATION_SEC / 60,
                        )
                        # Write a stub .ref.txt so the scanner doesn't retry
                        # this file on every poll cycle.
                        stub = path.with_name(path.stem + ".ref.txt")
                        stub.write_text(
                            f"[REJECTED: duration {info.duration:.0f}s exceeds "
                            f"{MAX_SAMPLE_DURATION_SEC:.0f}s limit — trim and re-add]",
                            encoding="utf-8",
                        )
                        continue
                except Exception:
                    pass  # Can't read duration — let Whisper try anyway
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
        try:
            for audio_path in pending:
                try:
                    transcript, language, chunks = self._transcribe_one(pipe, audio_path)
                    if transcript:
                        # Write .ref.txt transcript sidecar
                        ref_txt = audio_path.with_name(audio_path.stem + ".ref.txt")
                        ref_txt.write_text(transcript, encoding="utf-8")
                        log.info(
                            "Transcribed '%s': %s",
                            audio_path.name,
                            transcript[:80] + ("..." if len(transcript) > 80 else ""),
                        )

                        # Write .txt voice profile sidecar for master (only if not already present)
                        # profile is always resolved — either freshly generated or read from
                        # the existing sidecar — so the gender prefix step always has data.
                        profile = None
                        txt_sidecar = audio_path.with_name(audio_path.stem + ".txt")
                        if not txt_sidecar.exists():
                            profile = profile_voice(
                                audio_path,
                                transcript=transcript,
                                language=language,
                            )
                            if profile:
                                txt_sidecar.write_text(profile, encoding="utf-8")
                                log.info("Voice profile written for '%s': %s", audio_path.name, profile)
                        else:
                            # Read existing profile for gender detection
                            try:
                                profile = txt_sidecar.read_text(encoding="utf-8").strip()
                            except Exception:
                                pass

                        # Auto-apply gender prefix based on voice profile detection.
                        # Renames the WAV and all sidecars before extraction so that
                        # all extracted clips inherit the correct prefix.
                        # Skipped if stem already has M_/F_/U_ prefix (human-supplied).
                        if profile:
                            prefix = _gender_prefix(profile)
                            audio_path = _apply_gender_prefix(audio_path, prefix)

                        # Smart extraction — provider-specific clips and variants.
                        # Requires word-level timestamps from Whisper (chunks).
                        # Skipped gracefully if chunks are empty or extraction fails.
                        if chunks:
                            try:
                                import soundfile as sf
                                master_info     = sf.info(str(audio_path))
                                master_duration = master_info.duration
                                extraction      = extract_clips(
                                    master_path=audio_path,
                                    chunks=chunks,
                                    master_duration=master_duration,
                                    emit_variants=True,
                                )
                                # Write voice profile sidecar for each extracted clip
                                for clip in extraction.clips:
                                    clip_txt = clip.path.with_name(clip.path.stem + ".txt")
                                    if not clip_txt.exists():
                                        clip_profile = profile_voice(
                                            clip.path,
                                            transcript=clip.ref_text,
                                            language=language,
                                        )
                                        if clip_profile:
                                            clip_txt.write_text(clip_profile, encoding="utf-8")
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
                    else:
                        log.warning("Transcription returned empty result for: %s", audio_path.name)
                except Exception as e:
                    log.error("Failed to transcribe '%s': %s", audio_path.name, e)
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
                torch_dtype=torch_dtype,
                low_cpu_mem_usage=True,
                use_safetensors=True,
                local_files_only=True,   # NEVER download from HuggingFace
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
                torch_dtype=torch_dtype,
                device=device,
            )

            log.info("Whisper loaded successfully")
            return pipe

        except Exception as e:
            log.error("Failed to load Whisper from %s: %s", self._whisper_dir, e)
            return None

    def _transcribe_one(self, pipe, audio_path: Path) -> tuple[str, str, list]:
        """
        Transcribe a single audio file.
        Returns (transcript, language, chunks).

        chunks is result["chunks"] — a list of dicts with word/phrase-level
        timestamps: [{"text": "...", "timestamp": [start_sec, end_sec]}, ...]
        These are passed to sample_extractor for smart clip extraction.

        Language is auto-detected by Whisper — no forced language constraint
        so non-English samples transcribe correctly in their native language.

        Audio is pre-loaded with soundfile to bypass torchcodec which has
        ffmpeg shared library conflicts on some systems.
        """
        import soundfile as sf
        import numpy as np

        # Load audio with soundfile — always works, bypasses torchcodec
        audio_data, sample_rate = sf.read(str(audio_path), dtype='float32')
        if audio_data.ndim > 1:
            audio_data = audio_data.mean(axis=1)   # stereo → mono

        audio_input = {"array": audio_data, "sampling_rate": sample_rate}

        result = pipe(
            audio_input,
            return_timestamps="word",  # word-level timestamps for smart extraction
            chunk_length_s=30,         # process in 30s chunks for long-form audio
            ignore_warning=True,       # suppress seq2seq chunk_length_s warning — expected usage
        )
        text     = result.get("text", "").strip()
        language = result.get("language", "")
        chunks   = result.get("chunks", [])
        if language:
            log.info("Whisper detected language '%s' for '%s'", language, audio_path.name)
        log.debug("Whisper chunks for '%s': %d chunk(s)", audio_path.name, len(chunks))
        return text, language, chunks
