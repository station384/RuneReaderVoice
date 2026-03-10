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

using System;
using System.Linq;
using System.Threading.Tasks;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    public void Dispose()
    {
        // Wait briefly for any in-flight background compression to finish so we
        // don't leave orphaned .wav files alongside half-written .ogg files.
        Task[] pending;
        lock (_compressionTasksGate)
            pending = _compressionTasks.Where(t => !t.IsCompleted).ToArray();

        if (pending.Length > 0)
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));

        _manifestLock.Dispose();
    }
}