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

# Provider suffixes appended to extracted clips.
# All extracted clips carry one of these suffixes — no bare variant files exist.
#   <stem>-f5.wav                  — F5-TTS default
#   <stem>-chatterbox.wav          — Chatterbox default
#   <stem>-<mode>-f5.wav           — F5 variant (loud/quiet/fast/slow/breathy)
#   <stem>-<mode>-chatterbox.wav   — Chatterbox variant
#   <stem>-master.wav              — original master (hidden, kept for diagnostics)
PROVIDER_SUFFIXES = ("f5", "chatterbox")

# Variant mode names (without provider suffix)
VARIANT_SUFFIXES = ("loud", "quiet", "fast", "slow", "breathy")

# Regex matching ALL internal/hidden files — never exposed to clients.
# Format: provider prefix comes BEFORE mode suffix.
#   -f5              ← F5 default
#   -f5-loud         ← F5 loud variant
#   -chatterbox      ← Chatterbox default
#   -chatterbox-loud ← Chatterbox loud variant
#   -master          ← archived original
_INTERNAL_SUFFIX_RE = re.compile(
    r"-(?:f5|chatterbox)$"                                         # default clips
    r"|"
    r"-(?:f5|chatterbox)-(?:loud|quiet|fast|slow|breathy)$"        # variant clips
    r"|"
    r"-master$"                                                    # archived master
)


@dataclass
class SampleInfo:
    sample_id:        str
    filename:         str
    duration_seconds: float
    description:      str = ""
    ref_text:         str = ""   # verbatim transcript — required for F5-TTS synthesis


def _scan_directory_for_provider(directory: Path, dir_stem: str, suffix: str) -> list[SampleInfo]:
    """Scan one sample-family directory for a single provider family.

    Provider-tagged disk files are translated into provider-neutral public ids.
    Examples:
      F_NightElf-chatterbox.wav      -> sample_id F_NightElf
      F_NightElf-chatterbox-slow.wav -> sample_id F_NightElf-slow
    """
    results: list[SampleInfo] = []

    def public_id_for(stem: str) -> str | None:
        base_token = f"{dir_stem}-{suffix}"
        if stem == base_token:
            return dir_stem
        prefix = base_token + "-"
        if stem.startswith(prefix):
            variant = stem[len(prefix):]
            if variant in VARIANT_SUFFIXES:
                return f"{dir_stem}-{variant}"
        return None

    for path in sorted(directory.iterdir()):
        if not path.is_file() or path.suffix.lower() not in VALID_EXTENSIONS:
            continue
        public_id = public_id_for(path.stem)
        if not public_id:
            continue
        ref_text = _load_sidecar(path, ".ref.txt")
        if not ref_text:
            continue
        duration = _read_duration(path)
        if duration is None:
            continue
        description = _load_sidecar(path, ".txt")
        results.append(SampleInfo(
            sample_id=public_id,
            filename=path.name,
            duration_seconds=round(duration, 2),
            description=description,
            ref_text=ref_text,
        ))

    return results


def _scan_directory(samples_dir: Path, directory: Path) -> list[SampleInfo]:
    """
    Scan a single directory and return the union of all public sample ids found
    across provider-specific files.

    Disk naming is INTERNAL and provider-tagged:
        <stem>-f5.wav
        <stem>-f5-<variant>.wav
        <stem>-chatterbox.wav
        <stem>-chatterbox-<variant>.wav
        <stem>-master.wav

    Client-visible sample_ids are provider-neutral:
        <stem>
        <stem>-<variant>
    """
    results = []
    dir_stem = directory.name

    if not _VALID_STEM_RE.match(dir_stem):
        log.warning(
            "Sample directory skipped — invalid name "
            "(use underscores/hyphens only): %s", directory.name
        )
        return results

    seen_ids: set[str] = set()

    for suffix in PROVIDER_SUFFIXES:
        provider_entries = _scan_directory_for_provider(directory, dir_stem, suffix)
        for info in provider_entries:
            if info.sample_id in seen_ids:
                continue
            seen_ids.add(info.sample_id)
            results.append(info)

    return sorted(results, key=lambda s: s.sample_id.lower())


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
    Provider-aware sample scan.

    Disk files are provider-tagged, but the returned sample_id values are always
    provider-neutral public ids. For example, both of these map to client-facing
    ids without the provider token:
        F_NightElf-chatterbox.wav      -> F_NightElf
        F_NightElf-chatterbox-slow.wav -> F_NightElf-slow

    chatterbox_full is an alias of chatterbox and resolves against the same
    -chatterbox[-variant] files.
    """
    suffix = _provider_suffix(provider_id)
    if suffix is None:
        return scan(samples_dir)

    if not samples_dir.exists():
        return []

    results: list[SampleInfo] = []

    for entry in sorted(samples_dir.iterdir()):
        if entry.name == "originals":
            continue

        if entry.is_dir():
            dir_stem = entry.name
            if not _VALID_STEM_RE.match(dir_stem):
                continue
            results.extend(_scan_directory_for_provider(entry, dir_stem, suffix))
        elif entry.is_file():
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
            ref_text = _load_sidecar(entry, ".ref.txt")
            if not ref_text:
                continue
            results.append(SampleInfo(
                sample_id=stem,
                filename=entry.name,
                duration_seconds=round(duration, 2),
                description=description,
                ref_text=ref_text,
            ))

    log.debug("Provider sample scan: provider=%s found %d sample(s) in %s", provider_id, len(results), samples_dir)
    return results


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

    sample_id is always the CLIENT-VISIBLE id (no provider suffix):
        M_WWZ_10              -> M_WWZ_10
        M_WWZ_10-fast         -> M_WWZ_10
        M_WWZ_10-quiet        -> M_WWZ_10
        M_Christopher_Walken-breathy -> M_Christopher_Walken
    """
    for mode in VARIANT_SUFFIXES:
        if sample_id.endswith(f"-{mode}"):
            return sample_id[: -(len(mode) + 1)]
    return sample_id


def _provider_clip_search_paths(samples_dir: Path, sample_id: str, suffix: str):
    """
    Yield candidate paths for a provider-specific clip, checking
    subdirectory layout first then flat layout.

    Naming convention: provider prefix comes BEFORE mode suffix.
      sample_id "M_WWZ_10"       + suffix "f5"         → M_WWZ_10-f5.wav
      sample_id "M_WWZ_10-fast"  + suffix "f5"         → M_WWZ_10-f5-fast.wav
      sample_id "M_WWZ_10-loud"  + suffix "chatterbox" → M_WWZ_10-chatterbox-loud.wav

    For variants: strip the mode suffix from sample_id, insert provider,
    then re-append the mode.
    """
    base = _base_stem(sample_id)   # e.g. "M_WWZ_10" from "M_WWZ_10-fast"

    # Determine mode suffix (empty string for base sample_ids)
    mode = sample_id[len(base):]   # e.g. "-fast" or ""

    # Canonical path: base/base-{suffix}{mode}.ext
    # e.g. M_WWZ_10/M_WWZ_10-f5-fast.wav  or  M_WWZ_10/M_WWZ_10-f5.wav
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / base / f"{base}-{suffix}{mode}{ext}"
    # Flat fallback
    for ext in (".wav", ".mp3", ".flac", ".ogg"):
        yield samples_dir / f"{base}-{suffix}{mode}{ext}"


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
                log.info(
                    "resolve_sample_path_for_provider: sample_id='%s' provider='%s' audio='%s'",
                    sample_id, provider_id, candidate
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
                    log.info(
                        "resolve_sample_for_provider: sample_id='%s' provider='%s' audio='%s' ref='%s' chars=%d",
                        sample_id, provider_id, candidate, candidate.with_name(candidate.stem + '.ref.txt'), len(ref_text)
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
    