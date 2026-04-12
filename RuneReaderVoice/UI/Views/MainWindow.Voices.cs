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
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

// MainWindow.Voices.cs
// Voice slot grid, profile import/export, and slot-level editing flows.
public partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _voiceSummaryBlocks = new(StringComparer.OrdinalIgnoreCase);
    private string _voiceSearchText = string.Empty;


    private void OnVoiceSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _voiceSearchText = VoiceSearchBox?.Text?.Trim() ?? string.Empty;
        PopulateVoiceGrid();
    }

    private static bool ProviderRequiresExplicitVoiceSelection(ProviderDescriptor? descriptor)
        => descriptor != null && descriptor.TransportKind == ProviderTransportKind.Remote && descriptor.VoiceSourceKind == RemoteVoiceSourceKind.Samples;

    private async Task<IReadOnlyList<VoiceInfo>> GetActiveProviderVoicesForUiAsync()
    {
        try
        {
            IReadOnlyList<VoiceInfo> voices;
            if (AppServices.Provider is RemoteTtsProvider remote)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Voices] Calling RefreshVoiceSourcesAsync for {remote.ProviderId}");
                voices = await remote.RefreshVoiceSourcesAsync(CancellationToken.None);
                System.Diagnostics.Debug.WriteLine(
                    $"[Voices] RefreshVoiceSourcesAsync returned {voices.Count} voices");
            }
            else
                voices = AppServices.Provider.GetAvailableVoices();

            // Voice list just refreshed — repopulate the bespoke sample dropdown
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

        foreach (var item in GetVoiceCatalogItems())
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


    private IReadOnlyList<RuneReaderVoice.Data.VoiceSlotCatalogRow> GetVoiceCatalogItems()
    {
        var items = AppServices.NpcPeopleCatalog?.GetVoiceSlots();
        if (items == null || items.Count == 0)
            return Array.Empty<RuneReaderVoice.Data.VoiceSlotCatalogRow>();

        var maleNarrator = new VoiceSlot(AccentGroup.Narrator, Gender.Male);
        var femaleNarrator = new VoiceSlot(AccentGroup.Narrator, Gender.Female);
        var narrator = items.Where(x => x.Slot == VoiceSlot.Narrator || x.Slot == maleNarrator || x.Slot == femaleNarrator).ToList();
        var rest = items.Where(x => x.Slot != VoiceSlot.Narrator && x.Slot != maleNarrator && x.Slot != femaleNarrator)
            .OrderBy(x => x.NpcLabel.Split(" / ")[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Slot.Gender == Gender.Male ? 0 : x.Slot.Gender == Gender.Female ? 1 : 2)
            .ToList();
        var ordered = narrator.Concat(rest).ToList();
        if (!string.IsNullOrWhiteSpace(_voiceSearchText))
        {
            ordered = ordered.Where(x => VoiceSlotMatchesSearch(x, _voiceSearchText)).ToList();
        }
        return ordered;
    }


    private static bool VoiceSlotMatchesSearch(RuneReaderVoice.Data.VoiceSlotCatalogRow item, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText)) return true;

        var q = searchText.Trim();
        if (q.Length == 0) return true;

        var label = item.NpcLabel ?? string.Empty;
        var slotText = item.Slot.ToString();
        var accent = item.AccentLabel ?? string.Empty;

        return label.Contains(q, StringComparison.OrdinalIgnoreCase)
               || slotText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || accent.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private string DescribeVoiceProfile(VoiceSlot slot)
    {
        var provider   = AppServices.Provider;
        var providerId = provider.ProviderId;

        VoiceProfile? profile = null;

        if (AppServices.TryGetStoredVoiceProfile(providerId, slot, out var stored) && stored != null)
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

        var accentText = AppServices.NpcPeopleCatalog?.GetSlotAccentLabel(slot) ?? slot.Group.ToString();
        var standard   = StandardVoiceProfileCatalog.TryGetVoiceStandard(providerId, slot);

        if (standard != null && profile.CacheAffectingEquals(standard))
            return $"Standard Setup · {voiceText} · {lang} · {profile.SpeechRate * 100:0.#}%";

        return $"{voiceText} · {lang} · {profile.SpeechRate * 100:0.#}% · {accentText}";
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
        // async void — must not throw. Delegate to Task method.
        try { await VoiceProfilePreview_ClickAsync(sender, e); }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Preview failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[VoicesTab] Preview error: {ex}");
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    private async Task VoiceProfilePreview_ClickAsync(object? sender, RoutedEventArgs e)
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
        catch (Exception ex)
        {
            SessionStatus.Text = $"Preview failed: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[VoicesTab] Preview error: {ex.Message}");
        }
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
        var dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);

        btn.IsEnabled = false;
        try
        {
            var descriptor = AppServices.ProviderRegistry.Get(providerId);
            var availableVoices = await GetActiveProviderVoicesForUiAsync();

            VoiceProfile profile;
            if (AppServices.TryGetStoredVoiceProfile(providerId, slot, out var existingStored) && existingStored != null)
            {
                profile = existingStored.Clone();
            }
            else
            {
                profile = AppServices.Provider.ResolveProfile(slot)?.Clone()
                          ?? VoiceProfileDefaults.Create(string.Empty);

                if (string.IsNullOrWhiteSpace(profile.VoiceId) && !ProviderRequiresExplicitVoiceSelection(descriptor))
                    profile.VoiceId = availableVoices.FirstOrDefault()?.VoiceId ?? string.Empty;
            }

            var catalog = GetVoiceCatalogItems().FirstOrDefault(x => x.Slot.Equals(slot))
                          ?? new RuneReaderVoice.Data.VoiceSlotCatalogRow(slot, GetDisplaySlotLabel(slot), AppServices.NpcPeopleCatalog?.GetSlotAccentLabel(slot) ?? slot.Group.ToString(), 0);
            var voiceSourceLabel = descriptor?.VoiceSourceKind == RemoteVoiceSourceKind.Samples ? "sample" : "voice";
            var supportsPresets = AppServices.Provider is KokoroTtsProvider;
            // Blend is supported by local Kokoro and remote Kokoro backends.
            // Descriptor may be stale (pre-dates blend support) so also check provider ID.
            var isRemoteKokoro = AppServices.Provider is TTS.Providers.RemoteTtsProvider rp
                && rp.ProviderId.Contains("kokoro", StringComparison.OrdinalIgnoreCase);
            var supportsBlend = (descriptor?.SupportsVoiceBlending ?? false)
                             || (AppServices.Provider is KokoroTtsProvider)
                             || isRemoteKokoro;
            var dlg = new VoiceProfileEditorDialog(slot, catalog.NpcLabel, catalog.AccentLabel, profile, availableVoices, supportsPresets, supportsBlend, descriptor?.SupportsSynthesisSeed == true, voiceSourceLabel, descriptor?.Controls);
            var updated = await dlg.ShowDialog<VoiceProfile?>(this);
            if (updated == null)
                return;

            ApplyVoiceProfile(slot, updated, providerId, dict);
            await AppServices.ProviderSlotProfiles.UpsertAsync(providerId, slot.ToString(), updated, "Local");
            AppServices.ProviderSlotProfiles.WriteBackToSettings(AppServices.Settings);
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
            var providers = AppServices.ProviderSlotProfiles?.GetVoiceProfilesSnapshot()
                            ?? AppServices.Settings.PerProviderVoiceProfiles
                                .ToDictionary(
                                    kvp => kvp.Key,
                                    kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase);

            var export = new MultiProviderVoiceProfileExport
            {
                Providers = providers,
            };
            var json = JsonSerializer.Serialize(export, _jsonVoiceOptions);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var path = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export Voice Profiles",
                SuggestedFileName = "voice-profiles-all-providers.json",
                DefaultExtension  = "json",
                FileTypeChoices   = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (path == null) return;

            await File.WriteAllTextAsync(path.Path.LocalPath, json);
            var providerCount = export.Providers.Count;
            var profileCount = export.Providers.Values.Sum(x => x.Count);
            SessionStatus.Text = $"Exported {profileCount} voice profiles across {providerCount} providers.";
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

            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);

            var multi = JsonSerializer.Deserialize<MultiProviderVoiceProfileExport>(json);
            var importedProviders = 0;
            var importedProfiles = 0;

            if (multi?.Providers != null && multi.Providers.Count > 0)
            {
                foreach (var (providerId, profiles) in multi.Providers)
                {
                    if (string.IsNullOrWhiteSpace(providerId) || profiles == null)
                        continue;

                    Dictionary<string, VoiceProfile>? dict = null;
                    if (AppServices.ProviderSlotProfiles == null)
                    {
                        if (!AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out dict))
                        {
                            dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                            AppServices.Settings.PerProviderVoiceProfiles[providerId] = dict;
                        }
                    }

                    var providerImportedProfiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    var providerImported = 0;
                    foreach (var (slotKey, profile) in profiles)
                    {
                        if (!VoiceSlot.TryParse(slotKey, out var slot)) continue;
                        if (dict != null)
                            ApplyVoiceProfile(slot, profile, providerId, dict);
                        else
                            providerImportedProfiles[slot.ToString()] = profile.Clone();
                        providerImported++;
                    }

                    if (AppServices.ProviderSlotProfiles != null && providerImportedProfiles.Count > 0)
                    {
                        await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(providerId, providerImportedProfiles, "Imported");

                        if (string.Equals(providerId, AppServices.Provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                        {
                            var runtimeDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (slotId, profile) in providerImportedProfiles)
                            {
                                if (VoiceSlot.TryParse(slotId, out var runtimeSlot))
                                    ApplyVoiceProfile(runtimeSlot, profile, providerId, runtimeDict);
                            }
                        }
                    }

                    if (providerImported > 0)
                    {
                        importedProviders++;
                        importedProfiles += providerImported;
                    }
                }
            }
            else
            {
                var import = JsonSerializer.Deserialize<VoiceProfileExport>(json);
                if (import?.Profiles == null || import.Profiles.Count == 0)
                {
                    SessionStatus.Text = "No voice profiles found in file.";
                    return;
                }

                var providerId = string.IsNullOrWhiteSpace(import.ProviderId)
                    ? AppServices.Provider.ProviderId
                    : import.ProviderId;
                Dictionary<string, VoiceProfile>? dict = null;
                if (AppServices.ProviderSlotProfiles == null)
                {
                    if (!AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out dict))
                    {
                        dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                        AppServices.Settings.PerProviderVoiceProfiles[providerId] = dict;
                    }
                }

                var importedProviderProfiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var (slotKey, profile) in import.Profiles)
                {
                    if (!VoiceSlot.TryParse(slotKey, out var slot)) continue;
                    if (dict != null)
                        ApplyVoiceProfile(slot, profile, providerId, dict);
                    else
                        importedProviderProfiles[slot.ToString()] = profile.Clone();
                    importedProfiles++;
                }

                if (AppServices.ProviderSlotProfiles != null && importedProviderProfiles.Count > 0)
                {
                    await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(providerId, importedProviderProfiles, "Imported");

                    if (string.Equals(providerId, AppServices.Provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                    {
                        var runtimeDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (slotId, profile) in importedProviderProfiles)
                        {
                            if (VoiceSlot.TryParse(slotId, out var runtimeSlot))
                                ApplyVoiceProfile(runtimeSlot, profile, providerId, runtimeDict);
                        }
                    }
                }

                importedProviders = importedProfiles > 0 ? 1 : 0;
            }

            if (importedProfiles == 0)
            {
                SessionStatus.Text = "No voice profiles found in file.";
                return;
            }

            if (AppServices.ProviderSlotProfiles != null)
                AppServices.ProviderSlotProfiles.WriteBackToSettings(AppServices.Settings);
            VoiceSettingsManager.SaveSettings(AppServices.Settings);

            // Refresh summary labels
            foreach (var item in GetVoiceCatalogItems())
            {
                if (_voiceSummaryBlocks.TryGetValue(item.Slot.ToString(), out var summary))
                    summary.Text = DescribeVoiceProfile(item.Slot);
            }

            SessionStatus.Text = $"Imported {importedProfiles} voice profiles across {importedProviders} providers.";
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
            var providers = AppServices.ProviderSlotProfiles?.GetVoiceProfilesSnapshot()
                            ?? AppServices.Settings.PerProviderVoiceProfiles
                                .ToDictionary(
                                    kvp => kvp.Key,
                                    kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase);

            var export = new MultiProviderVoiceProfileExport
            {
                Providers = providers,
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
            if (ok && AppServices.ProviderSlotProfiles != null)
            {
                AppServices.ProviderSlotProfiles.WriteBackToSettings(AppServices.Settings);
                RefreshCurrentProviderVoiceAssignments();
            }
            SessionStatus.Text = ok ? "Voice profiles pulled from server." : "No voice profiles on server.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Pull failed: {ex.Message}";
        }
    }

    private static void RefreshCurrentProviderVoiceAssignments()
    {
        var providerId = AppServices.Provider.ProviderId;
        var dict = AppServices.ProviderSlotProfiles?.GetVoiceProfilesSnapshot().GetValueOrDefault(providerId)
                   ?? (AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var settingsDict) ? settingsDict : null);
        if (dict == null)
            return;

        var runtimeDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slotId, profile) in dict)
        {
            if (profile == null || !VoiceSlot.TryParse(slotId, out var slot))
                continue;

            ApplyVoiceProfile(slot, profile, providerId, runtimeDict);
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