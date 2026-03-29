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