"""
text_normalize.py — Two-layer text normalization for RRV server.

Layer 1: WoW-specific pre-normalization (custom regex rules)
Layer 2: wetext English TN (general English normalization)

Applied to all text before synthesis, regardless of provider.
wetext is optional — if not installed, only WoW pre-normalization runs.
"""
from __future__ import annotations

import logging
import re
from functools import lru_cache
from typing import Optional

log = logging.getLogger(__name__)

# ── WoW currency ──────────────────────────────────────────────────────────────
# "5g 32s 14c" → "5 gold 32 silver 14 copper"
# "5g" → "5 gold"
# "32s 14c" → "32 silver 14 copper"
_CURRENCY_RE = re.compile(
    r'\b(\d+)g(?:\s+(\d+)s)?(?:\s+(\d+)c)?\b'
    r'|'
    r'\b(\d+)s(?:\s+(\d+)c)?\b'
    r'|'
    r'\b(\d+)c\b'
)

def _currency_sub(m: re.Match) -> str:
    parts = []
    if m.group(1) is not None:
        parts.append(f"{m.group(1)} gold")
        if m.group(2): parts.append(f"{m.group(2)} silver")
        if m.group(3): parts.append(f"{m.group(3)} copper")
    elif m.group(4) is not None:
        parts.append(f"{m.group(4)} silver")
        if m.group(5): parts.append(f"{m.group(5)} copper")
    elif m.group(6) is not None:
        parts.append(f"{m.group(6)} copper")
    return " ".join(parts)

# ── WoW duration ──────────────────────────────────────────────────────────────
# "2:30:00" → "2 hours 30 minutes"
# "0:45:00" → "45 minutes"
# "1:30" → "1 minute 30 seconds"  (mm:ss when < 1hr context)
_DURATION_HHMMSS_RE = re.compile(r'\b(\d+):(\d{2}):(\d{2})\b')
_DURATION_MMSS_RE   = re.compile(r'\b([0-5]?\d):([0-5]\d)\b')

def _duration_hhmmss_sub(m: re.Match) -> str:
    h, mn, s = int(m.group(1)), int(m.group(2)), int(m.group(3))
    parts = []
    if h:  parts.append(f"{h} hour{'s' if h != 1 else ''}")
    if mn: parts.append(f"{mn} minute{'s' if mn != 1 else ''}")
    if s:  parts.append(f"{s} second{'s' if s != 1 else ''}")
    return " ".join(parts) if parts else "zero seconds"

def _duration_mmss_sub(m: re.Match) -> str:
    mn, s = int(m.group(1)), int(m.group(2))
    parts = []
    if mn: parts.append(f"{mn} minute{'s' if mn != 1 else ''}")
    if s:  parts.append(f"{s} second{'s' if s != 1 else ''}")
    return " ".join(parts) if parts else "zero seconds"

# ── WoW shorthand durations ───────────────────────────────────────────────────
# "1h 30m" → "1 hour 30 minutes"
# "45m" → "45 minutes"
_SHORTHAND_DUR_RE = re.compile(
    r'\b(\d+)h(?:\s+(\d+)m)?(?:\s+(\d+)s)?\b'
    r'|'
    r'\b(\d+)m(?:\s+(\d+)s)?\b'
)

def _shorthand_dur_sub(m: re.Match) -> str:
    parts = []
    if m.group(1) is not None:
        h = int(m.group(1))
        parts.append(f"{h} hour{'s' if h != 1 else ''}")
        if m.group(2):
            mn = int(m.group(2))
            parts.append(f"{mn} minute{'s' if mn != 1 else ''}")
        if m.group(3):
            s = int(m.group(3))
            parts.append(f"{s} second{'s' if s != 1 else ''}")
    elif m.group(4) is not None:
        mn = int(m.group(4))
        parts.append(f"{mn} minute{'s' if mn != 1 else ''}")
        if m.group(5):
            s = int(m.group(5))
            parts.append(f"{s} second{'s' if s != 1 else ''}")
    return " ".join(parts)

# ── Version strings ───────────────────────────────────────────────────────────
# "11.0.5" → "eleven point zero point five"
# Only when clearly a version (preceded by word like patch/version/hotfix)
_VERSION_RE = re.compile(
    r'(?i)\b(patch|version|hotfix|update|build)\s+(\d+\.\d+(?:\.\d+)*)\b'
)

_DIGIT_WORDS = {
    '0': 'zero', '1': 'one', '2': 'two', '3': 'three', '4': 'four',
    '5': 'five', '6': 'six', '7': 'seven', '8': 'eight', '9': 'nine',
}

def _version_sub(m: re.Match) -> str:
    label = m.group(1)
    # "11.0.5" -> ["11", ".", "0", ".", "5"] -> expand each numeric segment
    segments = m.group(2).split('.')
    word_segments = []
    for seg in segments:
        # Expand each digit individually for version numbers
        word_segments.append(' '.join(_DIGIT_WORDS.get(c, c) for c in seg))
    return f"{label} {' point '.join(word_segments)}"

# ── Abbreviated thousands ─────────────────────────────────────────────────────
# "15k gold" → "15 thousand gold"
# "1.5k" → "1.5 thousand"
_KILO_RE = re.compile(r'\b(\d+(?:\.\d+)?)[kK]\b')

def _kilo_sub(m: re.Match) -> str:
    return f"{m.group(1)} thousand"

# ── Coordinates ───────────────────────────────────────────────────────────────
# "52.3, 71.8" — leave as-is, wetext handles decimals fine
# No custom rule needed.

# ── WoW time with AM/PM ───────────────────────────────────────────────────────
# "10:21 AM" — wetext should handle this, but if it says "one thousand..."
# we pre-expand: "10:21 AM" → "10 21 AM"
# Actually let wetext handle standard times — only intervene for edge cases.

# ── Coordinate-like decimals in "Location:" context ──────────────────────────
# "Location: 52.3, 71.8" — fine as-is for TTS

# ── Master WoW pre-normalization pipeline ─────────────────────────────────────

def _wow_prenormalize(text: str) -> str:
    """Apply WoW-specific normalization rules before wetext."""
    # Order matters — apply most specific patterns first
    text = _CURRENCY_RE.sub(_currency_sub, text)
    text = _VERSION_RE.sub(_version_sub, text)
    text = _KILO_RE.sub(_kilo_sub, text)
    text = _DURATION_HHMMSS_RE.sub(_duration_hhmmss_sub, text)
    text = _DURATION_MMSS_RE.sub(_duration_mmss_sub, text)
    text = _SHORTHAND_DUR_RE.sub(_shorthand_dur_sub, text)
    return text

# ── wetext integration ────────────────────────────────────────────────────────

@lru_cache(maxsize=1)
def _get_wetext_normalizer() -> Optional[object]:
    """Load wetext normalizer once, return None if not installed."""
    try:
        from wetext import Normalizer  # type: ignore
        n = Normalizer(lang="en", operator="tn")
        log.info("text_normalize: wetext English TN loaded")
        return n
    except ImportError:
        log.info("text_normalize: wetext not installed — using WoW pre-normalization only")
        return None
    except Exception as e:
        log.warning("text_normalize: wetext failed to load (%s) — WoW pre-normalization only", e)
        return None

# ── Public API ────────────────────────────────────────────────────────────────

def normalize(text: str) -> str:
    """
    Normalize text for TTS synthesis.
    Layer 1: WoW-specific patterns (currency, durations, versions, abbreviations)
    Layer 2: wetext English TN (numbers, dates, percentages, standard times)
    """
    if not text or not text.strip():
        return text

    # Layer 1 — WoW-specific
    text = _wow_prenormalize(text)

    # Layer 2 — wetext (optional)
    normalizer = _get_wetext_normalizer()
    if normalizer is not None:
        try:
            text = normalizer.normalize(text)
        except Exception as e:
            log.warning("text_normalize: wetext normalize failed (%s) — returning layer-1 result", e)

    return text
