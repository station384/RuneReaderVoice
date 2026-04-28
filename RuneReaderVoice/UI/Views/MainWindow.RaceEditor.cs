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

    private async Task TrySyncRaceCatalogToServerAsync(string successMessage)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
            return;

        try
        {
            var ok = await AppServices.NpcSync.PushNpcPeopleCatalogAsync();
            if (ok)
                SetRaceEditorStatus(successMessage);
            else
                SetRaceEditorStatus(successMessage + " Server sync failed.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus(successMessage + $" Server sync failed: {ex.Message}");
        }
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
            var status = $"Saved {row.DisplayName}.";
            SetRaceEditorStatus(status);
            await TrySyncRaceCatalogToServerAsync(status);
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
            var status = enabled ? $"Activated {id}." : $"Deactivated {id}.";
            SetRaceEditorStatus(status);
            await TrySyncRaceCatalogToServerAsync(status);
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

    private async void OnRaceEditorPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SetRaceEditorStatus("No server URL configured.");
            return;
        }

        try
        {
            var ok = await AppServices.NpcSync.PushNpcPeopleCatalogAsync();
            SetRaceEditorStatus(ok ? "Race catalog pushed to server." : "Push failed — check server logs.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Push failed: {ex.Message}");
        }
    }

    private async void OnRaceEditorPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SetRaceEditorStatus("No server URL configured.");
            return;
        }

        try
        {
            var ok = await AppServices.NpcSync.PullAndApplyNpcPeopleCatalogAsync();
            if (ok)
                await ReloadRaceEditorAsync();

            SetRaceEditorStatus(ok ? "Race catalog pulled from server." : "No race catalog on server.");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Pull failed: {ex.Message}");
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

    private sealed class RaceCatalogJsonFile
    {
        public List<NpcPeopleCatalogRow> Rows { get; set; } = new();
    }

    private async void OnRaceEditorExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export Race Catalog",
                SuggestedFileName = "race-catalog.json",
                FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
            });
            if (file == null) return;

            var rows = (await AppServices.NpcPeopleCatalog.QueryPageAsync(null, 1, 500)).Items.ToList();
            var payload = new RaceCatalogJsonFile { Rows = rows };
            await using var stream = await file.OpenWriteAsync();
            await System.Text.Json.JsonSerializer.SerializeAsync(stream, payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            SetRaceEditorStatus($"Exported {rows.Count} race catalog row(s).");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Export failed: {ex.Message}");
        }
    }

    private async void OnRaceEditorImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import Race Catalog",
                AllowMultiple = false,
                FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
            });
            var file = files.FirstOrDefault();
            if (file == null) return;

            await using var stream = await file.OpenReadAsync();
            var payload = await System.Text.Json.JsonSerializer.DeserializeAsync<RaceCatalogJsonFile>(stream,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload?.Rows == null || payload.Rows.Count == 0)
            {
                SetRaceEditorStatus("No race catalog rows found in file.");
                return;
            }

            foreach (var row in payload.Rows)
                await AppServices.NpcPeopleCatalog.UpsertAsync(row);

            await ReloadRaceEditorAsync();
            SetRaceEditorStatus($"Imported {payload.Rows.Count} race catalog row(s).");
        }
        catch (Exception ex)
        {
            SetRaceEditorStatus($"Import failed: {ex.Message}");
        }
    }
}
