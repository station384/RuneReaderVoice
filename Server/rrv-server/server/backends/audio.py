# SPDX-License-Identifier: GPL-3.0-or-later
#
# This file is part of RuneReader Voice Server (rrv-server).
#
# Copyright (C) 2026 Michael Sutton / Tanstaafl Gaming
#
# RuneReader Voice Server is free software: you can redistribute it and/or
# modify it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# RuneReader Voice Server is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with RuneReader Voice Server. If not, see <https://www.gnu.org/licenses/>.
# server/backends/audio.py
#
# Shared audio encoding utilities for all TTS backends.
#
# OGG encoding uses ffmpeg (already a hard server dependency) rather than
# soundfile's built-in OGG writer. soundfile silently ignores the quality
# parameter on some platforms/versions, producing compressed output regardless
# of the setting. ffmpeg's libvorbis encoder honours -q:a reliably.
#
# Quality scale: ffmpeg libvorbis -q:a 0–10
#   5 = ~160kbps  (adequate for voice)
#   7 = ~224kbps  (good quality, default here)
#   9 = ~320kbps  (near-lossless for voice)
#
# Flow: float32 numpy array → temp WAV (soundfile) → ffmpeg → OGG bytes

from __future__ import annotations

import io
import logging
import subprocess
import tempfile
from pathlib import Path

log = logging.getLogger(__name__)

# Vorbis quality level — 7 gives excellent voice fidelity with modest file size.
# Raise to 9 for near-lossless; lower to 5 to reduce LAN transfer size.
OGG_QUALITY = 7


def pcm_to_ogg(samples, sample_rate: int) -> bytes:
    """
    Encode float32 PCM samples to OGG Vorbis bytes using ffmpeg.

    Falls back to soundfile if ffmpeg is unavailable or fails,
    so synthesis is never blocked by an encoding error.
    """
    import numpy as np
    import soundfile as sf

    # Ensure float32
    if not isinstance(samples, np.ndarray) or samples.dtype != np.float32:
        samples = np.array(samples, dtype=np.float32)

    if samples.ndim not in (1, 2):
        raise ValueError(f"Unsupported PCM shape for OGG encode: {samples.shape}")

    # Clamp to prevent clipping artefacts in the encoder
    samples = np.clip(samples, -1.0, 1.0)

    # Write to a temporary WAV first — ffmpeg reads from file, not stdin,
    # for reliable seeking on all platforms.
    try:
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp_wav:
            tmp_wav_path = tmp_wav.name

        sf.write(tmp_wav_path, samples, sample_rate, subtype="PCM_16")

        result = subprocess.run(
            [
                "ffmpeg", "-y",
                "-i", tmp_wav_path,
                "-c:a", "libvorbis",
                "-q:a", str(OGG_QUALITY),
                "-f", "ogg",
                "pipe:1",
            ],
            capture_output=True,
            timeout=30,
        )

        if result.returncode != 0:
            log.warning(
                "pcm_to_ogg: ffmpeg failed (rc=%d) — falling back to soundfile. "
                "stderr: %s",
                result.returncode,
                result.stderr[-200:].decode("utf-8", errors="replace") if result.stderr else "",
            )
            return _pcm_to_ogg_soundfile(samples, sample_rate)

        return result.stdout

    except FileNotFoundError:
        log.warning("pcm_to_ogg: ffmpeg not found — falling back to soundfile")
        return _pcm_to_ogg_soundfile(samples, sample_rate)

    except subprocess.TimeoutExpired:
        log.warning("pcm_to_ogg: ffmpeg timed out — falling back to soundfile")
        return _pcm_to_ogg_soundfile(samples, sample_rate)

    except Exception as e:
        log.warning("pcm_to_ogg: ffmpeg error (%s) — falling back to soundfile", e)
        return _pcm_to_ogg_soundfile(samples, sample_rate)

    finally:
        # Clean up temp WAV
        try:
            Path(tmp_wav_path).unlink(missing_ok=True)
        except Exception:
            pass


def _pcm_to_ogg_soundfile(samples, sample_rate: int) -> bytes:
    """Fallback OGG encoder using soundfile."""
    import soundfile as sf
    buf = io.BytesIO()
    sf.write(buf, samples, sample_rate, format="OGG", subtype="VORBIS")
    buf.seek(0)
    return buf.read()


def estimate_duration(ogg_bytes: bytes) -> float:
    """Read duration from OGG header bytes."""
    try:
        import soundfile as sf
        buf = io.BytesIO(ogg_bytes)
        info = sf.info(buf)
        return info.duration
    except Exception:
        return 0.0


def trim_ogg_tail_ms(ogg_bytes: bytes, tail_trim_ms: int) -> tuple[bytes, float]:
    """
    Trim a fixed amount from the tail of an OGG and re-encode it.

    Maintainer note:
    This is intentionally a transport/result-layer helper, not a synthesis-layer helper.
    We use it for client-requested batch joins where adjacent returned segments may be
    merged mid-sentence. In that case, a small sentence-ending pad that sounds natural
    at a true sentence boundary becomes an audible seam.

    Do NOT repurpose this to globally trim backend output without re-checking normal
    narration cadence first.
    """
    import numpy as np
    import soundfile as sf

    if tail_trim_ms <= 0:
        return ogg_bytes, estimate_duration(ogg_bytes)

    try:
        buf = io.BytesIO(ogg_bytes)
        samples, sample_rate = sf.read(buf, dtype="float32", always_2d=False)
        if sample_rate <= 0:
            return ogg_bytes, estimate_duration(ogg_bytes)

        trim_frames = int(sample_rate * (tail_trim_ms / 1000.0))
        if trim_frames <= 0:
            return ogg_bytes, estimate_duration(ogg_bytes)

        frame_count = int(samples.shape[0]) if getattr(samples, 'ndim', 1) > 1 else int(len(samples))
        # Keep a small safety margin so very short chunks are never gutted.
        min_keep_frames = max(int(sample_rate * 0.05), 1)
        if frame_count <= trim_frames + min_keep_frames:
            return ogg_bytes, estimate_duration(ogg_bytes)

        trimmed = samples[:-trim_frames]
        trimmed = np.asarray(trimmed, dtype=np.float32)
        trimmed_ogg = pcm_to_ogg(trimmed, int(sample_rate))
        trimmed_duration = float(len(trimmed) / sample_rate) if getattr(trimmed, 'ndim', 1) == 1 else float(trimmed.shape[0] / sample_rate)
        return trimmed_ogg, trimmed_duration
    except Exception as e:
        log.warning("trim_ogg_tail_ms: failed (%s) — returning original audio", e)
        return ogg_bytes, estimate_duration(ogg_bytes)
