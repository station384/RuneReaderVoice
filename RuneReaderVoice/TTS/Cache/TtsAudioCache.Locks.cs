// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Threading;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── Per-key lock management ───────────────────────────────────────────────

    private SemaphoreSlim GetKeyLock(string key)
    {
        lock (_keyLocksGate)
        {
            if (!_keyLocks.TryGetValue(key, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _keyLocks[key] = sem;
            }

            return sem;
        }
    }
}