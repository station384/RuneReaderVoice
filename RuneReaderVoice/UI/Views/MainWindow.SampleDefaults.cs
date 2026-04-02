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

public partial class MainWindow
{
    private readonly JsonSerializerOptions _jsonSampleVoiceOptions = new() { WriteIndented = true };

    private async Task PopulateSampleDefaultsGridAsync()
    {
        try
        {
            SampleDefaultsGrid.Children.Clear();

            var provider = AppServices.Provider;
            var descriptor = AppServices.ProviderRegistry.Get(provider.ProviderId);
            var voices = await GetActiveProviderVoicesForUiAsync();
            var providerLabel = descriptor?.DisplayName ?? provider.ProviderId;

            SampleDefaultsStatus.Text = $"Provider: {providerLabel} — {voices.Count} voice(s)";

            if (voices.Count == 0)
            {
                SampleDefaultsGrid.Children.Add(new TextBlock
                {
                    Text = "No voices are currently loaded for the active provider.",
                    Foreground = Avalonia.Media.Brushes.Gray
                });
                return;
            }

            foreach (var voice in voices.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
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
        if (AppServices.Settings.PerProviderSampleProfiles.TryGetValue(providerId, out var dict) &&
            dict.TryGetValue(sampleId, out var stored) && stored != null)
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
        if (AppServices.Settings.PerProviderSampleProfiles.TryGetValue(provider.ProviderId, out var dict) &&
            dict.TryGetValue(sampleId, out var existing) && existing != null)
        {
            profile = existing.Clone();
        }
        else if (provider is RemoteTtsProvider remote)
        {
            profile = remote.ResolveSampleProfile(sampleId, VoiceSlot.Narrator);
        }
        else
        {
            profile = VoiceProfileDefaults.Create(sampleId);
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
            voiceSourceLabel,
            descriptor?.Controls,
            sampleProfileKey: sampleId,
            sampleProviderId: provider.ProviderId,
            isSampleDefaultsEditor: true);

        var updated = await dlg.ShowDialog<VoiceProfile?>(this);
        if (updated == null)
            return;

        if (!AppServices.Settings.PerProviderSampleProfiles.TryGetValue(provider.ProviderId, out var profiles))
        {
            profiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            AppServices.Settings.PerProviderSampleProfiles[provider.ProviderId] = profiles;
        }

        updated.VoiceId = sampleId;
        profiles[sampleId] = updated.Clone();
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
        await PopulateSampleDefaultsGridAsync();
    }

    private async void OnSampleDefaultsRefreshClicked(object? sender, RoutedEventArgs e)
        => await PopulateSampleDefaultsGridAsync();

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

            var export = new MultiProviderSampleProfileExport
            {
                Providers = AppServices.Settings.PerProviderSampleProfiles
                    .ToDictionary(kvp => kvp.Key,
                        kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase)
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
            foreach (var (providerId, profiles) in import.Providers)
            {
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
            var export = new MultiProviderSampleProfileExport
            {
                Providers = AppServices.Settings.PerProviderSampleProfiles
                    .ToDictionary(kvp => kvp.Key,
                        kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase)
            };
            var json = JsonSerializer.Serialize(export, _jsonSampleVoiceOptions);
            var ok = await AppServices.NpcSync.PushDefaultsAsync("voice-sample-profiles", json);
            SampleDefaultsStatus.Text = ok ? "Voice sample defaults pushed to server." : "Push failed — check server logs.";
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
            var ok = await AppServices.NpcSync.PullAndApplyDefaultsAsync("voice-sample-profiles");
            if (ok)
            {
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
