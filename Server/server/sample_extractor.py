# SPDX-License-Identifier: GPL-3.0-or-later
# server/sample_extractor.py
#
# Smart reference audio extractor.
#
# Given a master WAV file and Whisper word-level timestamp chunks, extracts
# provider-optimised clips and optional speech-mode variants (loud, quiet,
# fast, slow, breathy).
#
# ── Output file naming ────────────────────────────────────────────────────────
#
#   <stem>-f5.wav                  ← F5-TTS default clip  (≤8s speech + silence pad)
#   <stem>-f5.ref.txt              ← transcript of F5 default clip
#   <stem>-f5.txt                  ← voice profile of F5 default clip
#   <stem>-f5-<mode>.wav           ← F5 variant (f5-loud / f5-slow / f5-fast etc.)
#   <stem>-f5-<mode>.ref.txt
#   <stem>-f5-<mode>.txt
#   <stem>-chatterbox.wav          ← Chatterbox default clip (≤38s + silence pad, ALWAYS)
#   <stem>-chatterbox.ref.txt      ← transcript of Chatterbox default clip
#   <stem>-chatterbox.txt          ← voice profile of Chatterbox default clip
#   <stem>-chatterbox-<mode>.wav   ← Chatterbox variant (chatterbox-loud / chatterbox-slow etc.)
#   <stem>-chatterbox-<mode>.ref.txt
#   <stem>-chatterbox-<mode>.txt
#
# Client-visible sample_ids (no provider suffix, no provider prefix on variants):
#   <stem>              → server resolves to -f5 or -chatterbox
#   <stem>-<mode>       → server resolves to -f5-<mode> or -chatterbox-<mode>
#
# The master WAV is renamed to <stem>-master.wav after extraction so it is
# invisible to clients (PROVIDER_SUFFIXES filter only shows -f5 / -chatterbox
# files; the scanner hides everything else). All files are built from the
# master directly — variants do NOT build on each other.
#
# IMPORTANT: Every clip (default and variant) gets silence padding:
#   - 0.5s lead silence before speech
#   - 1.0s tail silence after speech
# This is required by F5-TTS for correct pacing. Without lead silence the
# ODE solver rushes the first syllables.
#
# Provider-specific files (-f5, -chatterbox) are INTERNAL — the sample scanner
# filters them from the client-visible list. Clients always request the base
# stem; the server resolves to the right file for the active provider.
#
# ── Variant detection thresholds ─────────────────────────────────────────────
#
#   A variant is only emitted when its acoustic signature differs from the
#   Normal baseline by a meaningful margin AND enough contiguous speech exists
#   to fill the target duration. Conservative thresholds mean fewer false
#   positives — better to produce nothing than a misleading label.
#
#   loud:    mean RMS > baseline * 1.40  (40% louder)
#   quiet:   mean RMS < baseline * 0.60  (40% quieter)
#   fast:    mean WPM > baseline * 1.30  (30% faster)
#   slow:    mean WPM < baseline * 0.65  (35% slower)
#   breathy: spectral flatness > 0.06 AND RMS < baseline * 0.75
#
# ── Gender prefix ─────────────────────────────────────────────────────────────
#
#   The caller (transcriber.py) owns gender detection and already writes it
#   into the .txt profile. The extractor does NOT rename files — it writes
#   all output alongside the master using the master's stem. Renaming with
#   M_/F_/U_ prefix is a post-intake human step or can be automated by the
#   caller if desired.

from __future__ import annotations

import logging
import subprocess
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)

# ── Provider duration targets (seconds) ───────────────────────────────────────

F5_TARGET_SEC          = 8.0    # ideal F5 clip length — leaves room for silence pad
F5_MAX_SEC             = 9.0    # hard upper bound for F5 (silence pad added after)
CHATTERBOX_TARGET_SEC  = 35.0   # ideal Chatterbox clip length
CHATTERBOX_MAX_SEC     = 38.0   # hard upper bound for Chatterbox

# ── Variant detection thresholds ──────────────────────────────────────────────

_LOUD_RMS_RATIO    = 1.40   # 40% above baseline RMS
_QUIET_RMS_RATIO   = 0.60   # 40% below baseline RMS
_FAST_WPM_RATIO    = 1.30   # 30% above baseline WPM
_SLOW_WPM_RATIO    = 0.65   # 35% below baseline WPM
_BREATHY_FLATNESS  = 0.06   # spectral flatness threshold
_BREATHY_RMS_RATIO = 0.75   # must also be quieter than baseline
_MIN_VARIANT_DIFF  = True   # gate: skip variant if insufficient material

# Minimum contiguous speech seconds required to emit a variant
_MIN_VARIANT_SEC   = 6.0

# Window size and step for rolling acoustic analysis (seconds)
_WINDOW_SEC        = 4.0
_STEP_SEC          = 0.5


# ── Public data types ─────────────────────────────────────────────────────────

@dataclass
class WhisperChunk:
    """Single Whisper timestamp chunk: one word or short phrase."""
    text:  str
    start: float   # seconds
    end:   float   # seconds


@dataclass
class ExtractedClip:
    """Result of a single extraction."""
    path:     Path
    ref_path: Path
    start:    float
    end:      float
    label:    str    # "f5", "chatterbox", "loud", "quiet", "fast", "slow", "breathy"
    ref_text: str


@dataclass
class ExtractionResult:
    """All clips produced from one master file."""
    master_path:         Path
    clips:               list[ExtractedClip] = field(default_factory=list)
    master_renamed_path: Path | None = None   # set after master is renamed to -master.wav


# ── Public entry point ────────────────────────────────────────────────────────

def extract_clips(
    master_path: Path,
    chunks: list[dict],          # raw Whisper chunk dicts: {"text":..., "timestamp":[s,e]}
    master_duration: float,
    emit_variants: bool = True,
) -> ExtractionResult:
    """
    Main entry point. Called by transcriber after Whisper completes.

    master_path     : path to the already-converted master WAV
    chunks          : Whisper result["chunks"] — word/phrase timestamps
    master_duration : total audio duration in seconds
    emit_variants   : set False to skip variant detection (Normal clips only)

    Returns ExtractionResult with all clips written to disk alongside master.
    """
    result = ExtractionResult(master_path=master_path)

    if not chunks:
        log.warning("extract_clips: no Whisper chunks for '%s' — skipping", master_path.name)
        return result

    # Parse chunks into typed objects
    wchunks = _parse_chunks(chunks)
    if not wchunks:
        log.warning("extract_clips: could not parse chunks for '%s'", master_path.name)
        return result

    # Secondary duration gate — transcriber should have caught this first,
    # but guard here too in case extract_clips is called directly.
    if master_duration > 3600.0:
        log.error(
            "extract_clips: '%s' duration=%.0fs exceeds 3600s (60 min) limit — "
            "skipping to prevent OOM. Trim and re-add.",
            master_path.name, master_duration
        )
        return result

    log.info(
        "extract_clips: '%s' duration=%.1fs chunks=%d",
        master_path.name, master_duration, len(wchunks)
    )

    # ── Load audio for acoustic analysis ──────────────────────────────────────
    try:
        import numpy as np
        import librosa
        y, sr = librosa.load(str(master_path), sr=None, mono=True)
    except Exception as e:
        log.error("extract_clips: failed to load audio '%s': %s", master_path.name, e)
        return result

    # ── Build rolling window metrics ──────────────────────────────────────────
    windows = _compute_windows(y, sr, wchunks, master_duration)
    if not windows:
        log.warning("extract_clips: no windows computed for '%s'", master_path.name)
        return result

    # ── Compute baseline (median across all windows) ──────────────────────────
    import numpy as np
    baseline_rms      = float(np.median([w["rms"]      for w in windows]))
    baseline_wpm      = float(np.median([w["wpm"]      for w in windows if w["wpm"] > 0]))
    baseline_flatness = float(np.median([w["flatness"] for w in windows]))

    log.info(
        "extract_clips: baseline rms=%.4f wpm=%.1f flatness=%.4f",
        baseline_rms, baseline_wpm, baseline_flatness
    )

    # ── Extract normal region (best speech-dense segment) ────────────────────
    # The normal region is found once and used as a reference for both F5 and
    # Chatterbox default clips, AND as an overlap gate for variant detection.
    # All clips are extracted directly from the master — nothing builds on another.

    normal_f5_region = _find_best_region(
        windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
        mode="normal", baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
    )

    if normal_f5_region is None:
        log.warning("extract_clips: could not find normal region in '%s'", master_path.name)
        return result

    # ── F5 default clip ───────────────────────────────────────────────────────
    # Always written. Max 8s speech + silence pad → ~10s total.
    f5_clip = _write_clip(master_path, normal_f5_region, wchunks, "f5", y, sr)
    if f5_clip:
        result.clips.append(f5_clip)
        log.info(
            "extract_clips: wrote F5 default '%s' (%.1f–%.1fs, %d chars ref)",
            f5_clip.path.name, f5_clip.start, f5_clip.end, len(f5_clip.ref_text)
        )

    # ── Chatterbox default clip ───────────────────────────────────────────────
    # ALWAYS written — even if the master is short enough to use directly.
    # Having an explicit -chatterbox.wav ensures provider resolution is
    # unambiguous and the file always has the correct silence padding.
    # Max 38s speech + silence pad → ~40s total.
    normal_cb_region = _find_best_region(
        windows, wchunks, CHATTERBOX_TARGET_SEC, CHATTERBOX_MAX_SEC,
        mode="normal", baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
    )
    # If master is shorter than Chatterbox target, use whatever is available
    if normal_cb_region is None:
        normal_cb_region = _find_best_region(
            windows, wchunks, min(master_duration, CHATTERBOX_TARGET_SEC),
            min(master_duration, CHATTERBOX_MAX_SEC),
            mode="normal", baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
    if normal_cb_region:
        cb_clip = _write_clip(master_path, normal_cb_region, wchunks, "chatterbox", y, sr)
        if cb_clip:
            result.clips.append(cb_clip)
            log.info(
                "extract_clips: wrote Chatterbox default '%s' (%.1f–%.1fs)",
                cb_clip.path.name, cb_clip.start, cb_clip.end
            )
    else:
        log.warning(
            "extract_clips: could not find Chatterbox region for '%s' — skipping",
            master_path.name
        )

    # ── Variant detection ─────────────────────────────────────────────────────
    # For each detected speech mode, write BOTH a Chatterbox variant (-mode-chatterbox)
    # and an F5 variant (-mode-f5). Both are extracted directly from the master.
    # Variant names must carry the provider suffix so the server resolves correctly.
    if not emit_variants:
        # Rename master to -master.wav to hide it from client sample lists
        result.master_renamed_path = _rename_master(master_path)
        log.info(
            "extract_clips: '%s' complete — %d clip(s) written",
            master_path.name, len(result.clips)
        )
        return result

    variants_to_check = [
        ("loud",    _is_loud_window),
        ("quiet",   _is_quiet_window),
        ("fast",    _is_fast_window),
        ("slow",    _is_slow_window),
        ("breathy", _is_breathy_window),
    ]

    for mode, predicate in variants_to_check:
        variant_windows = [
            w for w in windows
            if predicate(w, baseline_rms, baseline_wpm, baseline_flatness)
        ]
        if not variant_windows:
            continue

        # ── F5 variant ────────────────────────────────────────────────────────
        f5_region = _find_best_region(
            variant_windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
            mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
        if f5_region is None:
            log.debug("extract_clips: variant '%s'-f5 — insufficient material", mode)
        else:
            overlap = _region_overlap(normal_f5_region, f5_region)
            if overlap > 0.5:
                log.debug(
                    "extract_clips: variant '%s'-f5 overlaps %.0f%% with normal — skipping",
                    mode, overlap * 100
                )
                f5_region = None

        if f5_region:
            f5_var = _write_clip(master_path, f5_region, wchunks, f"f5-{mode}", y, sr)
            if f5_var:
                result.clips.append(f5_var)
                log.info(
                    "extract_clips: wrote F5 variant '%s' (%.1f–%.1fs)",
                    f5_var.path.name, f5_var.start, f5_var.end
                )

        # ── Chatterbox variant ────────────────────────────────────────────────
        cb_region = _find_best_region(
            variant_windows, wchunks, CHATTERBOX_TARGET_SEC, CHATTERBOX_MAX_SEC,
            mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
        if cb_region is None:
            # Not enough material for Chatterbox length — fall back to F5 length
            cb_region = _find_best_region(
                variant_windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
                mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
            )
        if cb_region is None:
            log.debug("extract_clips: variant '%s'-chatterbox — insufficient material", mode)
            continue

        overlap = _region_overlap(normal_cb_region, cb_region)
        if overlap > 0.5:
            log.debug(
                "extract_clips: variant '%s'-chatterbox overlaps %.0f%% with normal — skipping",
                mode, overlap * 100
            )
            continue

        # Variant is named <mode>-chatterbox so the server resolves it correctly
        cb_var = _write_clip(master_path, cb_region, wchunks, f"chatterbox-{mode}", y, sr)
        if cb_var:
            result.clips.append(cb_var)
            log.info(
                "extract_clips: wrote Chatterbox variant '%s' (%.1f–%.1fs)",
                cb_var.path.name, cb_var.start, cb_var.end
            )

    # ── Rename master to hide from client sample lists ────────────────────────
    # The master WAV has served its purpose. Renaming to -master.wav keeps it
    # on disk for diagnostics but the sample scanner will not expose it
    # (only -f5 and -chatterbox files are provider-visible).
    result.master_renamed_path = _rename_master(master_path)

    log.info(
        "extract_clips: '%s' complete — %d clip(s) written",
        master_path.name, len(result.clips)
    )
    return result


# ── Master renaming ──────────────────────────────────────────────────────────

def _rename_master(master_path: Path) -> Path | None:
    """
    Rename the master WAV (and its sidecars) from <stem>.wav to
    <stem>-master.wav so it is invisible to the client sample scanner.

    The sample scanner only surfaces -f5 and -chatterbox files. The -master
    suffix is not in PROVIDER_SUFFIXES so it will never be exposed, but the
    file remains on disk for diagnostics and manual inspection.

    Sidecars renamed alongside the WAV:
      <stem>.ref.txt  → <stem>-master.ref.txt
      <stem>.txt      → <stem>-master.txt

    Returns the new path of the renamed WAV, or None if rename failed.
    """
    stem   = master_path.stem
    parent = master_path.parent

    new_wav = parent / f"{stem}-master.wav"
    if new_wav.exists():
        log.debug("_rename_master: '%s' already exists — skipping rename", new_wav.name)
        return new_wav

    try:
        master_path.rename(new_wav)
        log.info("_rename_master: '%s' → '%s'", master_path.name, new_wav.name)

        for ext in (".ref.txt", ".txt"):
            old_sidecar = parent / f"{stem}{ext}"
            new_sidecar = parent / f"{stem}-master{ext}"
            if old_sidecar.exists() and not new_sidecar.exists():
                old_sidecar.rename(new_sidecar)
                log.debug("_rename_master: sidecar '%s' → '%s'",
                          old_sidecar.name, new_sidecar.name)

        return new_wav
    except Exception as e:
        log.warning("_rename_master: failed to rename '%s': %s", master_path.name, e)
        return None


# ── Prose quality scoring ────────────────────────────────────────────────────

def _prose_ratio(chunks: list["WhisperChunk"]) -> float:
    """
    Return the fraction of words in chunks that are natural prose —
    i.e. do NOT look like techno-babble codes, isolated numbers, or
    identifier tokens.

    A word is considered "code-like" if it:
      - Contains both letters and digits (R2D2, T-800, CD6, 3028A)
      - Is a standalone number that isn't a natural prose number
        (years 1800-2100 are allowed; zero-padded times like 0900 are not)
      - Is a hyphenated token containing digits (T-800, XJ-9B)

    Returns 0.0 if there are no words, 1.0 if all words are natural prose.
    A region with prose_ratio < _MIN_PROSE_RATIO is rejected as a candidate.
    """
    import re

    if not chunks:
        return 0.0

    all_words = []
    for c in chunks:
        all_words.extend(c.text.split())

    if not all_words:
        return 0.0

    code_count = 0
    for word in all_words:
        # Strip leading/trailing punctuation for analysis
        w = re.sub(r"[.,!?;:()\[\]'\"]", '', word)
        if not w:
            continue
        # Hyphenated token with mixed letters+digits: T-800, XJ-9B, CD6-3028
        if re.search(r'-', w) and re.search(r'[A-Za-z]', w) and re.search(r'\d', w):
            code_count += 1
            continue
        # Unhyphenated mixed token: R2D2, C3PO, T800 (but not ordinals like 1st/42nd)
        if re.search(r'[A-Za-z]', w) and re.search(r'\d', w):
            if not re.match(r'^\d+(st|nd|rd|th)$', w):
                code_count += 1
                continue
        # Zero-padded times/codes: 0900, 0045
        if re.match(r'^0\d{2,3}$', w):
            code_count += 1
            continue
        # Standalone 4-digit non-year numbers
        if re.match(r'^\d{4}$', w):
            n = int(w)
            if not (1800 <= n <= 2100):
                code_count += 1
                continue
        # Standalone 5+ digit numbers (always codes)
        if re.match(r'^\d{5,}$', w):
            code_count += 1
            continue

    return 1.0 - (code_count / len(all_words))


# Minimum acceptable prose ratio for a candidate region.
# Windows below this threshold are penalized; regions below it are rejected.
_MIN_PROSE_RATIO = 0.85   # at least 85% of words must be natural prose


def _has_code_words(chunks: list["WhisperChunk"]) -> bool:
    """
    Return True if ANY word in chunks is a code-like token.

    A single code word in a region (e.g. "CD6" or "-3028") is enough to
    disqualify it — even if the surrounding prose is otherwise clean.
    The audio contains the unpronounced/mispronounced sound regardless of
    how many clean words surround it, and F5-TTS will mis-time around it.

    Detected patterns:
      - Hyphenated mixed letter+digit: T-800, CD6-3028, XJ-9B
      - Unhyphenated mixed: R2D2, C3PO, T800  (ordinals 1st/2nd exempt)
      - Zero-padded times/codes: 0900, 0045
    """
    import re
    for c in chunks:
        for word in c.text.split():
            w = word.strip().strip(".,!?;:()[]'\"")
            if not w:
                continue
            if re.search(r'-', w) and re.search(r'[A-Za-z]', w) and re.search(r'\d', w):
                return True
            if re.search(r'[A-Za-z]', w) and re.search(r'\d', w):
                if not re.match(r'^\d+(st|nd|rd|th)$', w):
                    return True
            if re.match(r'^0\d{2,3}$', w):
                return True
    return False


# ── Window computation ────────────────────────────────────────────────────────

def _compute_windows(y, sr, chunks: list[WhisperChunk], duration: float) -> list[dict]:
    """
    Compute rolling acoustic windows over the audio.
    Each window records: start, end, rms, wpm, flatness, speech_density.
    """
    import numpy as np
    import librosa

    windows = []
    t = 0.0
    while t + _WINDOW_SEC <= duration:
        t_end = t + _WINDOW_SEC

        # Extract audio samples for this window
        s_start = int(t * sr)
        s_end   = int(t_end * sr)
        segment = y[s_start:s_end]

        if len(segment) == 0:
            t += _STEP_SEC
            continue

        # RMS energy
        rms = float(np.sqrt(np.mean(segment ** 2)))

        # Spectral flatness
        flatness = float(np.mean(librosa.feature.spectral_flatness(y=segment)))

        # WPM from Whisper chunks that fall within this window
        window_chunks = [
            c for c in chunks
            if c.start >= t and c.end <= t_end
        ]
        word_count = sum(len(c.text.split()) for c in window_chunks)
        speech_sec = sum(c.end - c.start for c in window_chunks)
        speech_density = speech_sec / _WINDOW_SEC  # 0.0–1.0
        wpm = (word_count / _WINDOW_SEC) * 60.0 if word_count > 0 else 0.0

        # Prose quality: fraction of words that are natural speech (not codes/numbers)
        prose     = _prose_ratio(window_chunks)
        has_codes = _has_code_words(window_chunks)

        windows.append({
            "start":          t,
            "end":            t_end,
            "rms":            rms,
            "flatness":       flatness,
            "wpm":            wpm,
            "speech_density": speech_density,
            "word_count":     word_count,
            "prose_ratio":    prose,
            "has_codes":      has_codes,
        })
        t += _STEP_SEC

    return windows


# ── Region finder ─────────────────────────────────────────────────────────────

def _find_best_region(
    windows: list[dict],
    chunks: list[WhisperChunk],
    target_sec: float,
    max_sec: float,
    mode: str,
    baseline_rms: float,
    baseline_wpm: float,
) -> Optional[tuple[float, float]]:
    """
    Find the best contiguous time region of approximately target_sec duration
    from the given windows. Returns (start_sec, end_sec) snapped to word
    boundaries, or None if no suitable region found.

    Scoring: speech_density weighted most heavily, with a secondary preference
    for ending on a sentence boundary (. ! ?).
    """
    if not windows:
        return None

    import numpy as np

    # Score each window
    scored = []
    for w in windows:
        # Primary: speech density (we want dense, natural speech)
        score = w["speech_density"] * 10.0

        # Bonus for ending on sentence boundary
        end_time = w["end"]
        ending_chunks = [c for c in chunks if abs(c.end - end_time) < 1.0]
        if any(c.text.rstrip().endswith((".", "!", "?", ",")) for c in ending_chunks):
            score += 2.0

        # Penalty for very low RMS (near-silence)
        if w["rms"] < 0.005:
            score -= 5.0

        # Hard penalty for windows containing ANY code-like token (T-800, CD6-3028).
        # Even one code word disqualifies a window — the audio will contain
        # unpronounced/mispronounced sounds regardless of surrounding prose.
        if w.get("has_codes", False):
            score -= 50.0   # large enough to push below any clean window

        # Additional scaled penalty for high code density
        prose = w.get("prose_ratio", 1.0)
        if prose < _MIN_PROSE_RATIO:
            score -= (1.0 - prose) * 20.0

        scored.append((score, w))

    scored.sort(key=lambda x: -x[0])

    # Try each candidate window as an anchor, expand to target_sec.
    # After expansion, check the prose ratio of the full region — reject and
    # try the next candidate if the region is still code-heavy.
    for _, anchor in scored[:20]:  # try top 20 candidates
        region = _expand_region(anchor["start"], target_sec, max_sec, chunks, windows)
        if region is None:
            continue

        # Gate: reject any region that contains code-like tokens in its chunks.
        # A single T-800 or CD6-3028 in the audio is enough to corrupt F5 pacing.
        start, end = region
        region_chunks = [c for c in chunks if c.start >= start - 0.1 and c.end <= end + 0.1]
        if _has_code_words(region_chunks):
            log.debug(
                "_find_best_region: region %.1f–%.1fs rejected (contains code words)",
                start, end
            )
            continue   # try next candidate

        # Also check overall prose ratio
        prose = _prose_ratio(region_chunks)
        if prose < _MIN_PROSE_RATIO:
            log.debug(
                "_find_best_region: region %.1f–%.1fs rejected (prose_ratio=%.2f < %.2f)",
                start, end, prose, _MIN_PROSE_RATIO
            )
            continue   # try next candidate

        return region

    return None


def _expand_region(
    anchor_start: float,
    target_sec: float,
    max_sec: float,
    chunks: list[WhisperChunk],
    all_windows: list[dict],
) -> Optional[tuple[float, float]]:
    """
    Expand from anchor_start to target_sec, snapping start/end to word
    boundaries. Returns None if insufficient speech material available.
    """
    target_end = anchor_start + target_sec

    # Find the word chunk closest to anchor_start — snap start to word begin
    start_chunk = _nearest_chunk_start(chunks, anchor_start)
    if start_chunk is None:
        return None

    actual_start = start_chunk.start

    # Walk forward collecting chunks until we reach target duration
    end_chunk = None
    last_sentence_end_chunk = None

    for c in chunks:
        if c.start < actual_start:
            continue
        if c.end - actual_start > max_sec:
            break
        end_chunk = c
        if c.text.rstrip().endswith((".", "!", "?")):
            last_sentence_end_chunk = c

    if end_chunk is None:
        return None

    # Prefer ending on a sentence boundary if it's within reach
    if last_sentence_end_chunk is not None:
        candidate_end = last_sentence_end_chunk.end
        if candidate_end - actual_start >= _MIN_VARIANT_SEC:
            return (actual_start, min(candidate_end, actual_start + max_sec))

    # Fall back to word boundary nearest target
    actual_end = end_chunk.end
    if actual_end - actual_start < _MIN_VARIANT_SEC:
        return None

    return (actual_start, min(actual_end, actual_start + max_sec))


def _nearest_chunk_start(chunks: list[WhisperChunk], target_time: float) -> Optional[WhisperChunk]:
    """Return the chunk whose start time is nearest to target_time."""
    if not chunks:
        return None
    return min(chunks, key=lambda c: abs(c.start - target_time))


# ── Variant predicates ────────────────────────────────────────────────────────

def _is_loud_window(w, baseline_rms, baseline_wpm, baseline_flatness) -> bool:
    return w["rms"] > baseline_rms * _LOUD_RMS_RATIO and w["speech_density"] > 0.3

def _is_quiet_window(w, baseline_rms, baseline_wpm, baseline_flatness) -> bool:
    return (w["rms"] < baseline_rms * _QUIET_RMS_RATIO and
            w["rms"] > 0.002 and w["speech_density"] > 0.2)

def _is_fast_window(w, baseline_rms, baseline_wpm, baseline_flatness) -> bool:
    return w["wpm"] > baseline_wpm * _FAST_WPM_RATIO and w["speech_density"] > 0.4

def _is_slow_window(w, baseline_rms, baseline_wpm, baseline_flatness) -> bool:
    return (w["wpm"] > 0 and
            w["wpm"] < baseline_wpm * _SLOW_WPM_RATIO and
            w["speech_density"] > 0.2)

def _is_breathy_window(w, baseline_rms, baseline_wpm, baseline_flatness) -> bool:
    return (w["flatness"] > _BREATHY_FLATNESS and
            w["rms"] < baseline_rms * _BREATHY_RMS_RATIO and
            w["speech_density"] > 0.2)


# ── Overlap check ─────────────────────────────────────────────────────────────

def _region_overlap(r1: Optional[tuple], r2: Optional[tuple]) -> float:
    """Return fractional overlap between two (start, end) regions. 0.0–1.0."""
    if r1 is None or r2 is None:
        return 0.0
    overlap_start = max(r1[0], r2[0])
    overlap_end   = min(r1[1], r2[1])
    if overlap_end <= overlap_start:
        return 0.0
    overlap_sec = overlap_end - overlap_start
    shorter = min(r1[1] - r1[0], r2[1] - r2[0])
    return overlap_sec / shorter if shorter > 0 else 0.0


# ── Audio extraction ──────────────────────────────────────────────────────────

# ── Silence padding ──────────────────────────────────────────────────────────
#
# Every extracted clip receives fixed lead and tail silence padding.
# Silence in the source is NEVER trimmed — it is intentional and required.
#
#   Lead pad: 0.5s — gives F5's ODE solver a silence runway before the voice
#             starts. Without this, F5 rushes the first syllables.
#   Tail pad: 1.0s — gives the vocoder a clean anchor at the end.
#
# Chatterbox minimum: clips shorter than CHATTERBOX_MIN_SEC total duration
# are padded with extra tail silence to reach the minimum. Chatterbox
# produces poor quality on very short reference clips.

_LEAD_PAD_SEC         = 0.5    # silence prepended before speech (all clips)
_TAIL_PAD_SEC         = 1.0    # silence appended after speech (all clips)
CHATTERBOX_MIN_SEC    = 10.0   # minimum total clip duration for Chatterbox clips


def _pad_clip(segment, sr: int, label: str) -> "np.ndarray":
    """
    Add fixed lead and tail silence to a clip segment.
    Silence in the source is NEVER trimmed.

    For Chatterbox clips (label starts with "chatterbox"), ensures the
    total output is at least CHATTERBOX_MIN_SEC by extending the tail
    silence if necessary.
    """
    import numpy as np

    if len(segment) == 0:
        return segment

    lead = np.zeros(int(_LEAD_PAD_SEC * sr), dtype=np.float32)
    tail = np.zeros(int(_TAIL_PAD_SEC * sr), dtype=np.float32)

    padded = np.concatenate([lead, segment, tail])

    # Chatterbox minimum length enforcement
    is_chatterbox = label.startswith("chatterbox")
    if is_chatterbox:
        min_samples = int(CHATTERBOX_MIN_SEC * sr)
        if len(padded) < min_samples:
            extra = np.zeros(min_samples - len(padded), dtype=np.float32)
            padded = np.concatenate([padded, extra])
            log.debug(
                "_pad_clip: extended chatterbox clip '%s' to %.1fs minimum",
                label, CHATTERBOX_MIN_SEC
            )

    return padded


def _write_clip(
    master_path: Path,
    region: tuple[float, float],
    chunks: list[WhisperChunk],
    label: str,
    y,
    sr: int,
) -> Optional[ExtractedClip]:
    """
    Extract audio samples from y[sr*start:sr*end], write as WAV,
    write ref.txt sidecar. Returns ExtractedClip or None on failure.
    """
    import numpy as np
    import soundfile as sf

    start, end = region
    s_start = int(start * sr)
    s_end   = int(end   * sr)

    if s_end <= s_start:
        return None

    segment = y[s_start:s_end]
    if len(segment) == 0:
        return None

    # Add lead/tail silence padding. Source silence is NEVER trimmed.
    # Chatterbox clips are extended to CHATTERBOX_MIN_SEC if shorter.
    import numpy as np
    segment = _pad_clip(segment, sr, label)
    if len(segment) == 0:
        return None

    # Output paths
    stem     = master_path.stem
    out_wav  = master_path.parent / f"{stem}-{label}.wav"
    out_ref  = master_path.parent / f"{stem}-{label}.ref.txt"

    # Build ref text from chunks that fall within this region
    ref_chunks = [
        c for c in chunks
        if c.start >= start - 0.05 and c.end <= end + 0.05
    ]
    ref_text = " ".join(c.text.strip() for c in ref_chunks).strip()
    if not ref_text:
        log.warning("_write_clip: no ref text for label='%s' region=%.1f-%.1f", label, start, end)
        return None

    # Ensure ref text ends with punctuation (F5 requirement)
    if ref_text and not ref_text[-1] in ".!?,":
        ref_text += "."

    try:
        sf.write(str(out_wav), segment, sr, subtype="PCM_16")
        out_ref.write_text(ref_text, encoding="utf-8")
    except Exception as e:
        log.error("_write_clip: failed to write '%s': %s", out_wav.name, e)
        return None

    return ExtractedClip(
        path=out_wav,
        ref_path=out_ref,
        start=start,
        end=end,
        label=label,
        ref_text=ref_text,
    )


# ── Chunk parsing ─────────────────────────────────────────────────────────────

def _parse_chunks(raw_chunks: list[dict]) -> list[WhisperChunk]:
    """
    Parse raw Whisper chunk dicts into typed WhisperChunk objects.
    Handles both word-level {"text", "timestamp": [s, e]} and
    segment-level formats.
    """
    result = []
    for c in raw_chunks:
        try:
            text      = c.get("text", "").strip()
            timestamp = c.get("timestamp", [])
            if not text or not timestamp or len(timestamp) < 2:
                continue
            start = float(timestamp[0]) if timestamp[0] is not None else 0.0
            end   = float(timestamp[1]) if timestamp[1] is not None else start + 0.5
            if end <= start:
                end = start + 0.1
            result.append(WhisperChunk(text=text, start=start, end=end))
        except Exception:
            continue
    return result
