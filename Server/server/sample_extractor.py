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
#   <stem>.wav                ← master (already exists, untouched)
#   <stem>-f5.wav             ← F5-TTS clip  (≤9s speech + 80ms silence pad)
#   <stem>-f5.ref.txt         ← exact transcript of the F5 clip
#   <stem>-chatterbox.wav     ← Chatterbox clip (≤38s, only if master > 38s)
#   <stem>-chatterbox.ref.txt ← exact transcript of the Chatterbox clip
#   <stem>-<mode>.wav         ← variant clips (loud/quiet/fast/slow/breathy)
#   <stem>-<mode>.ref.txt     ← transcript of variant clip
#   <stem>-<mode>-f5.wav      ← F5-trimmed version of a variant
#   <stem>-<mode>-f5.ref.txt  ← transcript of F5-trimmed variant
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
MASTER_DIRECT_MAX_SEC  = 38.0   # if master ≤ this, no Chatterbox clip needed

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
    master_path: Path
    clips:       list[ExtractedClip] = field(default_factory=list)


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
    if master_duration > 1200.0:
        log.error(
            "extract_clips: '%s' duration=%.0fs exceeds 1200s (20 min) limit — "
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

    # ── Extract Normal clip (best speech-dense segment) ───────────────────────
    normal_region = _find_best_region(
        windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
        mode="normal", baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
    )

    if normal_region is None:
        log.warning("extract_clips: could not find normal region in '%s'", master_path.name)
        return result

    # Write F5 clip (trimmed to F5_MAX_SEC)
    f5_clip = _write_clip(master_path, normal_region, wchunks, "f5", y, sr)
    if f5_clip:
        result.clips.append(f5_clip)
        log.info(
            "extract_clips: wrote F5 clip '%s' (%.1f–%.1fs, %d chars ref)",
            f5_clip.path.name, f5_clip.start, f5_clip.end, len(f5_clip.ref_text)
        )

    # Write Chatterbox clip only if master exceeds direct-use threshold
    if master_duration > MASTER_DIRECT_MAX_SEC:
        cb_region = _find_best_region(
            windows, wchunks, CHATTERBOX_TARGET_SEC, CHATTERBOX_MAX_SEC,
            mode="normal", baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
        if cb_region:
            cb_clip = _write_clip(master_path, cb_region, wchunks, "chatterbox", y, sr)
            if cb_clip:
                result.clips.append(cb_clip)
                log.info(
                    "extract_clips: wrote Chatterbox clip '%s' (%.1f–%.1fs)",
                    cb_clip.path.name, cb_clip.start, cb_clip.end
                )

    # ── Variant detection ─────────────────────────────────────────────────────
    if not emit_variants:
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

        # Extract at Chatterbox target size first — richer voice cloning material.
        # F5-trimmed version always written alongside from the same region start.
        cb_region = _find_best_region(
            variant_windows, wchunks, CHATTERBOX_TARGET_SEC, CHATTERBOX_MAX_SEC,
            mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
        if cb_region is None:
            # Fall back to F5 target if not enough material for Chatterbox length
            cb_region = _find_best_region(
                variant_windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
                mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
            )
        if cb_region is None:
            log.debug("extract_clips: variant '%s' — insufficient material", mode)
            continue

        # Gate: skip if region overlaps heavily with normal baseline
        overlap = _region_overlap(normal_region, cb_region)
        if overlap > 0.5:
            log.debug(
                "extract_clips: variant '%s' overlaps %.0f%% with normal — skipping",
                mode, overlap * 100
            )
            continue

        # Write Chatterbox-length variant clip (the canonical variant file)
        variant_clip = _write_clip(master_path, cb_region, wchunks, mode, y, sr)
        if variant_clip:
            result.clips.append(variant_clip)
            duration = variant_clip.end - variant_clip.start
            log.info(
                "extract_clips: wrote variant '%s' clip '%s' (%.1f–%.1fs = %.1fs)",
                mode, variant_clip.path.name, variant_clip.start, variant_clip.end, duration
            )

        # Always write F5-trimmed version from the start of the same region.
        # Even if the variant is already short enough for F5, having an explicit
        # -f5 file makes provider resolution unambiguous and consistent.
        f5_region = _find_best_region(
            variant_windows, wchunks, F5_TARGET_SEC, F5_MAX_SEC,
            mode=mode, baseline_rms=baseline_rms, baseline_wpm=baseline_wpm,
        )
        if f5_region:
            f5_variant_clip = _write_clip(
                master_path, f5_region, wchunks, f"{mode}-f5", y, sr
            )
            if f5_variant_clip:
                result.clips.append(f5_variant_clip)
                log.info(
                    "extract_clips: wrote F5 variant '%s' clip '%s' (%.1f–%.1fs)",
                    mode, f5_variant_clip.path.name,
                    f5_variant_clip.start, f5_variant_clip.end
                )

    log.info(
        "extract_clips: '%s' complete — %d clip(s) written",
        master_path.name, len(result.clips)
    )
    return result


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

        windows.append({
            "start":          t,
            "end":            t_end,
            "rms":            rms,
            "flatness":       flatness,
            "wpm":            wpm,
            "speech_density": speech_density,
            "word_count":     word_count,
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

        scored.append((score, w))

    scored.sort(key=lambda x: -x[0])

    # Try each candidate window as an anchor, expand to target_sec
    for _, anchor in scored[:20]:  # try top 20 candidates
        region = _expand_region(anchor["start"], target_sec, max_sec, chunks, windows)
        if region is not None:
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

# ── Silence trim and pad ─────────────────────────────────────────────────────
#
# F5-TTS derives its output duration from ref_audio_len / ref_text_len ratio.
# Silence at the start or end of the reference clip inflates ref_audio_len
# without adding to ref_text_len — causing F5 to generate audio at 2-3x speed.
# Trimming silence from both ends and adding a fixed short pad gives F5 a
# clean voice-only reference with a predictable tail anchor.
#
# Silence threshold: -42 dBFS (matches F5's own preprocess_ref_audio_text)
# Tail pad: 80ms of silence appended after trimming

_SILENCE_THRESHOLD_DBFS = -42.0   # frames below this are considered silence
_TAIL_PAD_SEC           = 0.08    # 80ms silence appended after speech ends


def _trim_and_pad(segment, sr: int):
    """
    Trim leading and trailing silence from a numpy float32 audio segment,
    then append a short silence tail.

    This ensures:
      - ref_audio_len reflects actual speech duration (not silence padding)
      - F5's output duration estimate is accurate
      - The clip ends cleanly for vocoder anchoring
    """
    import numpy as np

    if len(segment) == 0:
        return segment

    # Convert threshold from dBFS to linear amplitude
    threshold = 10 ** (_SILENCE_THRESHOLD_DBFS / 20.0)

    # Find first and last non-silent sample
    abs_seg   = np.abs(segment)
    non_silent = np.where(abs_seg > threshold)[0]

    if len(non_silent) == 0:
        # Entirely silent — return as-is (caller will likely reject)
        return segment

    first = non_silent[0]
    last  = non_silent[-1] + 1

    trimmed = segment[first:last]

    # Append silence tail
    pad_samples = int(_TAIL_PAD_SEC * sr)
    tail        = np.zeros(pad_samples, dtype=np.float32)

    return np.concatenate([trimmed, tail])


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

    # Trim leading/trailing silence and add tail pad.
    # This prevents F5-TTS from generating at wrong speed due to silence
    # inflating ref_audio_len without contributing to ref_text_len.
    import numpy as np
    segment = _trim_and_pad(segment, sr)
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
