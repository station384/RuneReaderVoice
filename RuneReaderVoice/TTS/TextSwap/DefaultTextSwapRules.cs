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