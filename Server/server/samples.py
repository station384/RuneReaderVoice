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

# Provider suffixes appended to extracted clips — these files are internal
# and hidden from the client-visible sample list. The server resolves them
# based on the active provider when building synthesis requests.
PROVIDER_SUFFIXES = ("f5", "chatterbox")

# Variant mode suffixes produced by the smart extractor
VARIANT_SUFFIXES = ("loud", "quiet", "fast", "slow", "breathy")

# Regex matching ONLY provider-specific internal clips — hidden from clients.
# Variants (loud/quiet/fast/slow/breathy) are user-visible and must NOT match.
#   Hidden:  <base>-f5, <base>-chatterbox
#   Hidden:  <base>-loud-f5, <base>-quiet-f5  (F5-trimmed variant)
#   Visible: <base>-loud, <base>-quiet, <base>-fast, <base>-slow, <base>-breathy
_INTERNAL_SUFFIX_RE = re.compile(
    r"-(?:f5|chatterbox)$"
    r"|"
    r"-(?:loud|quiet|fast|slow|breathy)-f5$"
)


@dataclass
class SampleInfo:
    sample_id:        str
    filename:         str
    duration_seconds: float
    description:      str = ""
    ref_text:         str = ""   # verbatim transcript — required for F5-TTS synthesis


def _scan_directory(samples_dir: Path, directory: Path) -> list[SampleInfo]:
    """
    Scan a single directory for valid master sample files.
    The directory name is the sample_id (e.g. M_Christopher_Walken/).
    Only the master file (stem == directory name) is returned as a visible sample.
    Provider and variant clips are internal and filtered out.
    """
    results = []
    dir_stem = directory.name

    if not _VALID_STEM_RE.match(dir_stem):
        log.warning(
            "Sample directory skipped — invalid name "
            "(use underscores/hyphens only): %s", directory.name
        )
        return results

    # Look for the master file: <dir>/<stem>.<ext>
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        path = directory / f"{dir_stem}{ext}"
        if not path.exists():
            continue

        duration = _read_duration(path)
        if duration is None:
            log.warning("Sample skipped — could not read duration: %s", path)
            continue

        description = _load_sidecar(path, ".txt")
        ref_text    = _load_sidecar(path, ".ref.txt")

        if not ref_text:
            log.debug(
                "Sample '%s' skipped — no .ref.txt sidecar yet",
                dir_stem,
            )
            return results

        results.append(SampleInfo(
            sample_id=dir_stem,
            filename=path.name,
            duration_seconds=round(duration, 2),
            description=description,
            ref_text=ref_text,
        ))
        break  # found master, stop looking

    # Also expose variant clips (loud/quiet/fast/slow/breathy) as separate entries
    for path in sorted(directory.iterdir()):
        if not path.is_file():
            continue
        if path.suffix.lower() not in VALID_EXTENSIONS:
            continue

        stem = path.stem
        if stem == dir_stem:
            continue  # already handled as master above

        # Skip internal provider clips
        if _INTERNAL_SUFFIX_RE.search(stem):
            continue

        duration = _read_duration(path)
        if duration is None:
            continue

        description = _load_sidecar(path, ".txt")
        ref_text    = _load_sidecar(path, ".ref.txt")

        if not ref_text:
            continue

        results.append(SampleInfo(
            sample_id=stem,
            filename=path.name,
            duration_seconds=round(duration, 2),
            description=description,
            ref_text=ref_text,
        ))

    return results


def scan(samples_dir: Path) -> list[SampleInfo]:
    """
    Scan samples_dir and return a list of valid SampleInfo objects.
    Each subdirectory is treated as one sample group — the directory name
    is the base sample_id. Files directly in samples_dir are also scanned
    for backward compatibility during transition.
    Called on each GET /samples request — always reflects current directory state.
    """
    if not samples_dir.exists():
        log.debug("Samples directory does not exist: %s", samples_dir)
        return []

    results: list[SampleInfo] = []

    for entry in sorted(samples_dir.iterdir()):
        # Skip the originals archive directory
        if entry.name == "originals":
            continue

        if entry.is_dir():
            # Subdirectory — scan as a sample group
            results.extend(_scan_directory(samples_dir, entry))

        elif entry.is_file():
            # Flat file — legacy/transition support
            if entry.suffix.lower() not in VALID_EXTENSIONS:
                continue
            stem = entry.stem
            if not _VALID_STEM_RE.match(stem):
                continue
            if _INTERNAL_SUFFIX_RE.search(stem):
                continue
            duration = _read_duration(entry)
            if duration is None:
                continue
            description = _load_sidecar(entry, ".txt")
            ref_text    = _load_sidecar(entry, ".ref.txt")
            if not ref_text:
                continue
            results.append(SampleInfo(
                sample_id=stem,
                filename=entry.name,
                duration_seconds=round(duration, 2),
                description=description,
                ref_text=ref_text,
            ))

    log.debug("Sample scan: found %d valid sample(s) in %s", len(results), samples_dir)
    return results


def scan_for_provider(samples_dir: Path, provider_id: str) -> list[SampleInfo]:
    """
    Like scan() but returns provider-aware duration and description.

    When a provider-specific extracted clip exists (e.g. am_onyx-f5.wav),
    the reported duration_seconds reflects that clip rather than the master.
    This ensures the client duration filter (F5 <=11s, Chatterbox <=40s)
    operates on the actual clip that will be used for synthesis, not the
    potentially much longer master file.

    The sample_id and filename in the response always refer to the master
    so the client never needs to know about provider-specific files.
    """
    pid = provider_id.lower()
    if pid.startswith("f5"):
        suffix = "f5"
    elif pid.startswith("chatterbox"):
        suffix = "chatterbox"
    else:
        suffix = None

    base_results = scan(samples_dir)
    if suffix is None:
        return base_results

    adjusted = []
    for info in base_results:
        # Check if a provider-specific clip exists.
        # Checks subdirectory layout first, then flat layout.
        # Handles both base stems and variant stems.
        found_provider_clip = False
        for candidate in _provider_clip_search_paths(samples_dir, info.sample_id, suffix):
            if candidate.exists():
                clip_duration = _read_duration(candidate)
                if clip_duration is not None:
                    clip_description = _load_sidecar(candidate, ".txt") or info.description
                    adjusted.append(SampleInfo(
                        sample_id=info.sample_id,
                        filename=info.filename,
                        duration_seconds=round(clip_duration, 2),
                        description=clip_description,
                        ref_text=info.ref_text,
                    ))
                    log.debug(
                        "scan_for_provider: '%s' reporting provider clip duration "
                        "%.1fs (master=%.1fs) for provider '%s'",
                        info.sample_id, clip_duration, info.duration_seconds, provider_id
                    )
                    found_provider_clip = True
                    break

        if not found_provider_clip:
            adjusted.append(info)

    return adjusted


def _sample_search_paths(samples_dir: Path, sample_id: str):
    """
    Yield candidate audio file paths for a sample_id, checking the
    subdirectory layout first then the flat layout for compatibility.

    Master:  samples_dir/<sample_id>/<sample_id>.<ext>
    Variant: samples_dir/<base_stem>/<sample_id>.<ext>  (variants live in base subdir)
    Flat:    samples_dir/<sample_id>.<ext>               (legacy)
    """
    base = _base_stem(sample_id)
    # Base subdirectory (covers both master and variant files)
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / base / f"{sample_id}{ext}"
    # Flat fallback
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / f"{sample_id}{ext}"


def resolve_sample(samples_dir: Path, sample_id: str) -> Optional[SampleInfo]:
    """
    Find the full SampleInfo for a given sample_id.
    Checks subdirectory layout first, falls back to flat layout.
    Returns None if not found.
    """
    for candidate in _sample_search_paths(samples_dir, sample_id):
        if candidate.exists():
            duration = _read_duration(candidate)
            if duration is None:
                return None
            description = _load_sidecar(candidate, ".txt")
            ref_text    = _load_sidecar(candidate, ".ref.txt")
            return SampleInfo(
                sample_id=sample_id,
                filename=candidate.name,
                duration_seconds=round(duration, 2),
                description=description,
                ref_text=ref_text,
            )
    return None


def resolve_sample_path(samples_dir: Path, sample_id: str) -> Optional[Path]:
    """
    Return just the audio file path for a sample_id, or None.
    Checks subdirectory layout first, falls back to flat layout.
    """
    for candidate in _sample_search_paths(samples_dir, sample_id):
        if candidate.exists():
            return candidate
    return None


def _provider_suffix(provider_id: str) -> Optional[str]:
    """Derive the provider clip suffix from a provider_id."""
    pid = provider_id.lower()
    if pid.startswith("f5"):
        return "f5"
    elif pid.startswith("chatterbox"):
        return "chatterbox"
    return None


def _base_stem(sample_id: str) -> str:
    """
    Extract the base stem from a sample_id by stripping any variant suffix.
    Variant files live in the base sample's subdirectory, not their own.

    M_WWZ_10-fast    -> M_WWZ_10
    M_WWZ_10-quiet   -> M_WWZ_10
    M_WWZ_10         -> M_WWZ_10
    M_Christopher_Walken-breathy -> M_Christopher_Walken
    """
    for mode in ("loud", "quiet", "fast", "slow", "breathy"):
        if sample_id.endswith(f"-{mode}"):
            return sample_id[: -(len(mode) + 1)]
    return sample_id


def _provider_clip_search_paths(samples_dir: Path, sample_id: str, suffix: str):
    """
    Yield candidate paths for a provider-specific clip, checking
    subdirectory layout first then flat layout.

    Variant clips (M_WWZ_10-fast) live in the BASE sample's subdirectory
    (M_WWZ_10/), not their own. The base stem is extracted to locate the
    correct subdirectory.
    """
    base = _base_stem(sample_id)
    # Subdirectory of the base sample (covers both master and variant clips)
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / base / f"{sample_id}-{suffix}{ext}"
    # Flat fallback
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / f"{sample_id}-{suffix}{ext}"


def resolve_sample_path_for_provider(
    samples_dir: Path,
    sample_id: str,
    provider_id: str,
) -> Optional[Path]:
    """
    Return the best audio file path for a given sample_id and provider.
    Checks subdirectory layout first, falls back to flat layout.
    """
    suffix = _provider_suffix(provider_id)
    if suffix:
        for candidate in _provider_clip_search_paths(samples_dir, sample_id, suffix):
            if candidate.exists():
                log.debug(
                    "resolve_sample_path_for_provider: using '%s' for '%s'",
                    candidate.name, sample_id
                )
                return candidate
    return resolve_sample_path(samples_dir, sample_id)


def resolve_sample_for_provider(
    samples_dir: Path,
    sample_id: str,
    provider_id: str,
) -> Optional["SampleInfo"]:
    """
    Like resolve_sample() but prefers the provider-specific extracted clip.
    Returns SampleInfo with the ref_text from the provider-specific sidecar
    when available, falling back to the master ref_text.
    Checks subdirectory layout first, falls back to flat layout.
    """
    suffix = _provider_suffix(provider_id)
    if suffix:
        for candidate in _provider_clip_search_paths(samples_dir, sample_id, suffix):
            if candidate.exists():
                duration = _read_duration(candidate)
                if duration is None:
                    continue
                description = _load_sidecar(candidate, ".txt")
                ref_text    = _load_sidecar(candidate, ".ref.txt")
                if ref_text:
                    log.debug(
                        "resolve_sample_for_provider: using '%s'", candidate
                    )
                    # Store the full path relative to samples_dir so the backend
                    # can reconstruct the absolute path correctly regardless of
                    # whether the file is in a subdirectory or flat layout.
                    try:
                        rel = candidate.relative_to(samples_dir)
                    except ValueError:
                        rel = candidate  # use as-is if can't make relative
                    return SampleInfo(
                        sample_id=sample_id,
                        filename=str(rel),   # e.g. "M_WWZ_10/M_WWZ_10-f5.wav"
                        duration_seconds=round(duration, 2),
                        description=description,
                        ref_text=ref_text,
                    )

    return resolve_sample(samples_dir, sample_id)


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
    