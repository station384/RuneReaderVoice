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
using Avalonia.Threading;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private readonly DispatcherTimer _testQrTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private TestQrOverlayWindow? _testQrWindow;
    private IReadOnlyList<TestQrPacket> _testQrPackets = Array.Empty<TestQrPacket>();
    private int _testQrPacketIndex;
    private string? _testQrNonce;

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
        TestQrRaceSelector.Items.Clear();
        foreach (var option in TestQrGenerator.BuildRaceOptions())
        {
            TestQrRaceSelector.Items.Add(new ComboBoxItem
            {
                Content = option.Label,
                Tag = option,
            });
        }

        if (TestQrRaceSelector.Items.Count > 0)
        {
            var bloodElfFemale = TestQrRaceSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Content?.ToString()?.Contains("Blood Elf / Female", StringComparison.OrdinalIgnoreCase) == true);
            TestQrRaceSelector.SelectedItem = bloodElfFemale ?? TestQrRaceSelector.Items[0];
        }
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
            TestQrStatus.Text = "Select a race first.";
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
            TestQrLatencyStatus.Text = "Latency: " + snapshot.Summary;
        });
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
