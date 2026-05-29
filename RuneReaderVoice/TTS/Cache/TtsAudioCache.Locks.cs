// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── Per-key lock management ───────────────────────────────────────────────

    private sealed class KeyLockEntry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private sealed class KeyLockLease : IDisposable
    {
        private readonly TtsAudioCache _owner;
        private readonly string _key;
        private readonly KeyLockEntry _entry;
        private bool _disposed;

        public KeyLockLease(TtsAudioCache owner, string key, KeyLockEntry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _entry.Semaphore.Release();
            _owner.ReleaseKeyLock(_key, _entry);
        }
    }

    private async Task<KeyLockLease> AcquireKeyLockAsync(string key, CancellationToken ct)
    {
        KeyLockEntry entry;
        lock (_keyLocksGate)
        {
            if (!_keyLocks.TryGetValue(key, out entry!))
            {
                entry = new KeyLockEntry();
                _keyLocks[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new KeyLockLease(this, key, entry);
        }
        catch
        {
            ReleaseKeyLock(key, entry);
            throw;
        }
    }

    private void ReleaseKeyLock(string key, KeyLockEntry entry)
    {
        lock (_keyLocksGate)
        {
            if (entry.RefCount > 0)
                entry.RefCount--;

            if (entry.RefCount == 0 && _keyLocks.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _keyLocks.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }
}
