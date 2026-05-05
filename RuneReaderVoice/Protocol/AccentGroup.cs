// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.

using System;
using System.Collections.Generic;

namespace RuneReaderVoice.Protocol;

public enum Gender { Unknown = 0, Male = 1, Female = 2 }

/// <summary>
/// Identifies a specific voice slot. SlotKey is a catalog row id.
/// Narrator uses the special SlotKey "Narrator".
/// </summary>
public readonly record struct VoiceSlot(string SlotKey, Gender Gender)
{
    public static readonly VoiceSlot Narrator       = new("Narrator", Gender.Male);
    public static readonly VoiceSlot MaleNarrator   = new("Narrator", Gender.Male);
    public static readonly VoiceSlot FemaleNarrator = new("Narrator", Gender.Female);

    public static VoiceSlot CreateCatalog(string catalogId, Gender gender)
        => new(NormalizeCatalogId(catalogId), gender);

    public bool IsNarrator => string.Equals(SlotKey, "Narrator", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        IsNarrator
            ? (Gender == Gender.Female ? "Narrator/Female" : "Narrator/Male")
            : $"{NormalizeCatalogId(SlotKey)}/{Gender}";

    public static bool TryParse(string s, out VoiceSlot slot)
    {
        if (s == "Narrator" || s == "Narrator/Male")
        {
            slot = MaleNarrator;
            return true;
        }
        if (s == "Narrator/Female")
        {
            slot = FemaleNarrator;
            return true;
        }

        var idx = s.LastIndexOf('/');
        if (idx > 0)
        {
            var slotKey = NormalizeCatalogId(s[..idx]);
            var genderText = s[(idx + 1)..];
            if (Enum.TryParse<Gender>(genderText, out var gender))
            {
                slot = new VoiceSlot(slotKey, gender);
                return true;
            }
        }

        slot = default;
        return false;
    }

    /// <summary>
    /// Normalizes old enum-style names and UI labels to current DB catalog ids.
    /// This is a compatibility boundary only; new code should already pass catalog ids.
    /// </summary>
    ///
    // Todo:  This needs to be removed.    We don't need backwards compatibility.  There should not be any normalization going on as there are no predefined races.
    public static string NormalizeCatalogId(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var normalized = key.Trim()
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "narrator" => "Narrator",
            "darkirondwarf" => "darkirondwarf",
            "lightforged" or "lightforgeddraenei" => "lightforgeddraenei",
            "maghar" or "magharorc" => "magharorc",
            "highmountain" or "highmountaintauren" => "highmountaintauren",
            "zandalari" or "zandalaritroll" => "zandalaritroll",
            "nightelf" => "nightelf",
            "bloodelf" => "bloodelf",
            "voidelf" => "voidelf",
            "kultiran" => "kultiran",
            "mechagnome" => "mechagnome",
            "nightborne" => "nightborne",
            "dragonkinnpc" => "dragonkin",
            "elementalnpc" => "elemental",
            "giantnpc" => "giant",
            "mechanicalnpc" => "mechanical",
            "amanitroll" => "amani",
            "revantusktroll" => "revantusk",
            "shadowpinetroll" => "shadowpine",
            "titanconstruct" => "titan",
            "zulamantroll" => "zulaman",
            _ => normalized
        };
    }
}
