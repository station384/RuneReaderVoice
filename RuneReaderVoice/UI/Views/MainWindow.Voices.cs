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
    private const int VoiceGridMaxRows = 250;
    private readonly Dictionary<string, TextBlock> _voiceSummaryBlocks = new(StringComparer.OrdinalIgnoreCase);
    private string _voiceSearchText = string.Empty;

    private void SetVoicesStatus(string message)
    {
        if (VoicesStatus != null)
            VoicesStatus.Text = message;

        SessionStatus.Text = message;
    }


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

        var items = GetVoiceCatalogItems(out var totalCount);
        var introText = "Choose how RuneReader should read text for each NPC type. These settings affect RuneReader speech only and do not change the game's own audio.";
        if (totalCount > items.Count)
            introText += $" Showing first {items.Count} of {totalCount} slots. Use search to narrow results.";

        var intro = new TextBlock
        {
            Text = introText,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.LightGray,
            Margin = new Avalonia.Thickness(0, 0, 0, 8)
        };
        VoiceGrid.Children.Add(intro);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("230,*,Auto,Auto"),
                Margin = new Avalonia.Thickness(8, 4, 8, 4),
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

            var pairStripeIndex = (index / 2) % 2;
            var rowContainer = new Border
            {
                Background = pairStripeIndex == 0
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14000000"))
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#22FFFFFF")),
                CornerRadius = new Avalonia.CornerRadius(4),
                Padding = new Avalonia.Thickness(8, 4),
                Child = row
            };

            VoiceGrid.Children.Add(rowContainer);
            _voiceSummaryBlocks[item.Slot.ToString()] = summaryBlock;
        }
    }


    private IReadOnlyList<RuneReaderVoice.Data.VoiceSlotCatalogRow> GetVoiceCatalogItems(out int totalCount)
    {
        var items = AppServices.NpcPeopleCatalog?.GetVoiceSlots();
        if (items == null || items.Count == 0)
        {
            totalCount = 0;
            return Array.Empty<RuneReaderVoice.Data.VoiceSlotCatalogRow>();
        }

        var maleNarrator = VoiceSlot.MaleNarrator;
        var femaleNarrator = VoiceSlot.FemaleNarrator;
        var narrator = items.Where(x => x.Slot == VoiceSlot.Narrator || x.Slot == maleNarrator || x.Slot == femaleNarrator).ToList();
        var rest = items.Where(x => x.Slot != VoiceSlot.Narrator && x.Slot != maleNarrator && x.Slot != femaleNarrator)
            .OrderBy(x => x.NpcLabel.Split(" / ")[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Slot.Gender == Gender.Male ? 0 : x.Slot.Gender == Gender.Female ? 1 : 2)
            .ToList();
        var ordered = narrator.Concat(rest).ToList();
        if (!string.IsNullOrWhiteSpace(_voiceSearchText))
            ordered = ordered.Where(x => VoiceSlotMatchesSearch(x, _voiceSearchText)).ToList();

        totalCount = ordered.Count;
        if (ordered.Count > VoiceGridMaxRows)
            ordered = ordered.Take(VoiceGridMaxRows).ToList();

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

        var accentText = AppServices.NpcPeopleCatalog?.GetSlotAccentLabel(slot) ?? slot.SlotKey;
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

            var catalog = GetVoiceCatalogItems(out _).FirstOrDefault(x => x.Slot.Equals(slot))
                          ?? new RuneReaderVoice.Data.VoiceSlotCatalogRow(slot, GetDisplaySlotLabel(slot), AppServices.NpcPeopleCatalog?.GetSlotAccentLabel(slot) ?? slot.SlotKey, 0);
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

    private string CanonicalizeProviderProfileKey(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return string.Empty;

        return AppServices.ProviderSlotProfiles?.CanonicalizeProviderId(providerId)
               ?? providerId.Trim();
    }

    private async void OnVoicesExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sourceProviders = AppServices.ProviderSlotProfiles?.GetVoiceProfilesSnapshot()
                                  ?? AppServices.Settings.PerProviderVoiceProfiles
                                      .ToDictionary(
                                          kvp => CanonicalizeProviderProfileKey(kvp.Key),
                                          kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                                          StringComparer.OrdinalIgnoreCase);

            var providers = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerId, profiles) in sourceProviders)
            {
                var canonicalProviderId = CanonicalizeProviderProfileKey(providerId);
                if (string.IsNullOrWhiteSpace(canonicalProviderId))
                    continue;

                if (!providers.TryGetValue(canonicalProviderId, out var providerMap))
                {
                    providerMap = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    providers[canonicalProviderId] = providerMap;
                }

                foreach (var (slotId, profile) in profiles)
                {
                    if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                        continue;

                    providerMap[slotId.Trim()] = profile;
                }
            }

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
            SetVoicesStatus($"Voice export failed: {ex.Message}");
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

            Dictionary<string, Dictionary<string, VoiceProfile>>? importedProviderMap = null;
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("Providers", out _))
                {
                    importedProviderMap = JsonSerializer.Deserialize<MultiProviderVoiceProfileExport>(json)?.Providers;
                }
            }

            var importedProviders = 0;
            var importedProfiles = 0;

            if (importedProviderMap != null && importedProviderMap.Count > 0)
            {
                foreach (var (rawProviderId, profiles) in importedProviderMap)
                {
                    var providerId = CanonicalizeProviderProfileKey(rawProviderId);
                    if (string.IsNullOrWhiteSpace(providerId) || profiles == null)
                        continue;

                    var providerImportedProfiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    var providerImported = 0;
                    foreach (var (slotKey, profile) in profiles)
                    {
                        if (string.IsNullOrWhiteSpace(slotKey) || profile == null)
                            continue;

                        var trimmedSlotKey = slotKey.Trim();
                        if (!VoiceSlot.TryParse(trimmedSlotKey, out var slot))
                            continue;

                        providerImportedProfiles[slot.ToString()] = profile.Clone();
                        providerImported++;
                    }

                    if (providerImportedProfiles.Count == 0)
                        continue;

                    if (AppServices.ProviderSlotProfiles != null)
                    {
                        await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(providerId, providerImportedProfiles, "Imported");
                    }
                    else
                    {
                        AppServices.Settings.PerProviderVoiceProfiles[providerId] = providerImportedProfiles;
                    }

                    if (string.Equals(providerId, AppServices.Provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                    {
                        var runtimeDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                        foreach (var (slotId, profile) in providerImportedProfiles)
                        {
                            if (VoiceSlot.TryParse(slotId, out var runtimeSlot))
                                ApplyVoiceProfile(runtimeSlot, profile, providerId, runtimeDict);
                        }
                    }

                    importedProviders++;
                    importedProfiles += providerImported;
                }
            }
            else
            {
                var import = JsonSerializer.Deserialize<VoiceProfileExport>(json);
                if (import?.Profiles == null || import.Profiles.Count == 0)
                {
                    SetVoicesStatus("No voice profiles found in file.");
                    return;
                }

                var providerId = CanonicalizeProviderProfileKey(string.IsNullOrWhiteSpace(import.ProviderId)
                    ? AppServices.Provider.ProviderId
                    : import.ProviderId);

                var providerImportedProfiles = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (var (slotKey, profile) in import.Profiles)
                {
                    if (string.IsNullOrWhiteSpace(slotKey) || profile == null)
                        continue;

                    var trimmedSlotKey = slotKey.Trim();
                    if (!VoiceSlot.TryParse(trimmedSlotKey, out var slot))
                        continue;

                    providerImportedProfiles[slot.ToString()] = profile.Clone();
                }

                if (providerImportedProfiles.Count == 0)
                {
                    SetVoicesStatus("No voice profiles found in file.");
                    return;
                }

                if (AppServices.ProviderSlotProfiles != null)
                    await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(providerId, providerImportedProfiles, "Imported");
                else
                    AppServices.Settings.PerProviderVoiceProfiles[providerId] = providerImportedProfiles;

                if (string.Equals(providerId, AppServices.Provider.ProviderId, StringComparison.OrdinalIgnoreCase))
                {
                    var runtimeDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (slotId, profile) in providerImportedProfiles)
                    {
                        if (VoiceSlot.TryParse(slotId, out var runtimeSlot))
                            ApplyVoiceProfile(runtimeSlot, profile, providerId, runtimeDict);
                    }
                }

                importedProviders = 1;
                importedProfiles = providerImportedProfiles.Count;
            }

            if (AppServices.ProviderSlotProfiles == null)
                VoiceSettingsManager.SaveSettings(AppServices.Settings);

            PopulateVoiceGrid();
            SetVoicesStatus($"Imported {importedProfiles} voice profiles across {importedProviders} providers.");
        }
        catch (Exception ex)
        {
            SetVoicesStatus($"Voice import failed: {ex.Message}");
        }
    }

    private async void OnVoicesPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SetVoicesStatus("No server URL configured.");
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

            var upserted = await AppServices.NpcSync.PushProviderSlotProfilesAsync("voice_slot", providers);
            SetVoicesStatus(upserted >= 0 ? $"Voice profiles pushed to server ({upserted} row(s))." : "Push failed — check server logs.");
        }
        catch (Exception ex)
        {
            SetVoicesStatus($"Push failed: {ex.Message}");
        }
    }

    private async void OnVoicesPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            SetVoicesStatus("No server URL configured.");
            return;
        }

        try
        {
            var ok = await AppServices.NpcSync.PullAndApplyProviderSlotProfilesAsync("voice_slot");
            if (ok && AppServices.ProviderSlotProfiles != null)
            {
                    RefreshCurrentProviderVoiceAssignments();
            }
            SetVoicesStatus(ok ? "Voice profiles pulled from server." : "No voice profiles on server.");
        }
        catch (Exception ex)
        {
            SetVoicesStatus($"Pull failed: {ex.Message}");
        }
    }

    private static void RefreshCurrentProviderVoiceAssignments()
    {
        var providerId = AppServices.Provider.ProviderId;
        var dict = AppServices.ProviderSlotProfiles != null
            ? AppServices.ProviderSlotProfiles.GetVoiceProfilesForProvider(providerId)
            : (AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var settingsDict) ? settingsDict : null);
        if (dict == null || dict.Count == 0)
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
        var cloned = profile.Clone();
        dict[slot.ToString()] = cloned;

        var currentProviderId = AppServices.Provider.ProviderId;
        if (!string.Equals(providerId, currentProviderId, StringComparison.OrdinalIgnoreCase))
            return;

        if (AppServices.Provider is KokoroTtsProvider kokoro)
            kokoro.SetVoiceProfile(slot, cloned.Clone());

#if WINDOWS
        if (AppServices.Provider is WinRtTtsProvider winRt)
            winRt.SetVoice(slot, cloned.VoiceId);
#endif

#if LINUX
        if (AppServices.Provider is LinuxPiperTtsProvider piper)
            piper.SetModel(slot, cloned.VoiceId);
#endif
    }
}