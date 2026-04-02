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

using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationDebug
{
    public static string Test(string text, AccentGroup group)
    {
        var processor = new DialoguePronunciationProcessor(
            WowPronunciationRules.CreateDefault());

        var slot = group == AccentGroup.Narrator
            ? VoiceSlot.Narrator
            : new VoiceSlot(group, Gender.Male);

        return processor.ProcessText(text, slot);
    }
}