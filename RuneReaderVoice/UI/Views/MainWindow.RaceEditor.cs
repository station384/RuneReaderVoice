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
    private int _raceEditorPageNumber = 1;
    private int _raceEditorPageSize = 50;
    private int _raceEditorTotalCount;
    private IReadOnlyList<NpcPeopleCatalogRow> _raceEditorPageItems = Array.Empty<NpcPeopleCatalogRow>();

    private void InitRaceEditorUi()
    {
        if (RaceEditorPageSizeDropdown != null)
            RaceEditorPageSizeDropdown.SelectedIndex = 1;
        _ = ReloadRaceEditorAsync();
        ClearRaceEditorForm();
    }

    private async Task ReloadRaceEditorAsync()
    {
        await AppServices.NpcPeopleCatalog.ReloadAsync();
        await RefreshRaceEditorListAsync();
        PopulateVoiceGrid();
        await RefreshNpcOverrideUiAfterRaceCatalogChangeAsync();
    }

    private async Task RefreshRaceEditorListAsync()
    {
        var page = await AppServices.NpcPeopleCatalog.QueryPageAsync(
            _raceEditorSearchText,
            _raceEditorPageNumber,
            _raceEditorPageSize);

        _raceEditorPageItems = page.Items;
        _raceEditorTotalCount = page.TotalCount;
        _raceEditorPageNumber = page.PageNumber;
        _raceEditorPageSize = page.PageSize;

        RaceEditorList.ItemsSource = _raceEditorPageItems
            .Select(x => new RaceEditorListItem
            {
                Id = x.Id,
                DisplayText = $"{x.DisplayName}  [{x.Id}]  •  {x.AccentLabel}  •  {(x.Enabled ? "Enabled" : "Inactive")}"
            })
            .ToList();

        UpdateRaceEditorPagingUi();

        if (!string.IsNullOrWhiteSpace(_raceEditorSelectedId) && RaceEditorList.ItemsSource is IEnumerable<RaceEditorListItem> items)
        {
            RaceEditorList.SelectedItem = items.FirstOrDefault(x => x.Id == _raceEditorSelectedId);
        }
    }

    private void UpdateRaceEditorPagingUi()
    {
        var totalPages = Math.Max(1, (_raceEditorTotalCount + _raceEditorPageSize - 1) / _raceEditorPageSize);
        if (RaceEditorPageInfoText != null)
            RaceEditorPageInfoText.Text = $"Page {_raceEditorPageNumber} / {totalPages}  •  {_raceEditorTotalCount} rows";
        if (RaceEditorPrevButton != null)
            RaceEditorPrevButton.IsEnabled = _raceEditorPageNumber > 1;
        if (RaceEditorNextButton != null)
            RaceEditorNextButton.IsEnabled = _raceEditorPageNumber < totalPages;
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

        if (!keepStatus)
            SetRaceEditorStatus(string.Empty);
    }

    private void LoadRaceEditorRow(NpcPeopleCatalogRow row)
    {
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
            await RefreshRaceEditorListAsync();
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
            var row = await AppServices.NpcPeopleCatalog.GetByIdAsync(id);
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
        _raceEditorPageNumber = 1;
        _ = RefreshRaceEditorListAsync();
    }

    private void OnRaceEditorListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RaceEditorList.SelectedItem is not RaceEditorListItem item)
            return;

        var row = _raceEditorPageItems.FirstOrDefault(x => x.Id == item.Id);
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

    private void OnRaceEditorPrevPageClicked(object? sender, RoutedEventArgs e)
    {
        if (_raceEditorPageNumber <= 1)
            return;
        _raceEditorPageNumber--;
        _ = RefreshRaceEditorListAsync();
    }

    private void OnRaceEditorNextPageClicked(object? sender, RoutedEventArgs e)
    {
        var totalPages = Math.Max(1, (_raceEditorTotalCount + _raceEditorPageSize - 1) / _raceEditorPageSize);
        if (_raceEditorPageNumber >= totalPages)
            return;
        _raceEditorPageNumber++;
        _ = RefreshRaceEditorListAsync();
    }

    private void OnRaceEditorPageSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RaceEditorPageSizeDropdown?.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var size))
        {
            _raceEditorPageSize = size;
            _raceEditorPageNumber = 1;
            _ = RefreshRaceEditorListAsync();
        }
    }
}
