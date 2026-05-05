// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

public sealed class NpcPeopleCatalogService
{
    private static readonly IReadOnlyDictionary<int, string> RaceIdToCatalogId = new Dictionary<int, string>
    {
        { 1, "human" }, { 2, "orc" }, { 3, "dwarf" }, { 4, "nightelf" },
        { 5, "undead" }, { 6, "tauren" }, { 7, "gnome" }, { 8, "troll" },
        { 9, "goblin" }, { 10, "bloodelf" }, { 11, "draenei" }, { 13, "pandaren" },
        { 22, "worgen" }, { 24, "nightborne" }, { 25, "highmountaintauren" },
        { 26, "lightforgeddraenei" }, { 27, "highmountaintauren" }, { 28, "lightforgeddraenei" },
        { 29, "voidelf" }, { 30, "darkirondwarf" }, { 31, "zandalaritroll" },
        { 32, "kultiran" }, { 34, "dracthyr" }, { 35, "vulpera" },
        { 36, "magharorc" }, { 37, "mechagnome" }, { 52, "earthen" }, { 70, "haranir" },
        { 0x52, "dragonkin" }, { 0x53, "undead" }, { 0x54, "illidari" },
        { 0x55, "elemental" }, { 0x56, "giant" }, { 0x57, "mechanical" },
        { 0x021F, "amani" }, { 0x0220, "arathi" }, { 0x0221, "broken" },
        { 0x0222, "centaur" }, { 0x0223, "darktroll" }, { 0x0224, "dredger" },
        { 0x0225, "dryad" }, { 0x0226, "faerie" }, { 0x0227, "fungarian" },
        { 0x0228, "grummle" }, { 0x0229, "hobgoblin" }, { 0x022A, "kyrian" },
        { 0x022B, "nerubian" }, { 0x022C, "refti" }, { 0x022D, "revantusk" },
        { 0x022E, "rutaani" }, { 0x022F, "shadowpine" }, { 0x0230, "titan" },
        { 0x0231, "tortollan" }, { 0x0232, "tuskarr" }, { 0x0233, "venthyr" },
        { 0x0234, "zulaman" },
    };

    private static readonly IReadOnlyDictionary<string, int> CatalogIdToRaceId =
        RaceIdToCatalogId
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(kv => kv.Key), StringComparer.OrdinalIgnoreCase);

    public static string CatalogIdFromRaceId(int raceId)
        => RaceIdToCatalogId.TryGetValue(raceId, out var id) ? id : string.Empty;

    public static int? RaceIdFromCatalogId(string? catalogId)
    {
        var key = VoiceSlot.NormalizeCatalogId(catalogId);
        return CatalogIdToRaceId.TryGetValue(key, out var raceId) ? raceId : null;
    }

    private readonly NpcPeopleCatalogStore _store;

    public NpcPeopleCatalogService(NpcPeopleCatalogStore store) => _store = store;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task ReloadAsync() => Task.CompletedTask;

    public Task<NpcPeopleCatalogPage> QueryPageAsync(string? filter, int pageNumber, int pageSize)
        => _store.QueryPageAsync(filter, pageNumber, pageSize);

    public Task<NpcPeopleCatalogRow> GetByIdAsync(string id)
    {
        return _store.GetByIdAsync(id);
    }

    public IReadOnlyList<NpcPeopleCatalogRow> GetAllRows()
        => _store.GetAllAsync().GetAwaiter().GetResult()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<NpcPeopleCatalogRow> GetEnabledRows()
        => _store.GetEnabledAsync().GetAwaiter().GetResult()
            .Where(x => x.HasMale || x.HasFemale || x.HasNeutral)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task UpsertAsync(NpcPeopleCatalogRow row)
        => await _store.UpsertAsync(row);

    public async Task SetEnabledAsync(string id, bool enabled)
        => await _store.SetEnabledAsync(id, enabled);

    public async Task ReplaceAllAsync(IEnumerable<NpcPeopleCatalogRow> rows)
        => await _store.ReplaceAllAsync(rows);

    public IReadOnlyList<NpcPeopleCatalogRow> GetAllRowsSnapshot()
        => _store.GetAllAsync().GetAwaiter().GetResult()
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<NpcPeopleCatalogRow> SearchEnabledRows(string? filter, int limit = 500)
        => _store.QueryEnabledAsync(filter, limit).GetAwaiter().GetResult();

    public IReadOnlyList<VoiceSlotCatalogRow> GetVoiceSlots()
    {
        var result = new List<VoiceSlotCatalogRow>();
        var maleNarrator = new VoiceSlotCatalogRow(VoiceSlot.MaleNarrator, "Narrator / Male", "Narrator", 0);
        var femaleNarrator = new VoiceSlotCatalogRow(VoiceSlot.FemaleNarrator, "Narrator / Female", "Narrator", 1);

        result.Add(maleNarrator);
        result.Add(femaleNarrator);

        foreach (var row in GetEnabledRows())
        {
            if (row.HasMale)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Male), $"{row.DisplayName} / Male", row.AccentLabel, row.SortOrder));
            if (row.HasFemale)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Female), $"{row.DisplayName} / Female", row.AccentLabel, row.SortOrder + 1));
            if (row.HasNeutral)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Unknown), row.DisplayName, row.AccentLabel, row.SortOrder + 2));
        }

        return result;
    }

    public VoiceSlot ResolveCatalogSlot(string catalogId, Gender packetGender)
    {
        var row = _store.GetByIdAsync(catalogId).GetAwaiter().GetResult();
        if (row == null || !row.Enabled)
            return packetGender == Gender.Female ? VoiceSlot.FemaleNarrator : VoiceSlot.MaleNarrator;

        if (packetGender == Gender.Female && row.HasFemale)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Female);
        if (packetGender == Gender.Male && row.HasMale)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Male);
        if (packetGender == Gender.Unknown && row.HasNeutral)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Unknown);

        if (row.HasMale)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Male);
        if (row.HasFemale)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Female);
        if (row.HasNeutral)
            return VoiceSlot.CreateCatalog(row.Id, Gender.Unknown);

        return packetGender == Gender.Female ? VoiceSlot.FemaleNarrator : VoiceSlot.MaleNarrator;
    }

    public string GetSlotLabel(VoiceSlot slot)
    {
        if (slot.IsNarrator)
            return slot.Gender == Gender.Female ? "Narrator / Female" : "Narrator / Male";

        var row = _store.GetByIdAsync(slot.SlotKey).GetAwaiter().GetResult();
        if (row == null || !row.Enabled)
            return slot.ToString();

        return slot.Gender switch
        {
            Gender.Male when row.HasMale => $"{row.DisplayName} / Male",
            Gender.Female when row.HasFemale => $"{row.DisplayName} / Female",
            Gender.Unknown when row.HasNeutral => row.DisplayName,
            _ => row.DisplayName
        };
    }

    public string GetSlotAccentLabel(VoiceSlot slot)
    {
        if (slot.IsNarrator)
            return "Narrator";

        var row = _store.GetByIdAsync(slot.SlotKey).GetAwaiter().GetResult();
        return row?.AccentLabel ?? slot.SlotKey;
    }
}
