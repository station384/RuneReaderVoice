// SPDX-License-Identifier: GPL-3.0-or-later

namespace RuneReaderVoice.TTS.Pronunciation;

public sealed record PronunciationSymbol(
    string Symbol,
    string Name,
    string Description,
    string Example,
    string Category,
    string? DisplayLabel = null)
{
    public string ButtonLabel => string.IsNullOrWhiteSpace(DisplayLabel) ? Symbol : DisplayLabel!;
}
