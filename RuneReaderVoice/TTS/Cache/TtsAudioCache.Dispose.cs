// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // DB is owned by RvrDb, disposed via AppServices. Nothing to do here.
    public void Dispose() { }
}
