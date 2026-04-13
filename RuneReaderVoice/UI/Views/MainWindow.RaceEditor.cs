// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.Data;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private sealed class RaceEditorListItem
    {
        public required string Id { get; init; }
        public required string DisplayText { get; init; }
        public override string ToString() => DisplayText;
    }

    private string _raceEditorSearchText = string.Empty;
    private string? _raceEditorSelectedId;
    private bool _raceEditorLoading;

    private void InitRaceEditorUi()
    {
        _ = ReloadRaceEditorAsync();
        ClearRaceEditorForm();
    }

    private async Task ReloadRaceEditorAsync()
    {
        await AppServices.NpcPeopleCatalog.ReloadAsync();
        PopulateRaceEditorList();
        PopulateVoiceGrid();
        await RefreshNpcOverrideUiAfterRaceCatalogChangeAsync();
    }

    private void PopulateRaceEditorList()
    {
        var rows = AppServices.NpcPeopleCatalog.GetAllRows();
        if (!string.IsNullOrWhiteSpace(_raceEditorSearchText))
        {
            rows = rows.Where(x =>
                    x.Id.Contains(_raceEditorSearchText, StringComparison.OrdinalIgnoreCase) ||
                    x.AccentLabel.Contains(_raceEditorSearchText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        RaceEditorList.ItemsSource = rows
            .Select(x => new RaceEditorListItem
            {
                Id = x.Id,
                DisplayText = $"{x.DisplayName}  [{x.Id}]  •  {x.AccentLabel}  •  {(x.Enabled ? "Enabled" : "Inactive")}"
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(_raceEditorSelectedId))
        {
            var match = ((IEnumerable<RaceEditorListItem>)RaceEditorList.ItemsSource!).FirstOrDefault(x => x.Id == _raceEditorSelectedId);
            RaceEditorList.SelectedItem = match;
        }
    }

    private async Task RefreshNpcOverrideUiAfterRaceCatalogChangeAsync()
    {
        try
        {
            PopulateNpcOverrideRaceDropdown(LastNpcRaceDropdown);
            RefreshNpcOverridesGrid();
        }
        catch
        {
            // best-effort UI refresh only
        }
    }

    private void SetRaceEditorStatus(string message)
    {
        if (RaceEditorStatus != null)
            RaceEditorStatus.Text = message;
    }

    private void ClearRaceEditorForm(bool keepStatus = true)
    {
        _raceEditorLoading = true;
        _raceEditorSelectedId = null;

        RaceEditorIdBox.Text = string.Empty;
        RaceEditorIdBox.IsEnabled = true;
        RaceEditorDisplayNameBox.Text = string.Empty;
        RaceEditorAccentLabelBox.Text = string.Empty;
        RaceEditorSortOrderBox.Value = 0;
        RaceEditorEnabledCheck.IsChecked = true;
        RaceEditorHasMaleCheck.IsChecked = true;
        RaceEditorHasFemaleCheck.IsChecked = true;
        RaceEditorHasNeutralCheck.IsChecked = false;

        _raceEditorLoading = false;
        if (!keepStatus)
            SetRaceEditorStatus(string.Empty);
    }

    private void LoadRaceEditorRow(NpcPeopleCatalogRow row)
    {
        _raceEditorLoading = true;
        _raceEditorSelectedId = row.Id;

        RaceEditorIdBox.Text = row.Id;
        RaceEditorIdBox.IsEnabled = false;
        RaceEditorDisplayNameBox.Text = row.DisplayName;
        RaceEditorAccentLabelBox.Text = row.AccentLabel;
        RaceEditorSortOrderBox.Value = row.SortOrder;
        RaceEditorEnabledCheck.IsChecked = row.Enabled;
        RaceEditorHasMaleCheck.IsChecked = row.HasMale;
        RaceEditorHasFemaleCheck.IsChecked = row.HasFemale;
        RaceEditorHasNeutralCheck.IsChecked = row.HasNeutral;

        _raceEditorLoading = false;
    }

    private NpcPeopleCatalogRow? BuildRaceEditorRowFromForm()
    {
        var id = (RaceEditorIdBox.Text ?? string.Empty).Trim();
        var displayName = (RaceEditorDisplayNameBox.Text ?? string.Empty).Trim();
        var accentLabel = (RaceEditorAccentLabelBox.Text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(id))
        {
            SetRaceEditorStatus("Catalog Id required.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            SetRaceEditorStatus("Display Name required.");
            return null;
        }

        var hasMale = RaceEditorHasMaleCheck.IsChecked == true;
        var hasFemale = RaceEditorHasFemaleCheck.IsChecked == true;
        var hasNeutral = RaceEditorHasNeutralCheck.IsChecked == true;

        if (!hasMale && !hasFemale && !hasNeutral)
        {
            SetRaceEditorStatus("At least one slot variant required.");
            return null;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new NpcPeopleCatalogRow
        {
            Id = id,
            DisplayName = displayName,
            AccentLabel = string.IsNullOrWhiteSpace(accentLabel) ? displayName : accentLabel,
            HasMale = hasMale,
            HasFemale = hasFemale,
            HasNeutral = hasNeutral,
            Enabled = RaceEditorEnabledCheck.IsChecked == true,
            SortOrder = (int)(RaceEditorSortOrderBox.Value ?? 0),
            Source = "Local",
            UpdatedUtc = now,
        };
    }

    private async void OnRaceEditorSaveClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var row = BuildRaceEditorRowFromForm();
            if (row == null)
                return;

            await AppServices.NpcPeopleCatalog.UpsertAsync(row);
            _raceEditorSelectedId = row.Id;
            PopulateRaceEditorList();
            LoadRaceEditorRow(row);
            PopulateVoiceGrid();
            await RefreshNpcOverrideUiAfterRaceCatalogChangeAsync();
            SetRaceEditorStatus($"Saved {row.DisplayName}.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Save failed: {ex.Message}");
        }
    }

    private async void OnRaceEditorActivateClicked(object? sender, RoutedEventArgs e)
    {
        await SetRaceEditorEnabledAsync(true);
    }

    private async void OnRaceEditorDeactivateClicked(object? sender, RoutedEventArgs e)
    {
        await SetRaceEditorEnabledAsync(false);
    }

    private async Task SetRaceEditorEnabledAsync(bool enabled)
    {
        try
        {
            var id = _raceEditorSelectedId ?? (RaceEditorIdBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                SetRaceEditorStatus("Select or enter a catalog row first.");
                return;
            }

            await AppServices.NpcPeopleCatalog.SetEnabledAsync(id, enabled);
            await ReloadRaceEditorAsync();
            var row = AppServices.NpcPeopleCatalog.GetAllRows().FirstOrDefault(x => x.Id == id);
            if (row != null)
                LoadRaceEditorRow(row);
            SetRaceEditorStatus(enabled ? $"Activated {id}." : $"Deactivated {id}.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"State change failed: {ex.Message}");
        }
    }

    private async void OnRaceEditorRefreshClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadRaceEditorAsync();
            SetRaceEditorStatus("Race catalog refreshed.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Refresh failed: {ex.Message}");
        }
    }

    private void OnRaceEditorNewClicked(object? sender, RoutedEventArgs e)
    {
        RaceEditorList.SelectedItem = null;
        ClearRaceEditorForm(false);
        SetRaceEditorStatus("New catalog row.");
    }

    private void OnRaceEditorClearClicked(object? sender, RoutedEventArgs e)
    {
        RaceEditorList.SelectedItem = null;
        ClearRaceEditorForm(false);
    }

    private void OnRaceEditorSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _raceEditorSearchText = RaceEditorSearchBox.Text?.Trim() ?? string.Empty;
        PopulateRaceEditorList();
    }

    private void OnRaceEditorListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RaceEditorList.SelectedItem is not RaceEditorListItem item)
            return;

        var row = AppServices.NpcPeopleCatalog.GetAllRows().FirstOrDefault(x => x.Id == item.Id);
        if (row == null)
            return;

        LoadRaceEditorRow(row);
        SetRaceEditorStatus($"Loaded {row.DisplayName}.");
    }

    private void OnRaceEditorFormTextChanged(object? sender, TextChangedEventArgs e)
    {
    }

    private void OnRaceEditorFlagsClicked(object? sender, RoutedEventArgs e)
    {
    }

    private void OnRaceEditorEnabledClicked(object? sender, RoutedEventArgs e)
    {
    }

    private void OnRaceEditorSortOrderChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
    }
}
