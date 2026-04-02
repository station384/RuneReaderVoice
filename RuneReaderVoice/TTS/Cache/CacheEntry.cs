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

using System;

namespace RuneReaderVoice.TTS.Cache;

/// <summary>
/// In-memory representation of a cache manifest entry.
/// Serialized to/from the AudioCacheManifest DB table via AudioCacheManifestRow.
/// </summary>
public sealed class CacheEntry
{
    public string Key           { get; init; } = string.Empty;
    public string FileName      { get; init; } = string.Empty;
    public string VoiceSlotId   { get; init; } = string.Empty;
    public string TextPreview   { get; init; } = string.Empty; // first 60 chars
    public long   FileSizeBytes { get; set; }
    public bool   IsCompressed  { get; init; }
    public DateTime LastAccessed { get; set; }
    public DateTime Created      { get; init; }
}