// SPDX-License-Identifier: GPL-3.0-or-later

using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public sealed record PronunciationRule(
    string MatchText,
    string PhonemeText,
    AccentGroup? Group = null,
    bool WholeWord = false,
    bool CaseSensitive = false,
    int Priority = 0)
{
    public bool IsGlobal => Group is null;

    public string ToKokoroMarkup() => $"[{MatchText}](/{PhonemeText}/)";
}