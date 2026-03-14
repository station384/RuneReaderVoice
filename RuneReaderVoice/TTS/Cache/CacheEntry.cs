// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

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
