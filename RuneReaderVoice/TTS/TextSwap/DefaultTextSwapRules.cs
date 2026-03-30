// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace RuneReaderVoice.TTS.TextSwap;

public static class DefaultTextSwapRules
{
    public static IReadOnlyList<TextSwapRuleEntry> CreateDefaultEntries()
        => new[]
        {
            new TextSwapRuleEntry { FindText = "...", ReplaceText = "... ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 300, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = " -- ", ReplaceText = " ... ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 290, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = "—", ReplaceText = ", ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 280, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = "–", ReplaceText = ", ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 270, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = ";", ReplaceText = ". ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 260, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = ",", ReplaceText = ", ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 250, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = "!", ReplaceText = "! ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 240, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = "?", ReplaceText = "? ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 230, Notes = "Built-in default" },
            new TextSwapRuleEntry { FindText = "--", ReplaceText = ", ", WholeWord = false, CaseSensitive = true, Enabled = true, Priority = 220, Notes = "Built-in default" },
        };

    public static IReadOnlyList<TextSwapRule> CreateDefault()
        => CreateDefaultEntries()
            .Select(r => r.ToRule())
            .ToList();
}
