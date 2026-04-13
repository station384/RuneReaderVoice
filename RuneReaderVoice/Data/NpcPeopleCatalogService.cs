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
    private List<NpcPeopleCatalogRow> _rows = new();

    public NpcPeopleCatalogService(NpcPeopleCatalogStore store) => _store = store;

    public async Task InitializeAsync() => _rows = await _store.GetAllAsync();

    public Task ReloadAsync() => InitializeAsync();

    public IReadOnlyList<NpcPeopleCatalogRow> GetAllRows()
        => _rows
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public async Task UpsertAsync(NpcPeopleCatalogRow row)
    {
        await _store.UpsertAsync(row);
        await ReloadAsync();
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await _store.SetEnabledAsync(id, enabled);
        await ReloadAsync();
    }

    public IReadOnlyList<VoiceSlotCatalogRow> GetVoiceSlots()
    {
        var result = new List<VoiceSlotCatalogRow>();
        var maleNarrator = new VoiceSlotCatalogRow(VoiceSlot.MaleNarrator, "Narrator / Male", "Narrator", 0);
        var femaleNarrator = new VoiceSlotCatalogRow(VoiceSlot.FemaleNarrator, "Narrator / Female", "Narrator", 1);

        result.Add(maleNarrator);
        result.Add(femaleNarrator);

        foreach (var row in _rows.Where(x => x.Enabled).OrderBy(x => x.SortOrder).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            AccentGroup? legacyGroup = Enum.TryParse<AccentGroup>(row.AccentGroupName, out var parsed)
                ? parsed
                : null;

            if (row.HasMale)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Male, legacyGroup), $"{row.DisplayName} / Male", row.AccentLabel, row.SortOrder));
            if (row.HasFemale)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Female, legacyGroup), $"{row.DisplayName} / Female", row.AccentLabel, row.SortOrder + 1));
            if (row.HasNeutral)
                result.Add(new VoiceSlotCatalogRow(VoiceSlot.CreateCatalog(row.Id, Gender.Unknown, legacyGroup), row.DisplayName, row.AccentLabel, row.SortOrder + 2));
        }

        return result;
    }

    public string GetSlotLabel(VoiceSlot slot)
        => GetVoiceSlots().FirstOrDefault(x => x.Slot.Equals(slot))?.NpcLabel
           ?? slot.ToString();

    public string GetSlotAccentLabel(VoiceSlot slot)
        => GetVoiceSlots().FirstOrDefault(x => x.Slot.Equals(slot))?.AccentLabel
           ?? (slot.LegacyGroup?.ToString() ?? slot.SlotKey);
}

