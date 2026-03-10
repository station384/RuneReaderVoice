using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private readonly Dictionary<string, TextBlock> _voiceSummaryBlocks = new(StringComparer.OrdinalIgnoreCase);

    private void PopulateVoiceGrid()
    {
        VoiceGrid.Children.Clear();
        _voiceSummaryBlocks.Clear();

        var intro = new TextBlock
        {
            Text = "Choose how RuneReader should read text for each NPC type. These settings affect RuneReader speech only and do not change the game’s own audio.",
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

            var previewBtn = new Button
            {
                Content = "Preview",
                Width = 80,
                Tag = item.Slot
            };
            previewBtn.Click += VoiceProfilePreview_Click;

            var editBtn = new Button
            {
                Content = "Edit",
                Width = 70,
                Tag = item.Slot
            };
            editBtn.Click += VoiceProfileEdit_Click;

            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(summaryBlock, 1);
            Grid.SetColumn(previewBtn, 2);
            Grid.SetColumn(editBtn, 3);

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
        var provider = AppServices.Provider;
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

        if (profile == null)
            return "(default)";

        var lang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
            string.Equals(x.Code, profile.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? profile.LangCode;

        var voiceText = profile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)
            ? "Blend"
            : profile.VoiceId;

        var accentText = NpcVoiceSlotCatalog.All.FirstOrDefault(x => x.Slot.Equals(slot))?.AccentLabel ?? slot.Group.ToString();
        var preset = SpeakerPresetCatalog.GetRecommendedForSlot(slot);

        if (preset != null && string.Equals(profile.VoiceId, preset.Profile.VoiceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile.LangCode, preset.Profile.LangCode, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(profile.SpeechRate - preset.Profile.SpeechRate) < 0.001f)
        {
            return $"{preset.DisplayName} · {lang} · {profile.SpeechRate:0.00}x";
        }

        return $"{voiceText} · {lang} · {profile.SpeechRate:0.00}x · {accentText}";
    }

    private async void VoiceProfilePreview_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoiceSlot slot)
            return;

        const string previewText = "The tides of fate are shifting.";
        var audioPath = await GetOrCreateAudioPathAsync(previewText, slot);
        await AppServices.Player.PlayAsync(audioPath, CancellationToken.None);
    }

    private async void VoiceProfileEdit_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not VoiceSlot slot)
            return;

        var providerId = AppServices.Provider.ProviderId;
        if (!AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(providerId, out var dict))
        {
            dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            AppServices.Settings.PerProviderVoiceProfiles[providerId] = dict;
        }

        VoiceProfile profile;
        if (!dict.TryGetValue(slot.ToString(), out var existing) || existing == null)
        {
            profile = AppServices.Provider switch
            {
                KokoroTtsProvider kokoro => kokoro.ResolveVoiceProfile(slot).Clone(),
                _ => VoiceProfileDefaults.Create("")
            };
        }
        else
        {
            profile = existing.Clone();
        }

        var catalog = NpcVoiceSlotCatalog.All.First(x => x.Slot.Equals(slot));
        var dlg = new VoiceProfileEditorDialog(slot, catalog.NpcLabel, catalog.AccentLabel, profile);
        var updated = await dlg.ShowDialog<VoiceProfile?>(this);
        if (updated == null)
            return;

        dict[slot.ToString()] = updated.Clone();

        if (AppServices.Provider is KokoroTtsProvider kokoroProvider)
            kokoroProvider.SetVoiceProfile(slot, updated);

#if WINDOWS
        if (AppServices.Provider is WinRtTtsProvider winRt)
            winRt.SetVoice(slot, updated.VoiceId);
#endif

#if LINUX
        if (AppServices.Provider is LinuxPiperTtsProvider piper)
            piper.SetModel(slot, updated.VoiceId);
#endif

        VoiceSettingsManager.SaveSettings(AppServices.Settings);

        if (_voiceSummaryBlocks.TryGetValue(slot.ToString(), out var summary))
            summary.Text = DescribeVoiceProfile(slot);
    }
}
