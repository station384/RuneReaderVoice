# SPDX-License-Identifier: GPL-3.0-or-later
# server/voice_profiler.py
#
# Automatic voice profile generator for reference audio samples.
#
# Analyses a WAV file using librosa signal processing and generates a
# human-readable one-line description written to the <stem>.txt sidecar.
#
# Analysis pipeline:
#   - Fundamental frequency (F0) via pyin → gender + vocal register
#   - Spectral centroid → tone color (dark/warm/bright/piercing)
#   - Harmonic-to-noise ratio → texture (smooth/clear/rough/gravelly)
#   - RMS energy → dynamic energy level (soft/moderate/strong/powerful)
#   - Words-per-minute from Whisper transcript → speaking pace
#   - Duration from audio file
#   - Language from Whisper result
#
# All analysis uses librosa which is already in the venv as an f5-tts
# dependency. No additional packages required.
#
# Example output:
#   Male · Baritone · Warm tone · Smooth · Moderate energy · English · Slow pace · 8.3s
#   Female · Soprano · Bright tone · Clear · Soft energy · English · Fast pace · 6.1s

from __future__ import annotations

import logging
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)


def profile_voice(
    audio_path: Path,
    transcript: str = "",
    language: str = "",
) -> str:
    """
    Analyse audio_path and return a one-line voice description string.
    transcript and language are from Whisper output — used for pace and
    language label. Both are optional; omitting them produces a partial
    description.

    Returns empty string on failure so the caller can decide whether to
    write a sidecar or skip it.
    """
    try:
        import numpy as np
        import librosa

        y, sr = librosa.load(str(audio_path), sr=None, mono=True)
        duration = len(y) / sr

        parts = []

        # ── Gender + Register (from F0) ──────────────────────────────────────
        gender, register = _analyse_pitch(y, sr)
        parts.append(gender)
        parts.append(register)

        # ── Tone color (from spectral centroid) ──────────────────────────────
        tone = _analyse_tone(y, sr)
        parts.append(tone)

        # ── Texture (from HNR approximation) ─────────────────────────────────
        texture = _analyse_texture(y, sr)
        parts.append(texture)

        # ── Energy (from RMS) ─────────────────────────────────────────────────
        energy = _analyse_energy(y)
        parts.append(energy)

        # ── Language ──────────────────────────────────────────────────────────
        if language:
            parts.append(_format_language(language))

        # ── Speaking pace (from WPM) ──────────────────────────────────────────
        if transcript and duration > 0:
            pace = _analyse_pace(transcript, duration)
            parts.append(pace)

        # ── Duration ──────────────────────────────────────────────────────────
        parts.append(f"{duration:.1f}s")

        description = " · ".join(parts)
        log.info("Voice profile for '%s': %s", audio_path.name, description)
        return description

    except Exception as e:
        log.warning("Voice profiling failed for '%s': %s", audio_path.name, e)
        return ""


# ── Analysis functions ────────────────────────────────────────────────────────

def _analyse_pitch(y, sr) -> tuple[str, str]:
    """Return (gender, register) from mean fundamental frequency."""
    import numpy as np
    import librosa

    try:
        f0, voiced_flag, _ = librosa.pyin(
            y,
            fmin=librosa.note_to_hz('C2'),   # ~65 Hz — below bass
            fmax=librosa.note_to_hz('C7'),   # ~2093 Hz — above soprano
            sr=sr,
        )
        voiced_f0 = f0[voiced_flag & ~np.isnan(f0)]

        if len(voiced_f0) == 0:
            return "Unknown", "Unknown register"

        mean_f0 = float(np.median(voiced_f0))

        # Gender from pitch
        gender = "Female" if mean_f0 >= 165 else "Male"

        # Register from pitch ranges (Hz)
        # Male:   Bass <110, Baritone 110-155, Tenor 155-210
        # Female: Alto 165-225, Mezzo-soprano 200-265, Soprano 250-350+
        if mean_f0 < 110:
            register = "Bass"
        elif mean_f0 < 155:
            register = "Baritone"
        elif mean_f0 < 165:
            register = "Tenor"
        elif mean_f0 < 225:
            register = "Alto"
        elif mean_f0 < 265:
            register = "Mezzo-soprano"
        else:
            register = "Soprano"

        return gender, register

    except Exception as e:
        log.debug("Pitch analysis failed: %s", e)
        return "Unknown", "Unknown register"


def _analyse_tone(y, sr) -> str:
    """Tone color from spectral centroid (Hz). Low = dark/warm, high = bright/piercing."""
    import numpy as np
    import librosa

    centroid = librosa.feature.spectral_centroid(y=y, sr=sr)
    mean_centroid = float(np.mean(centroid))

    # Centroid thresholds (Hz) — tuned for speech content
    if mean_centroid < 1200:
        return "Dark tone"
    elif mean_centroid < 1800:
        return "Warm tone"
    elif mean_centroid < 2500:
        return "Neutral tone"
    elif mean_centroid < 3200:
        return "Bright tone"
    else:
        return "Piercing tone"


def _analyse_texture(y, sr) -> str:
    """
    Voice texture from a harmonic-to-noise ratio approximation.
    Uses spectral flatness as a proxy — low flatness = tonal/smooth,
    high flatness = noise-like/rough/breathy.
    """
    import numpy as np
    import librosa

    flatness = librosa.feature.spectral_flatness(y=y)
    mean_flatness = float(np.mean(flatness))

    # Spectral flatness is 0 (pure tone) to 1 (white noise)
    # For voiced speech: smooth voices have lower flatness
    if mean_flatness < 0.005:
        return "Smooth"
    elif mean_flatness < 0.015:
        return "Clear"
    elif mean_flatness < 0.04:
        return "Slightly rough"
    elif mean_flatness < 0.10:
        return "Rough"
    else:
        return "Gravelly"


def _analyse_energy(y) -> str:
    """Dynamic energy level from RMS."""
    import numpy as np
    import librosa

    rms = librosa.feature.rms(y=y)
    mean_rms = float(np.mean(rms))

    if mean_rms < 0.02:
        return "Soft"
    elif mean_rms < 0.05:
        return "Moderate energy"
    elif mean_rms < 0.10:
        return "Strong energy"
    else:
        return "Powerful"


def _analyse_pace(transcript: str, duration_sec: float) -> str:
    """Speaking pace from words-per-minute."""
    words = len(transcript.split())
    if words == 0 or duration_sec == 0:
        return "Unknown pace"

    wpm = (words / duration_sec) * 60

    if wpm < 90:
        return "Very slow pace"
    elif wpm < 130:
        return "Slow pace"
    elif wpm < 170:
        return "Moderate pace"
    elif wpm < 210:
        return "Fast pace"
    else:
        return "Rapid pace"


def _format_language(lang: str) -> str:
    """Convert Whisper language code to display name."""
    _LANG_MAP = {
        "english": "English",
        "en": "English",
        "russian": "Russian",
        "ru": "Russian",
        "japanese": "Japanese",
        "ja": "Japanese",
        "chinese": "Chinese",
        "zh": "Chinese",
        "french": "French",
        "fr": "French",
        "spanish": "Spanish",
        "es": "Spanish",
        "german": "German",
        "de": "German",
        "italian": "Italian",
        "it": "Italian",
        "korean": "Korean",
        "ko": "Korean",
        "portuguese": "Portuguese",
        "pt": "Portuguese",
    }
    return _LANG_MAP.get(lang.lower(), lang.title())
