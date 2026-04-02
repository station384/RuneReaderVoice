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

// Base45Simple.cs
// Minimal Base45 encoder and decoder used by the RV packet pipeline.

namespace RuneReaderVoice;

using System;
using System.Collections.Generic;
using System.Text;

public static class Base45Simple
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    private static readonly Dictionary<char, int> Map = CreateMap();

    private static Dictionary<char, int> CreateMap()
    {
        var map = new Dictionary<char, int>();
        for (int i = 0; i < Alphabet.Length; i++)
            map[Alphabet[i]] = i;
        return map;
    }

    public static byte[] Decode(string s)
    {
        if (s == null)
            throw new ArgumentNullException(nameof(s));

        var bytes = new List<byte>();
        int pos = 0;

        while (pos < s.Length)
        {
            int remaining = s.Length - pos;

            if (remaining >= 3)
            {
                int a = ValueOf(s[pos]);
                int b = ValueOf(s[pos + 1]);
                int c = ValueOf(s[pos + 2]);

                int value = a + b * 45 + c * 45 * 45;
                if (value > 65535)
                    throw new FormatException("Invalid Base45 3-character group.");

                bytes.Add((byte)(value / 256));
                bytes.Add((byte)(value % 256));
                pos += 3;
            }
            else if (remaining == 2)
            {
                int a = ValueOf(s[pos]);
                int b = ValueOf(s[pos + 1]);

                int value = a + b * 45;
                if (value > 255)
                    throw new FormatException("Invalid Base45 2-character group.");

                bytes.Add((byte)value);
                pos += 2;
            }
            else
            {
                throw new FormatException("Invalid Base45 length.");
            }
        }

        return bytes.ToArray();
    }

    public static string DecodeUtf8(string s)
    {
        return Encoding.UTF8.GetString(Decode(s));
    }

    private static int ValueOf(char c)
    {
        if (!Map.TryGetValue(c, out int value))
            throw new FormatException("Invalid Base45 character: " + c);
        return value;
    }
}