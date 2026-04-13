// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.



using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;
// MainWindow.SampleDefaults.cs
// Voice Defaults tab UI, import/export, and server sync for per-sample defaults.
public partial class MainWindow
{
    private readonly JsonSerializerOptions _jsonSampleVoiceOptions = new() { WriteIndented = true };
    private int _sampleDefaultsPageNumber = 1;
    private int _sampleDefaultsPageSize = 25;

    private async Task PopulateSampleDefaultsGridAsync()
    {
        try
        {
            SampleDefaultsGrid.Children.Clear();

            var provider = AppServices.Provider;
            var descriptor = AppServices.ProviderRegistry.Get(provider.ProviderId);
            var voices = (await GetActiveProviderVoicesForUiAsync())
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(v => v.VoiceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var providerLabel = descriptor?.DisplayName ?? provider.ProviderId;

            var totalCount = voices.Count;
            var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)_sampleDefaultsPageSize));
            if (_sampleDefaultsPageNumber > totalPages)
                _sampleDefaultsPageNumber = totalPages;
            if (_sampleDefaultsPageNumber < 1)
                _sampleDefaultsPageNumber = 1;

            var pageVoices = voices
                .Skip((_sampleDefaultsPageNumber - 1) * _sampleDefaultsPageSize)
                .Take(_sampleDefaultsPageSize)
                .ToList();

            SampleDefaultsStatus.Text = $"Provider: {providerLabel} — {totalCount} voice(s)";
            SampleDefaultsPageInfoText.Text = $"Page {_sampleDefaultsPageNumber} / {totalPages}";
            SampleDefaultsPrevButton.IsEnabled = _sampleDefaultsPageNumber > 1;
            SampleDefaultsNextButton.IsEnabled = _sampleDefaultsPageNumber < totalPages;

            if (SampleDefaultsPageSizeDropdown.SelectedItem == null)
            {
                foreach (var item in SampleDefaultsPageSizeDropdown.Items.OfType<ComboBoxItem>())
                {
                    if (string.Equals(item.Tag?.ToString(), _sampleDefaultsPageSize.ToString(), StringComparison.Ordinal))
                    {
                        SampleDefaultsPageSizeDropdown.SelectedItem = item;
                        break;
                    }
                }
            }

            if (totalCount == 0)
            {
                SampleDefaultsGrid.Children.Add(new TextBlock
                {
                    Text = "No voices are currently loaded for the active provider.",
                    Foreground = Avalonia.Media.Brushes.Gray
                });
                return;
            }

            foreach (var voice in pageVoices)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("280,*,Auto"),
                    ColumnSpacing = 10,
                    Margin = new Avalonia.Thickness(0, 2, 0, 2)
                };

                var name = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(voice.Name) ? voice.VoiceId : $"{voice.Name} ({voice.VoiceId})",
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    FontWeight = Avalonia.Media.FontWeight.SemiBold,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };

                var summary = new TextBlock
                {
                    Text = DescribeSampleProfile(provider.ProviderId, voice.VoiceId),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };

                var editBtn = new Button { Content = "Edit", Width = 70, Tag = voice.VoiceId };
                editBtn.Click += OnSampleDefaultEditClicked;

                Grid.SetColumn(name, 0);
                Grid.SetColumn(summary, 1);
                Grid.SetColumn(editBtn, 2);
                row.Children.Add(name);
                row.Children.Add(summary);
                row.Children.Add(editBtn);
                SampleDefaultsGrid.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            SampleDefaultsStatus.Text = $"Voice defaults refresh failed: {ex.Message}";
        }
    }

    private string DescribeSampleProfile(string providerId, string sampleId)
    {
        if (AppServices.TryGetStoredSampleProfile(providerId, sampleId, out var stored) && stored != null)
        {
            var lang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
                string.Equals(x.Code, stored.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? stored.LangCode;
            var dsp = stored.Dsp is { IsNeutral: false } ? "DSP" : "flat";
            return $"{lang} · {stored.SpeechRate * 100:0.#}% · {dsp}";
        }
        return "(uses built-in defaults)";
    }

    private async void OnSampleDefaultEditClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string sampleId)
            return;

        var provider = AppServices.Provider;
        var descriptor = AppServices.ProviderRegistry.Get(provider.ProviderId);
        var availableVoices = await GetActiveProviderVoicesForUiAsync();

        VoiceProfile profile;
        if (AppServices.TryGetStoredSampleProfile(provider.ProviderId, sampleId, out var existing) && existing != null)
        {
            profile = existing.Clone();
        }
        else
        {
            profile = CreateDefaultSampleProfile(sampleId);
        }
        profile.VoiceId = sampleId;

        var voiceSourceLabel = descriptor?.VoiceSourceKind == RemoteVoiceSourceKind.Samples ? "sample" : "voice";
        var dlg = new VoiceProfileEditorDialog(
            VoiceSlot.Narrator,
            sampleId,
            "Voice Default",
            profile,
            availableVoices,
            supportsPresets: false,
            supportsBlend: descriptor?.SupportsVoiceBlending == true,
            supportsSynthesisSeed: descriptor?.SupportsSynthesisSeed == true,
            voiceSourceLabel,
            descriptor?.Controls,
            sampleProfileKey: sampleId,
            sampleProviderId: provider.ProviderId,
            isSampleDefaultsEditor: true);

        var updated = await dlg.ShowDialog<VoiceProfile?>(this);
        if (updated == null)
            return;

        updated.VoiceId = sampleId;
        if (AppServices.ProviderSlotProfiles != null)
        {
            await AppServices.ProviderSlotProfiles.UpsertSampleAsync(provider.ProviderId, sampleId, updated.Clone(), "Local");
        }
        else
        {
            if (!AppServices.Settings.PerProviderSampleProfiles.TryGetValue(provider.ProviderId, out var profiles))
            {
                profiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                AppServices.Settings.PerProviderSampleProfiles[provider.ProviderId] = profiles;
            }

            profiles[sampleId] = updated.Clone();
        }
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
        await PopulateSampleDefaultsGridAsync();
    }

    private static VoiceProfile CreateDefaultSampleProfile(string sampleId)
    {
        var profile = VoiceProfileDefaults.Create(sampleId);
        profile.Dsp = new DspProfile
        {
            Enabled = true,
            Effects = new List<DspEffectItem>
            {
                new()
                {
                    Kind = DspEffectKind.Air,
                    Enabled = true,
                    Params = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["amount"] = 0.6f,
                    }
                },
                new()
                {
                    Kind = DspEffectKind.Room,
                    Enabled = true,
                    Params = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["roomSize"] = 0.1125f,
                        ["damping"] = 0.9f,
                        ["wet"] = 0.1f,
                    }
                }
            }
        };
        return profile;
    }

    private async void OnSampleDefaultsRefreshClicked(object? sender, RoutedEventArgs e)
    {
        _sampleDefaultsPageNumber = 1;
        await PopulateSampleDefaultsGridAsync();
    }

    private async void OnSampleDefaultsPrevPageClicked(object? sender, RoutedEventArgs e)
    {
        if (_sampleDefaultsPageNumber <= 1)
            return;

        _sampleDefaultsPageNumber--;
        await PopulateSampleDefaultsGridAsync();
    }

    private async void OnSampleDefaultsNextPageClicked(object? sender, RoutedEventArgs e)
    {
        _sampleDefaultsPageNumber++;
        await PopulateSampleDefaultsGridAsync();
    }

    private async void OnSampleDefaultsPageSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SampleDefaultsPageSizeDropdown.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var pageSize)
            && pageSize > 0)
        {
            _sampleDefaultsPageSize = pageSize;
            _sampleDefaultsPageNumber = 1;
            await PopulateSampleDefaultsGridAsync();
        }
    }

    private async void OnSampleDefaultsExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export Voice Sample Defaults",
                SuggestedFileName = "voice-sample-profiles.json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
            });
            if (file == null) return;

            var providers = AppServices.ProviderSlotProfiles?.GetSampleProfilesSnapshot()
                            ?? AppServices.Settings.PerProviderSampleProfiles
                                .ToDictionary(kvp => kvp.Key,
                                    kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase);

            var export = new MultiProviderSampleProfileExport
            {
                Providers = providers
            };
            await using var stream = await file.OpenWriteAsync();
            await JsonSerializer.SerializeAsync(stream, export, _jsonSampleVoiceOptions);
            SampleDefaultsStatus.Text = "Voice sample defaults exported.";
        }
        catch (Exception ex)
        {
            SampleDefaultsStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnSampleDefaultsImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Voice Sample Defaults",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } }
            });
            var file = files.FirstOrDefault();
            if (file == null) return;
            await using var stream = await file.OpenReadAsync();
            var import = await JsonSerializer.DeserializeAsync<MultiProviderSampleProfileExport>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (import?.Providers == null || import.Providers.Count == 0)
            {
                SampleDefaultsStatus.Text = "No voice sample defaults found in file.";
                return;
            }
            if (AppServices.ProviderSlotProfiles != null)
            {
                foreach (var (providerId, profiles) in import.Providers)
                {
                    if (string.IsNullOrWhiteSpace(providerId) || profiles == null)
                        continue;

                    await AppServices.ProviderSlotProfiles.ReplaceSampleProfilesAsync(
                        providerId,
                        new Dictionary<string, VoiceProfile>(profiles, StringComparer.OrdinalIgnoreCase),
                        "Import");
                }

                }
            else
            {
                foreach (var (providerId, profiles) in import.Providers)
                    AppServices.Settings.PerProviderSampleProfiles[providerId] = new Dictionary<string, VoiceProfile>(profiles, StringComparer.OrdinalIgnoreCase);
            }
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
            await PopulateSampleDefaultsGridAsync();
            SampleDefaultsStatus.Text = $"Imported voice sample defaults for {import.Providers.Count} provider(s).";
        }
        catch (Exception ex)
        {
            SampleDefaultsStatus.Text = $"Import failed: {ex.Message}";
        }
    }

    private async void OnSampleDefaultsPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SampleDefaultsStatus.Text = "No server URL configured.";
            return;
        }
        try
        {
            var providers = AppServices.ProviderSlotProfiles?.GetSampleProfilesSnapshot()
                            ?? AppServices.Settings.PerProviderSampleProfiles
                                .ToDictionary(kvp => kvp.Key,
                                    kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase);

            var upserted = await AppServices.NpcSync.PushProviderSlotProfilesAsync("sample", providers);
            SampleDefaultsStatus.Text = upserted >= 0 ? $"Voice sample defaults pushed to server ({upserted} row(s))." : "Push failed — check server logs.";
        }
        catch (Exception ex)
        {
            SampleDefaultsStatus.Text = $"Push failed: {ex.Message}";
        }
    }

    private async void OnSampleDefaultsPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SampleDefaultsStatus.Text = "No server URL configured.";
            return;
        }
        try
        {
            var ok = await AppServices.NpcSync.PullAndApplyProviderSlotProfilesAsync("sample");
            if (ok)
            {
                if (AppServices.ProviderSlotProfiles != null)
                        await PopulateSampleDefaultsGridAsync();
                SampleDefaultsStatus.Text = "Voice sample defaults pulled from server.";
            }
            else
            {
                SampleDefaultsStatus.Text = "No voice sample defaults on server.";
            }
        }
        catch (Exception ex)
        {
            SampleDefaultsStatus.Text = $"Pull failed: {ex.Message}";
        }
    }
}