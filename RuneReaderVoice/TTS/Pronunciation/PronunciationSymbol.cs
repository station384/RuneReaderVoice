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