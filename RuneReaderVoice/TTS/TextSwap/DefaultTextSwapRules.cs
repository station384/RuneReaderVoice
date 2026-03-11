// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;

namespace RuneReaderVoice.TTS.TextSwap;

public static class DefaultTextSwapRules
{
    public static IReadOnlyList<TextSwapRule> CreateDefault()
        => new[]
        {
            new TextSwapRule("...", "... ", WholeWord: false, CaseSensitive: true, Priority: 300),
            new TextSwapRule(" -- ", " ... ", WholeWord: false, CaseSensitive: true, Priority: 290),
            new TextSwapRule("—", ", ", WholeWord: false, CaseSensitive: true, Priority: 280),
            new TextSwapRule("–", ", ", WholeWord: false, CaseSensitive: true, Priority: 270),
            new TextSwapRule(";", ". ", WholeWord: false, CaseSensitive: true, Priority: 260),
            new TextSwapRule(",", ", ", WholeWord: false, CaseSensitive: true, Priority: 250),
            new TextSwapRule("!", "! ", WholeWord: false, CaseSensitive: true, Priority: 240),
            new TextSwapRule("?", "? ", WholeWord: false, CaseSensitive: true, Priority: 230),
            new TextSwapRule("--", ", ", WholeWord: false, CaseSensitive: true, Priority: 220),
        };
}