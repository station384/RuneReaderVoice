using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _voiceSummaryBlocks = new(StringComparer.OrdinalIgnoreCase);

    private static bool ProviderRequiresExplicitVoiceSelection(ProviderDescriptor? descriptor)
        => descriptor != null && descriptor.TransportKind == ProviderTransportKind.Remote && descriptor.VoiceSourceKind == RemoteVoiceSourceKind.Samples;

    private async Task<IReadOnlyList<VoiceInfo>> GetActiveProviderVoicesForUiAsync()
    {
        try
        {
            IReadOnlyList<VoiceInfo> voices;
            if (AppServices.Provider is RemoteTtsProvider remote)
                voices = await remote.RefreshVoiceSourcesAsync(CancellationToken.None);
            else
                voices = AppServices.Provider.GetAvailableVoices();

            // Voice list just refreshed — repopulate the bespoke sample dropdown
            // in the Last NPC panel so it reflects the current sample library.
            PopulateLastNpcSampleDropdown();
            return voices;
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Voice source lookup failed: {ex.Message}";
            return Array.Empty<VoiceInfo>();
        }
    }

    private bool HasValidVoiceSelection(VoiceSlot slot)
    {
        var provider = AppServices.Provider;
        var descriptor = AppServices.ProviderRegistry.Get(provider.ProviderId);
        var profile = provider.ResolveProfile(slot);

        if (ProviderRequiresExplicitVoiceSelection(descriptor))
            return profile != null && !string.IsNullOrWhiteSpace(profile.VoiceId);

        return true;
    }


    private void PopulateVoiceGrid()
    {
        VoiceGrid.Children.Clear();
        _voiceSummaryBlocks.Clear();

        var intro = new TextBlock
        {
            Text = "Choose how RuneReader should read text for each NPC type. These settings affect RuneReader speech only and do not change the game's own audio.",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };
        VoiceGrid.Children.Add(intro);

        foreach (var item in NpcVoiceSlotCatalog.All.OrderBy(x => x.SortOrder))
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("230,*,Auto,Auto"),
                Margin = new Avalonia.Thickness(0, 2, 0, 2),
                ColumnSpacing = 10
            };

            var nameBlock = new TextBlock
            {
                Text = item.NpcLabel,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                FontWeight = Avalonia.Media.FontWeight.SemiBold
            };

            var summaryBlock = new TextBlock
            {
                Text = DescribeVoiceProfile(item.Slot),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            var previewBtn = new Button { Content = "Preview", Width = 80, Tag = item.Slot };
            previewBtn.Click += VoiceProfilePreview_Click;

            var editBtn = new Button { Content = "Edit", Width = 70, Tag = item.Slot };
            editBtn.Click += VoiceProfileEdit_Click;

            Grid.SetColumn(nameBlock,   0);
            Grid.SetColumn(summaryBlock, 1);
            Grid.SetColumn(previewBtn,  2);
            Grid.SetColumn(editBtn,     3);

            row.Children.Add(nameBlock);
            row.Children.Add(summaryBlock);
            row.Children.Add(previewBtn);
            row.Children.Add(editBtn);

            VoiceGrid.Children.Add(row);
            _voiceSummaryBlocks[item.Slot.ToString()] = summaryBlock;
        }
    }

    private string DescribeVoiceProfile(VoiceSlot slot)
    {
        var provider   = AppServices.Provider;
        var providerId = provider.ProviderId;

        VoiceProfile? profile = null;

        if (AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict) &&
            dict.TryGetValue(slot.ToString(), out var stored) &&
            stored != null)
        {
            profile = stored;
        }
        else if (provider is KokoroTtsProvider kokoro)
        {
            profile = kokoro.ResolveVoiceProfile(slot);
        }
        else
        {
            profile = provider.ResolveProfile(slot);
        }

        var descriptor = AppServices.ProviderRegistry.Get(providerId);

        if (profile == null)
            return ProviderRequiresExplicitVoiceSelection(descriptor) ? "(sample required)" : "(default)";

        var lang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
            string.Equals(x.Code, profile.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? profile.LangCode;

        var voiceText = profile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)
            ? "Blend"
            : ResolveVoiceDisplayName(provider, profile.VoiceId);

        var accentText = NpcVoiceSlotCatalog.All.FirstOrDefault(x => x.Slot.Equals(slot))?.AccentLabel ?? slot.Group.ToString();
        var preset     = SpeakerPresetCatalog.GetRecommendedForSlot(slot);

        if (preset != null &&
            string.Equals(profile.VoiceId,   preset.Profile.VoiceId,  StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.LangCode,  preset.Profile.LangCode, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(profile.SpeechRate - preset.Profile.SpeechRate) < 0.001f)
        {
            return $"{preset.DisplayName} · {lang} · {profile.SpeechRate:0.00}x";
        }

        return $"{voiceText} · {lang} · {profile.SpeechRate:0.00}x · {accentText}";
    }


    private static string ResolveVoiceDisplayName(ITtsProvider provider, string voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return "(not selected)";

        // Remote providers may not have loaded their voice/sample catalog yet during startup.
        // Do not trigger network I/O here just to render summary text.
        var available = provider.GetAvailableVoices();
        if (available.Count == 0)
            return voiceId;

        var match = available.FirstOrDefault(v =>
            string.Equals(v.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase));
        return match?.Name ?? voiceId;
    }

    private async void VoiceProfilePreview_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoiceSlot slot)
            return;

        const string previewText = @"Ehh? You there—yes, you. Come closer. I have a task, and before you ask, no, I cannot do it myself, because the last time I walked that road my knees argued with me for three full days. Listen carefully, because this matters.

Take this satchel to Bramblewatch Hollow, speak to Warden Elira, and make certain she receives it before sunset. On the way, keep to the old stone path, avoid the shallow marsh to the east, and do not, under any circumstances, answer if something in the fog calls your name. It may sound like a friend. It will not be a friend.

Now then, there are three things you must remember. First, the bridge near the hollow looks sturdy, but the center planks are rotten. Second, the crows nesting in the watchtower startle easily, and when they scatter, the bandits nearby take it as a warning. Third—and this is the part people forget—if you find a silver lantern hanging from a branch, leave it where it is and walk the other way.

Years ago, before the road fell quiet, caravans used to pass through Bramblewatch every week. Traders, pilgrims, mercenaries, storytellers—always too loud, always in a hurry, always certain the woods were only woods. Then the disappearances began, and the village learned to keep its fires low and its doors barred after dusk. Some say the land remembers old grief. Some say it is only smugglers and superstition. Me? I say a wise traveler respects both.

So go quickly, keep your wits about you, and return by the main road if you value your skin. And if Warden Elira offers you tea, be polite and decline. Her tea is strong enough to wake the dead, and we have enough restless things wandering about already.";

        if (!HasValidVoiceSelection(slot))
        {
            SessionStatus.Text = "Select a remote sample before previewing this slot.";
            return;
        }

        btn.IsEnabled = false;
        try
        {
            var audio = await GetOrCreateAudioAsync(previewText, slot);
            await AppServices.Player.PlayAsync(audio, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private async void VoiceProfileEdit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoiceSlot slot)
            return;

        var providerId = AppServices.Provider.ProviderId;
        if (!AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict))
        {
            dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            AppServices.Settings.PerProviderVoiceProfiles[providerId] = dict;
        }

        btn.IsEnabled = false;
        try
        {
            var descriptor = AppServices.ProviderRegistry.Get(providerId);
            var availableVoices = await GetActiveProviderVoicesForUiAsync();

            VoiceProfile profile;
            if (!dict.TryGetValue(slot.ToString(), out var existing) || existing == null)
            {
                profile = AppServices.Provider.ResolveProfile(slot)?.Clone()
                          ?? VoiceProfileDefaults.Create(string.Empty);

                if (string.IsNullOrWhiteSpace(profile.VoiceId) && !ProviderRequiresExplicitVoiceSelection(descriptor))
                    profile.VoiceId = availableVoices.FirstOrDefault()?.VoiceId ?? string.Empty;
            }
            else
            {
                profile = existing.Clone();
            }

            var catalog = NpcVoiceSlotCatalog.All.First(x => x.Slot.Equals(slot));
            var voiceSourceLabel = descriptor?.VoiceSourceKind == RemoteVoiceSourceKind.Samples ? "sample" : "voice";
            var supportsPresets = AppServices.Provider is KokoroTtsProvider;
            var supportsBlend = descriptor?.SupportsVoiceBlending ?? (AppServices.Provider is KokoroTtsProvider);
            var dlg = new VoiceProfileEditorDialog(slot, catalog.NpcLabel, catalog.AccentLabel, profile, availableVoices, supportsPresets, supportsBlend, voiceSourceLabel, descriptor?.Controls);
            var updated = await dlg.ShowDialog<VoiceProfile?>(this);
            if (updated == null)
                return;

            ApplyVoiceProfile(slot, updated, providerId, dict);
            VoiceSettingsManager.SaveSettings(AppServices.Settings);

            if (_voiceSummaryBlocks.TryGetValue(slot.ToString(), out var summary))
                summary.Text = DescribeVoiceProfile(slot);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ── Import / Export ───────────────────────────────────────────────────────

    // Export format: { "providerId": "kokoro", "profiles": { "SlotKey": { VoiceProfile } } }
    // VoiceProfileExport is defined in VoiceProfileModels.cs (shared with NpcSyncService)

    private static readonly JsonSerializerOptions _jsonVoiceOptions = new() { WriteIndented = true };

    private async void OnVoicesExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var providerId = AppServices.Provider.ProviderId;
            AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict);

            var export = new VoiceProfileExport
            {
                ProviderId = providerId,
                Profiles   = dict ?? new Dictionary<string, VoiceProfile>(),
            };
            var json = JsonSerializer.Serialize(export, _jsonVoiceOptions);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var path = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export Voice Profiles",
                SuggestedFileName = $"voice-profiles-{providerId}.json",
                DefaultExtension  = "json",
                FileTypeChoices   = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (path == null) return;

            await File.WriteAllTextAsync(path.Path.LocalPath, json);
            SessionStatus.Text = $"Exported {export.Profiles.Count} voice profiles.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Voice export failed: {ex.Message}";
        }
    }

    private async void OnVoicesImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import Voice Profiles",
                AllowMultiple  = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (files.Count == 0) return;

            var json   = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            var import = JsonSerializer.Deserialize<VoiceProfileExport>(json);
            if (import?.Profiles == null || import.Profiles.Count == 0)
            {
                SessionStatus.Text = "No voice profiles found in file.";
                return;
            }

            // Always import into the currently active provider's slot dictionary.
            var providerId = AppServices.Provider.ProviderId;
            if (!AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict))
            {
                dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                AppServices.Settings.PerProviderVoiceProfiles[providerId] = dict;
            }

            foreach (var (slotKey, profile) in import.Profiles)
            {
                if (!VoiceSlot.TryParse(slotKey, out var slot)) continue;
                ApplyVoiceProfile(slot, profile, providerId, dict);
            }

            VoiceSettingsManager.SaveSettings(AppServices.Settings);

            // Refresh summary labels
            foreach (var item in NpcVoiceSlotCatalog.All)
            {
                if (_voiceSummaryBlocks.TryGetValue(item.Slot.ToString(), out var summary))
                    summary.Text = DescribeVoiceProfile(item.Slot);
            }

            SessionStatus.Text = $"Imported {import.Profiles.Count} voice profiles.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Voice import failed: {ex.Message}";
        }
    }

    private async void OnVoicesPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SessionStatus.Text = "No server URL configured.";
            return;
        }

        try
        {
            var providerId = AppServices.Provider.ProviderId;
            AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict);
            var export = new VoiceProfileExport
            {
                ProviderId = providerId,
                Profiles   = dict ?? new System.Collections.Generic.Dictionary<string, VoiceProfile>(),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(export, _jsonVoiceOptions);
            var ok   = await AppServices.NpcSync.PushDefaultsAsync("voice-profiles", json);
            SessionStatus.Text = ok ? "Voice profiles pushed to server." : "Push failed — check server logs.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Push failed: {ex.Message}";
        }
    }

    private async void OnVoicesPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SessionStatus.Text = "No server URL configured.";
            return;
        }

        try
        {
            var ok = await AppServices.NpcSync.PullAndApplyDefaultsAsync("voice-profiles");
            SessionStatus.Text = ok ? "Voice profiles pulled from server." : "No voice profiles on server.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Pull failed: {ex.Message}";
        }
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private static void ApplyVoiceProfile(
        VoiceSlot slot, VoiceProfile profile,
        string providerId, Dictionary<string, VoiceProfile> dict)
    {
        dict[slot.ToString()] = profile.Clone();

        if (AppServices.Provider is KokoroTtsProvider kokoro)
            kokoro.SetVoiceProfile(slot, profile);

#if WINDOWS
        if (AppServices.Provider is WinRtTtsProvider winRt)
            winRt.SetVoice(slot, profile.VoiceId);
#endif

#if LINUX
        if (AppServices.Provider is LinuxPiperTtsProvider piper)
            piper.SetModel(slot, profile.VoiceId);
#endif
    }
}
