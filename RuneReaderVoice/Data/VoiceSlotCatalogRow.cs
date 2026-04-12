// SPDX-License-Identifier: GPL-3.0-only
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

public sealed record VoiceSlotCatalogRow(
    VoiceSlot Slot,
    string NpcLabel,
    string AccentLabel,
    int SortOrder
);
