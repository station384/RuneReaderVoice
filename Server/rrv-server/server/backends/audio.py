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

    # Determine channel count from array shape so ffmpeg output can be forced
    # back to the expected layout after loudnorm processing.
    if samples.ndim == 1:
        channels = 1
    elif samples.ndim == 2:
        channels = int(samples.shape[1])
    else:
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
                # Loudness normalisation — aligns all backends to consistent
                # perceived volume. Target is -21 LUFS which matches Chatterbox's
                # natural output level (-20.6 LUFS measured). F5 outputs at
                # ~-16 LUFS natively so this reduces it by ~4-5dB.
                # I=-21    target integrated loudness (LUFS)
                # LRA=11   loudness range — preserves natural dynamics
                # TP=-1.5  true peak ceiling — prevents clipping after encode
                "-af", "loudnorm=I=-21:LRA=11:TP=-1.5",
                # loudnorm may internally upsample to 192 kHz for true-peak
                # analysis; force the encoded output back to the model/native
                # PCM format so downstream caches and clients see stable audio
                # formats.
                "-ar", str(int(sample_rate)),
                "-ac", str(int(channels)),
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
