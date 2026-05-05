// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _testQrTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private TestQrOverlayWindow? _testQrWindow;
    private IReadOnlyList<TestQrPacket> _testQrPackets = Array.Empty<TestQrPacket>();
    private int _testQrPacketIndex;
    private string? _testQrNonce;
    private int? _testQrLatencyDialogId;
    private readonly Dictionary<(int DialogId, int SegmentIndex), PipelineLatencySnapshot> _testQrLatencySnapshots = new();
    private readonly List<(int DialogId, int SegmentIndex)> _testQrLatencyOrder = new();

    private void InitTestQrUi()
    {
        PopulateTestQrRaceSelector();
        PopulateTestQrScenarioSelector();
        TestQrIncludeNarratorCheck.IsChecked = true;
        TestQrForceGenerationCheck.IsChecked = false;
        _testQrTimer.Tick += (_, _) => AdvanceTestQrPacket();
        AppServices.PipelineLatencyChanged += OnPipelineLatencyChanged;
    }

    private void PopulateTestQrRaceSelector()
    {
        var previousSlot = (TestQrRaceSelector.SelectedItem as ComboBoxItem)?.Tag is TestQrRaceOption previous
            ? previous.Slot.SlotKey + "/" + previous.Slot.Gender
            : null;

        TestQrRaceSelector.Items.Clear();

        foreach (var option in TestQrGenerator.BuildRaceOptions(AppServices.NpcPeopleCatalog?.GetVoiceSlots()))
        {
            if (!TryDescribeValidTestQrRaceOption(option, out var label))
                continue;

            TestQrRaceSelector.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag = option,
            });
        }

        if (TestQrRaceSelector.Items.Count == 0)
        {
            TestQrRaceSelector.Items.Add(new ComboBoxItem
            {
                Content = "No race slots have a valid voice for the active provider",
                IsEnabled = false,
            });
            TestQrRaceSelector.SelectedIndex = 0;
            TestQrStatus.Text = "No QR test race slots are valid for the active provider. Check Race Voices / Voice Defaults.";
            return;
        }

        ComboBoxItem? selected = null;
        if (!string.IsNullOrWhiteSpace(previousSlot))
        {
            selected = TestQrRaceSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag is TestQrRaceOption o &&
                                     string.Equals(o.Slot.SlotKey + "/" + o.Slot.Gender, previousSlot, StringComparison.OrdinalIgnoreCase));
        }

        selected ??= TestQrRaceSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is TestQrRaceOption &&
                                 i.Content?.ToString()?.Contains("Blood Elf / Female", StringComparison.OrdinalIgnoreCase) == true);

        TestQrRaceSelector.SelectedItem = selected ?? TestQrRaceSelector.Items[0];
    }

    private bool TryDescribeValidTestQrRaceOption(TestQrRaceOption option, out string label)
    {
        // QR test slot selection must mirror the Race Voices / quick-select slot
        // rules.  Do not use provider.ResolveProfile() directly here: remote
        // providers may fall back through sample defaults, which is correct for
        // bespoke sample rendering but wrong for the race-slot selector.
        var provider = AppServices.Provider;
        var providerId = provider.ProviderId;
        var descriptor = AppServices.ProviderRegistry.Get(providerId);

        VoiceProfile? profile = null;
        if (AppServices.TryGetStoredVoiceProfile(providerId, option.Slot, out var stored) && stored != null)
        {
            profile = stored;
        }
        else if (provider is KokoroTtsProvider kokoro)
        {
            profile = kokoro.ResolveVoiceProfile(option.Slot);
        }
        else if (!ProviderRequiresExplicitVoiceSelection(descriptor))
        {
            profile = provider.ResolveProfile(option.Slot);
        }

        if (profile == null || string.IsNullOrWhiteSpace(profile.VoiceId))
        {
            if (ProviderRequiresExplicitVoiceSelection(descriptor))
            {
                label = string.Empty;
                return false;
            }

            label = $"{option.Label} — (default)";
            return true;
        }

        var voiceText = profile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)
            ? "Blend"
            : ResolveVoiceDisplayName(provider, profile.VoiceId);

        label = $"{option.Label} — {voiceText}";
        return true;
    }

    private void PopulateTestQrScenarioSelector()
    {
        TestQrScenarioSelector.Items.Clear();
        TestQrScenarioSelector.Items.Add(new ComboBoxItem { Content = "Small", Tag = TestQrScenario.Small });
        TestQrScenarioSelector.Items.Add(new ComboBoxItem { Content = "Medium", Tag = TestQrScenario.Medium });
        TestQrScenarioSelector.Items.Add(new ComboBoxItem { Content = "Full", Tag = TestQrScenario.Full });
        TestQrScenarioSelector.SelectedIndex = 0;
    }

    private void OnTestQrToggleClicked(object? sender, RoutedEventArgs e)
    {
        if (_testQrWindow != null)
        {
            StopTestQrOverlay();
            return;
        }

        StartTestQrOverlay();
    }

    private void OnTestQrOptionsChanged (object? sender, RoutedEventArgs e)
        
    {
        if (_uiInitializing) return;
        if (_testQrWindow == null) return;

        BuildTestQrPackets(reuseNonce: true);
        ShowCurrentTestQrPacket();
    }

    private void StartTestQrOverlay()
    {
        if (!BuildTestQrPackets(reuseNonce: false))
            return;

        ClearTestQrLatencyRows();

        _testQrWindow = new TestQrOverlayWindow();
        _testQrWindow.Closed += (_, _) => StopTestQrOverlay();
        _testQrWindow.Show();

        TestQrToggleButton.Content = "Hide Test QR";
        TestQrStatus.Text = $"Showing {_testQrPackets.Count} packet(s).";
        ShowCurrentTestQrPacket();
        _testQrTimer.Start();
    }

    private void StopTestQrOverlay()
    {
        _testQrTimer.Stop();
        var window = _testQrWindow;
        _testQrWindow = null;
        _testQrPackets = Array.Empty<TestQrPacket>();
        _testQrPacketIndex = 0;
        _testQrNonce = null;

        if (window != null)
        {
            try { window.Close(); } catch { }
        }

        TestQrToggleButton.Content = "Show Test QR";
        TestQrStatus.Text = "QR test hidden.";
    }

    private bool BuildTestQrPackets(bool reuseNonce)
    {
        if (TestQrRaceSelector.SelectedItem is not ComboBoxItem raceItem || raceItem.Tag is not TestQrRaceOption race)
        {
            TestQrStatus.Text = "Select a valid race slot first.";
            return false;
        }

        if (TestQrScenarioSelector.SelectedItem is not ComboBoxItem scenarioItem || scenarioItem.Tag is not TestQrScenario scenario)
        {
            TestQrStatus.Text = "Select a test size first.";
            return false;
        }

        var forceGeneration = TestQrForceGenerationCheck.IsChecked == true;
        if (!forceGeneration)
        {
            _testQrNonce = null;
        }
        else if (!reuseNonce || string.IsNullOrWhiteSpace(_testQrNonce))
        {
            _testQrNonce = BuildSpeakableTestTimestamp(DateTime.Now);
        }

        _testQrPackets = TestQrGenerator.BuildPackets(new TestQrBuildOptions(
            race,
            scenario,
            TestQrIncludeNarratorCheck.IsChecked == true,
            _testQrNonce));

        _testQrPacketIndex = 0;
        TestQrStatus.Text = forceGeneration
            ? $"Generated {_testQrPackets.Count} packet(s), nonce {_testQrNonce}."
            : $"Generated {_testQrPackets.Count} packet(s).";
        return _testQrPackets.Count > 0;
    }

    private void AdvanceTestQrPacket()
    {
        if (_testQrWindow == null || _testQrPackets.Count == 0)
            return;

        _testQrPacketIndex = (_testQrPacketIndex + 1) % _testQrPackets.Count;
        ShowCurrentTestQrPacket();
    }

    private void ShowCurrentTestQrPacket()
    {
        if (_testQrWindow == null || _testQrPackets.Count == 0)
            return;

        var packet = _testQrPackets[_testQrPacketIndex];
        _testQrWindow.SetQrText(
            packet.EncodedQrText,
            $"SEQ {packet.SeqIndex + 1}/{packet.SeqTotal}  SUB {packet.SubIndex + 1}/{packet.SubTotal}");
        UpdateTestQrDebug(packet);
    }


    private void UpdateTestQrDebug(TestQrPacket packet)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Packet {_testQrPacketIndex + 1}/{_testQrPackets.Count}");
        sb.AppendLine($"SEQ {packet.SeqIndex + 1}/{packet.SeqTotal}  SUB {packet.SubIndex + 1}/{packet.SubTotal}");
        sb.AppendLine();
        sb.AppendLine("Raw RV packet:");
        sb.AppendLine(packet.RawPacket);
        sb.AppendLine();
        sb.AppendLine("Base45 QR text:");
        sb.AppendLine(packet.EncodedQrText);
        sb.AppendLine();
        sb.AppendLine("Decoded text payload:");
        sb.AppendLine(packet.TextPayload.TrimEnd());
        TestQrDebugText.Text = sb.ToString();
    }

    private async void OnCopyTestQrDebugClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
                await clipboard.SetTextAsync(TestQrDebugText.Text ?? string.Empty);
        }
        catch
        {
            // Debug copy is best-effort only.
        }
    }

    private void OnPipelineLatencyChanged(PipelineLatencySnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_testQrLatencyDialogId != snapshot.DialogId)
                ClearTestQrLatencyRows(snapshot.DialogId);

            var key = (snapshot.DialogId, snapshot.SegmentIndex);
            if (!_testQrLatencySnapshots.ContainsKey(key))
                _testQrLatencyOrder.Add(key);

            _testQrLatencySnapshots[key] = snapshot;
            RebuildTestQrLatencyRows();
        });
    }

    private void ClearTestQrLatencyRows(int? activeDialogId = null)
    {
        _testQrLatencyDialogId = activeDialogId;
        _testQrLatencySnapshots.Clear();
        _testQrLatencyOrder.Clear();
        TestQrLatencyRows.Children.Clear();
    }

    private void RebuildTestQrLatencyRows()
    {
        TestQrLatencyRows.Children.Clear();

        var completedTotals = _testQrLatencyOrder
            .Select(k => _testQrLatencySnapshots.TryGetValue(k, out var s) ? s.TotalMs : null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();
        var avgTotal = completedTotals.Count > 0 ? completedTotals.Average() : (double?)null;

        foreach (var key in _testQrLatencyOrder)
        {
            if (!_testQrLatencySnapshots.TryGetValue(key, out var snapshot))
                continue;

            TestQrLatencyRows.Children.Add(BuildLatencyRow(snapshot, avgTotal));
        }
    }

    private static Grid BuildLatencyRow(PipelineLatencySnapshot snapshot, double? avgTotalMs)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("44,60,66,66,78,78,62"),
        };

        AddLatencyCell(row, 0, snapshot.SegmentIndex.ToString("000"));
        AddLatencyCell(row, 1, FormatFixedMs(snapshot.ScanToAssembleMs, 3));
        AddLatencyCell(row, 2, FormatFixedMs(snapshot.AssembleToTtsStartMs, 5));
        AddLatencyCell(row, 3, FormatFixedSeconds(snapshot.TtsStartToAudioStartMs));
        AddLatencyCell(row, 4, FormatFixedSeconds(snapshot.TotalMs));
        AddLatencyCell(row, 5, FormatFixedSeconds(avgTotalMs));
        AddLatencyCell(row, 6, snapshot.CacheState.PadRight(4));

        return row;
    }

    private static void AddLatencyCell(Grid row, int column, string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = "Consolas",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(tb, column);
        row.Children.Add(tb);
    }

    private static string FormatFixedMs(double? value, int digits)
    {
        if (!value.HasValue)
            return new string('-', digits) + "ms";

        var rounded = (int)Math.Round(Math.Clamp(value.Value, 0, Math.Pow(10, digits) - 1));
        return rounded.ToString(new string('0', digits)) + "ms";
    }

    private static string FormatFixedSeconds(double? valueMs)
    {
        if (!valueMs.HasValue)
            return "----.-s";

        var seconds = Math.Clamp(valueMs.Value / 1000.0, 0, 9999.9);
        return seconds.ToString("0000.0") + "s";
    }

    private static string BuildSpeakableTestTimestamp(DateTime now)
        => $"{now:MMMM} {now.Day}{GetOrdinalSuffix(now.Day)} {now:yyyy h:mm tt}";

    private static string GetOrdinalSuffix(int day)
    {
        var mod100 = day % 100;
        if (mod100 is >= 11 and <= 13)
            return "th";

        return (day % 10) switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };
    }

}
