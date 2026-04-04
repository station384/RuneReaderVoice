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
# server/utils.py
#
# Pure stdlib utilities with no heavy dependencies.
# Safe to import in worker venvs that do not have the full host dep chain.

from __future__ import annotations

import hashlib
from pathlib import Path


def compute_file_hash(path: Path) -> str:
    """
    SHA-256 hash of a file's contents, truncated to 8 hex characters.

    Used by:
      - Backends: hashing reference sample files for internal conditionals caching
        (Chatterbox, F5-TTS) and model files for model_version (Kokoro, F5-TTS).
      - Routes: hashing sample files as the voice_identity component of the
        server-side cache key.

    Pure stdlib — safe to import in worker venvs.
    """
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()[:8]
