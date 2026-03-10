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
using System.IO;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider
{
    // ── PCM float[] → 16-bit mono WAV ────────────────────────────────────────

    private static byte[] PcmToWav(float[] pcm, int sampleRate)
    {
        int byteCount = pcm.Length * 2;
        using var ms = new MemoryStream(44 + byteCount);
        using var writer = new BinaryWriter(ms);

        writer.Write("RIFF"u8);
        writer.Write(36 + byteCount);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);   // PCM, mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);  // block align, bits
        writer.Write("data"u8);
        writer.Write(byteCount);

        foreach (var s in pcm)
            writer.Write((short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue));

        return ms.ToArray();
    }
}