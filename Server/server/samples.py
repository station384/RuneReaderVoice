# SPDX-License-Identifier: GPL-3.0-or-later
# server/samples.py
#
# Reference sample directory scanner.
#
# Scans RRV_SAMPLES_DIR for audio files to use as voice matching references.
# Results are returned on GET /api/v1/providers/{id}/samples.
# The directory is re-scanned on each request — no restart needed to pick up new files.
#
# Naming convention (enforced):
#   - Underscores and hyphens only. No spaces. No other special characters.
#   - Valid extensions: .wav .mp3 .flac .ogg
#   - sample_id = filename stem exactly as written
#   - Files violating the convention are skipped with a warning logged
#
# Sidecars (both alongside the audio file):
#   <stem>.txt      — one-line human description shown in GET /samples responses
#   <stem>.ref.txt  — REQUIRED for F5-TTS: verbatim transcript of the spoken content.
#                     If absent, F5-TTS synthesis for this sample is refused with a
#                     clear error. The server NEVER downloads Whisper or any other
#                     model at runtime — all files must be pre-placed by the admin.
#
# Duration is read from the file header. Files that cannot be read are skipped.

from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)

VALID_EXTENSIONS = frozenset({".wav", ".mp3", ".flac", ".ogg"})
_VALID_STEM_RE = re.compile(r"^[A-Za-z0-9_-]+$")


@dataclass
class SampleInfo:
    sample_id:        str
    filename:         str
    duration_seconds: float
    description:      str = ""
    ref_text:         str = ""   # verbatim transcript — required for F5-TTS synthesis


def scan(samples_dir: Path) -> list[SampleInfo]:
    """
    Scan samples_dir and return a list of valid SampleInfo objects.
    Called on each GET /samples request — always reflects current directory state.
    """
    if not samples_dir.exists():
        log.debug("Samples directory does not exist: %s", samples_dir)
        return []

    results: list[SampleInfo] = []

    for path in sorted(samples_dir.iterdir()):
        if not path.is_file():
            continue
        if path.suffix.lower() not in VALID_EXTENSIONS:
            continue

        stem = path.stem

        if not _VALID_STEM_RE.match(stem):
            log.warning(
                "Sample skipped — invalid filename (use underscores/hyphens only, "
                "no spaces or special characters): %s",
                path.name,
            )
            continue

        duration = _read_duration(path)
        if duration is None:
            log.warning("Sample skipped — could not read duration: %s", path.name)
            continue

        description = _load_sidecar(path, ".txt")
        ref_text     = _load_sidecar(path, ".ref.txt")

        if not ref_text:
            log.debug(
                "Sample '%s' skipped — no .ref.txt transcript sidecar yet "
                "(transcription pending or not available)",
                stem,
            )
            continue

        results.append(SampleInfo(
            sample_id=stem,
            filename=path.name,
            duration_seconds=round(duration, 2),
            description=description,
            ref_text=ref_text,
        ))

    log.debug("Sample scan: found %d valid file(s) in %s", len(results), samples_dir)
    return results


def resolve_sample(samples_dir: Path, sample_id: str) -> Optional[SampleInfo]:
    """
    Find the full SampleInfo for a given sample_id.
    Returns None if not found.
    """
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        candidate = samples_dir / f"{sample_id}{ext}"
        if candidate.exists():
            duration = _read_duration(candidate)
            if duration is None:
                return None
            description = _load_sidecar(candidate, ".txt")
            ref_text     = _load_sidecar(candidate, ".ref.txt")
            return SampleInfo(
                sample_id=sample_id,
                filename=candidate.name,
                duration_seconds=round(duration, 2),
                description=description,
                ref_text=ref_text,
            )
    return None


def resolve_sample_path(samples_dir: Path, sample_id: str) -> Optional[Path]:
    """Return just the audio file path for a sample_id, or None."""
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        candidate = samples_dir / f"{sample_id}{ext}"
        if candidate.exists():
            return candidate
    return None


# ── Internals ─────────────────────────────────────────────────────────────────

def _read_duration(path: Path) -> Optional[float]:
    try:
        import soundfile as sf
        info = sf.info(str(path))
        return info.duration
    except Exception:
        pass
    try:
        import wave
        if path.suffix.lower() == ".wav":
            with wave.open(str(path), "rb") as wf:
                return wf.getnframes() / float(wf.getframerate())
    except Exception:
        pass
    return None


def _load_sidecar(audio_path: Path, suffix: str) -> str:
    """
    Load a sidecar file with the given suffix alongside the audio file.
    suffix examples: ".txt", ".ref.txt"
    Returns the first non-empty line, or "" if absent or empty.
    """
    sidecar = audio_path.with_name(audio_path.stem + suffix)
    if not sidecar.exists():
        return ""
    try:
        text = sidecar.read_text(encoding="utf-8").strip()
        first_line = text.splitlines()[0].strip() if text else ""
        return first_line
    except Exception as e:
        log.warning("Could not read sidecar %s: %s", sidecar.name, e)
        return ""
    