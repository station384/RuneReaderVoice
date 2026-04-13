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

    public Task<NpcPeopleCatalogRow?> GetByIdAsync(string id)
        => _store.GetByIdAsync(id);

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
        => GetVoiceSlots().FirstOrDefault(x => x.Slot.Equals(slot))?.NpcLabel
           ?? slot.ToString();

    public string GetSlotAccentLabel(VoiceSlot slot)
        => GetVoiceSlots().FirstOrDefault(x => x.Slot.Equals(slot))?.AccentLabel
           ?? slot.SlotKey;
}
