// SPDX-License-Identifier: GPL-3.0-only

using System;

namespace RuneReaderVoice.Session;

internal static class NpcIdentityHelpers
{
    public static int? TryExtractNpcIdFromGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        var parts = guid.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 6 &&
            string.Equals(parts[0], "Creature", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[5], out var creatureId) && creatureId > 0)
            return creatureId;

        return null;
    }

    public static bool IsSyntheticBookNpcId(int npcId)
        => npcId >= 0xF00000 && npcId <= 0xFFFFFF;
}
