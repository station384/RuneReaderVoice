// SPDX-License-Identifier: GPL-3.0-or-later

namespace RuneReaderVoice.TTS.TextSwap;

public sealed record TextSwapRule(
    string FindText,
    string ReplaceText,
    bool WholeWord = false,
    bool CaseSensitive = false,
    int Priority = 0)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(FindText);
}