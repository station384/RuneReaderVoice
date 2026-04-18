// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

public sealed class NpcPeopleCatalogService
{
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
