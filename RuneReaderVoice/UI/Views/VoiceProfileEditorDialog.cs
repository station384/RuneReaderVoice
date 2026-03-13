// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NWaves.Effects;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Dsp;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class VoiceProfileEditorDialog : Window
{
    // ─────────────────────────────────────────────────────────────────────────
    // Types
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class VoiceChoice
    {
        public string VoiceId { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────────

    private readonly VoiceSlot    _slot;
    private readonly VoiceProfile _workingProfile;
    private bool _saved;

    private DspProfile WorkingDsp => _workingProfile.Dsp ??= new DspProfile();

    // ─────────────────────────────────────────────────────────────────────────
    // Voice controls
    // ─────────────────────────────────────────────────────────────────────────

    private readonly ComboBox    _presetCombo;
    private readonly TextBlock   _presetDescriptionText;
    private readonly RadioButton _singleVoiceRadio;
    private readonly RadioButton _blendVoiceRadio;
    private readonly Border      _singleVoiceSection;
    private readonly Border      _blendVoiceSection;
    private readonly ComboBox    _voiceCombo;
    private readonly TextBlock   _voiceSummaryText;
    private readonly TextBlock   _blendSummaryText;
    private readonly TextBlock   _languageNameText;
    private readonly Slider      _speechRateSlider;
    private readonly TextBox     _speechRateText;
    private readonly TextBox     _previewText;
    private readonly TextBlock   _summaryText;

    // ─────────────────────────────────────────────────────────────────────────
    // DSP outer section
    // ─────────────────────────────────────────────────────────────────────────

    private bool              _dspSectionExpanded;
    private readonly StackPanel    _dspSectionBody;
    private readonly TextBlock     _dspSectionArrow;
    private readonly TextBlock     _dspSummaryLine;

    // ─────────────────────────────────────────────────────────────────────────
    // DSP distortion visibility panels
    // ─────────────────────────────────────────────────────────────────────────

    private readonly StackPanel _distModePanel;
    private readonly StackPanel _distTubePanel;

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public VoiceProfileEditorDialog(
        VoiceSlot slot,
        string npcLabel,
        string accentLabel,
        VoiceProfile initialProfile)
    {
        _slot           = slot;
        _workingProfile = initialProfile.Clone();
        _workingProfile.Dsp ??= new DspProfile();

        Title  = $"Edit NPC Voice Profile — {npcLabel}";
        Width  = 860;
        Height = 720;
        MinWidth  = 780;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        // Deep clone of the incoming DSP state used as the restore-shadow for
        // per-group enable/disable. Must be a clone, NOT a reference — otherwise
        // WorkingDsp mutations would corrupt the shadow values.
        var dsp = WorkingDsp.Clone();

        // ── Presets ───────────────────────────────────────────────────────────

        var presetItems = SpeakerPresetCatalog.GetForSlot(slot).ToArray();
        _presetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = presetItems };
        _presetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        _presetDescriptionText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        var useRecommendedBtn = Btn("Use Recommended", 130); useRecommendedBtn.Click += UseRecommendedButton_Click;
        var applyPresetBtn    = Btn("Apply Preset",    110); applyPresetBtn.Click    += ApplyPresetButton_Click;

        // ── Voice mode ────────────────────────────────────────────────────────

        _singleVoiceRadio = new RadioButton { Content = "Single Voice", IsChecked = !_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode" };
        _blendVoiceRadio  = new RadioButton { Content = "Blend Voices", IsChecked  = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode" };
        _singleVoiceRadio.Checked += VoiceModeChanged;
        _blendVoiceRadio.Checked  += VoiceModeChanged;

        _voiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = KokoroTtsProvider.KnownVoices.OrderBy(v => v.Name)
                .Select(v => new VoiceChoice { VoiceId = v.VoiceId, Display = $"{v.Name} · {v.Language}" }).ToArray()
        };
        _voiceCombo.SelectionChanged += VoiceCombo_SelectionChanged;

        _voiceSummaryText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _blendSummaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

        var editBlendBtn = Btn("Edit Blend…", 110); editBlendBtn.Click += EditBlendButton_Click;

        // ── Language ──────────────────────────────────────────────────────────

        var currentLang = EspeakLanguageCatalog.All.FirstOrDefault(x => string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase));
        _languageNameText = new TextBlock { Text = currentLang?.DisplayName ?? _workingProfile.LangCode, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var chooseLangBtn = Btn("Choose…", 100); chooseLangBtn.Click += ChooseLanguageButton_Click;

        // ── Speech rate ───────────────────────────────────────────────────────

        _speechRateSlider = new Slider { Minimum = 0.50, Maximum = 1.50, Value = _workingProfile.SpeechRate, TickFrequency = 0.05, HorizontalAlignment = HorizontalAlignment.Stretch };
        _speechRateText   = new TextBox { Width = 80, Text = _workingProfile.SpeechRate.ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
        _speechRateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            _workingProfile.SpeechRate = (float)_speechRateSlider.Value;
            var t = _workingProfile.SpeechRate.ToString("0.00", Inv);
            if (_speechRateText.Text != t) _speechRateText.Text = t;
            RefreshSummary();
        };
        _speechRateText.TextChanged += (_, _) =>
        {
            if (!float.TryParse(_speechRateText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
            v = Math.Clamp(v, 0.50f, 1.50f);
            _workingProfile.SpeechRate = v;
            if (Math.Abs(_speechRateSlider.Value - v) > 0.001) _speechRateSlider.Value = v;
            RefreshSummary();
        };

        // ── DSP groups ────────────────────────────────────────────────────────

        var masterEnabledCheck = new CheckBox { Content = "Effects enabled", IsChecked = dsp.Enabled, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        masterEnabledCheck.IsCheckedChanged += (_, _) => WorkingDsp.Enabled = masterEnabledCheck.IsChecked == true;

        // Distortion sub-panels
        _distModePanel = new StackPanel { Spacing = 4, IsVisible = !dsp.TubeDistortion };
        _distTubePanel = new StackPanel { Spacing = 4, IsVisible =  dsp.TubeDistortion };

        var tubeCheck = new CheckBox { Content = "Tube model", IsChecked = dsp.TubeDistortion, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
        tubeCheck.IsCheckedChanged += (_, _) =>
        {
            WorkingDsp.TubeDistortion = tubeCheck.IsChecked == true;
            _distModePanel.IsVisible  = !WorkingDsp.TubeDistortion;
            _distTubePanel.IsVisible  =  WorkingDsp.TubeDistortion;
        };

        var distModeCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "(off)", "SoftClipping", "HardClipping", "Exponential", "FullWaveRectify", "HalfWaveRectify" },
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        distModeCombo.SelectedIndex = dsp.DistortionMode switch
        {
            DistortionMode.SoftClipping    => 1, DistortionMode.HardClipping    => 2,
            DistortionMode.Exponential     => 3, DistortionMode.FullWaveRectify => 4,
            DistortionMode.HalfWaveRectify => 5, _                              => 0
        };
        distModeCombo.SelectionChanged += (_, _) =>
            WorkingDsp.DistortionMode = distModeCombo.SelectedIndex switch
            {
                1 => DistortionMode.SoftClipping, 2 => DistortionMode.HardClipping,
                3 => DistortionMode.Exponential,  4 => DistortionMode.FullWaveRectify,
                5 => DistortionMode.HalfWaveRectify, _ => (DistortionMode?)null
            };

        _distModePanel.Children.Add(distModeCombo);
        _distModePanel.Children.Add(DspRow("In",  MakeCompactSlider(0,   40,  dsp.DistortionInputGainDb,  "0.0"," dB",v => WorkingDsp.DistortionInputGainDb  = v), "Input gain dB before clipping."));
        _distModePanel.Children.Add(DspRow("Out", MakeCompactSlider(-40, 0,   dsp.DistortionOutputGainDb, "0.0"," dB",v => WorkingDsp.DistortionOutputGainDb = v), "Output compensation dB."));
        _distTubePanel.Children.Add(DspRow("Drive", MakeCompactSlider(1,   20,  dsp.TubeDistortionDist, "0.0","",  v => WorkingDsp.TubeDistortionDist = v), "Tube character — higher = harder."));
        _distTubePanel.Children.Add(DspRow("Q",     MakeCompactSlider(-1,  0,   dsp.TubeDistortionQ,    "0.00","", v => WorkingDsp.TubeDistortionQ    = v), "Work point — linearity at low levels."));

        // Robot / Whisper combos — hop sizes must be powers of 2.
        // fftSize is derived as NextPow2(hop*2) in DspFilterChain, min 256.
        // Valid hops: 64 (fft=256), 128 (fft=256), 256 (fft=512), 512 (fft=1024)
        var robotCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "(off)", "64  — subtle", "128 — moderate", "256 — strong", "512 — maximum" }
        };
        robotCombo.SelectedIndex = dsp.RobotHopSize switch { 64 => 1, 128 => 2, 256 => 3, 512 => 4, _ => 0 };
        robotCombo.SelectionChanged += (_, _) =>
            WorkingDsp.RobotHopSize = robotCombo.SelectedIndex switch { 1 => 64, 2 => 128, 3 => 256, 4 => 512, _ => 0 };

        var whisperCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "(off)", "64  — subtle", "128 — moderate", "256 — strong", "512 — maximum" }
        };
        whisperCombo.SelectedIndex = dsp.WhisperHopSize switch { 64 => 1, 128 => 2, 256 => 3, 512 => 4, _ => 0 };
        whisperCombo.SelectionChanged += (_, _) =>
            WorkingDsp.WhisperHopSize = whisperCombo.SelectedIndex switch { 1 => 64, 2 => 128, 3 => 256, 4 => 512, _ => 0 };

        // ── Build groups ──────────────────────────────────────────────────────

        // Each group has a per-effect enable checkbox in its header.
        // When disabled the sliders are still visible (so you can see/compare
        // the setting) but the effect won't be applied at preview time.
        // We store disabled state as a sentinel on the profile fields themselves:
        // enable = restore last value; disable = zero/null the field.
        // Simpler approach: store a separate bool per group, apply only when enabled.
        // The DspProfile has no per-group bypass flags, so we handle it in the UI
        // by keeping a "shadow" of the slider value and zeroing the profile field
        // when the group is off.

        var groups = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 258 };

        groups.Children.Add(MakeGroup("Dynamics",
            isOn: dsp.CompressorThresholdDb < 0,
            onToggle: on =>
            {
                WorkingDsp.CompressorThresholdDb = on ? dsp.CompressorThresholdDb : 0f;
                WorkingDsp.CompressorRatio       = on ? dsp.CompressorRatio       : 4f;
            },
            body: MakeDspRows(
                ("Thresh", MakeCompactSlider(-60, 0,  dsp.CompressorThresholdDb, "0.0", " dB", v => WorkingDsp.CompressorThresholdDb = v), "Compressor threshold dB. Below 0 activates. -18 dB suits uneven TTS."),
                ("Ratio",  MakeCompactSlider(1,   20, dsp.CompressorRatio,       "0.0", ":1",  v => WorkingDsp.CompressorRatio        = v), "Compression ratio above threshold.")
            )));

        groups.Children.Add(MakeGroup("Pitch & Tempo",
            isOn: dsp.PitchSemitones != 0 || dsp.TempoPercent != 0,
            onToggle: on =>
            {
                WorkingDsp.PitchSemitones = on ? dsp.PitchSemitones : 0f;
                WorkingDsp.TempoPercent   = on ? dsp.TempoPercent   : 0f;
            },
            body: MakeDspRows(
                ("Pitch",   MakeCompactSlider(-12, 12, dsp.PitchSemitones, "+0.0;-0.0;0", " st", v => WorkingDsp.PitchSemitones = v), "Pitch shift in semitones. ±4 is natural; ±12 is an octave."),
                ("Tempo %", MakeCompactSlider(-50, 50, dsp.TempoPercent,   "+0;-0;0",     "%",   v => WorkingDsp.TempoPercent   = v), "Speed up (+) or slow down (-) without changing pitch (WSOLA).")
            )));

        groups.Children.Add(MakeGroup("EQ",
            isOn: dsp.HighPassHz > 0 || dsp.LowShelfDb != 0 || dsp.MidGainDb != 0 || dsp.HighShelfDb != 0 || dsp.ExciterAmount > 0,
            onToggle: on =>
            {
                WorkingDsp.HighPassHz     = on ? dsp.HighPassHz     : 0f;
                WorkingDsp.LowShelfDb     = on ? dsp.LowShelfDb     : 0f;
                WorkingDsp.MidGainDb      = on ? dsp.MidGainDb      : 0f;
                WorkingDsp.MidFrequencyHz = on ? dsp.MidFrequencyHz : 1000f;
                WorkingDsp.HighShelfDb    = on ? dsp.HighShelfDb    : 0f;
                WorkingDsp.ExciterAmount  = on ? dsp.ExciterAmount  : 0f;
            },
            body: MakeDspRows(
                ("HPF",     MakeCompactSlider(0,   500,  dsp.HighPassHz,     "0",           " Hz", v => WorkingDsp.HighPassHz     = v), "Cut below this Hz. 80–150 Hz removes mud."),
                ("Low",     MakeCompactSlider(-12, 12,   dsp.LowShelfDb,     "+0.0;-0.0;0", " dB", v => WorkingDsp.LowShelfDb     = v), "Bass shelf (~200 Hz). + = warmer, - = thinner."),
                ("Mid",     MakeCompactSlider(-12, 12,   dsp.MidGainDb,      "+0.0;-0.0;0", " dB", v => WorkingDsp.MidGainDb      = v), "Mid peak gain. + = honky/nasal, - = scooped."),
                ("Mid Hz",  MakeCompactSlider(100, 8000, dsp.MidFrequencyHz, "0",           " Hz", v => WorkingDsp.MidFrequencyHz = v), "Center frequency for the mid band."),
                ("High",    MakeCompactSlider(-12, 12,   dsp.HighShelfDb,    "+0.0;-0.0;0", " dB", v => WorkingDsp.HighShelfDb    = v), "High shelf (~5 kHz). + = bright, - = dark."),
                ("Exciter", MakeCompactSlider(0,   1,    dsp.ExciterAmount,  "0.00",        "",    v => WorkingDsp.ExciterAmount   = v), "Adds synthesized upper harmonics for presence.")
            )));

        groups.Children.Add(MakeGroup("Distortion",
            isOn: dsp.TubeDistortion || dsp.DistortionMode.HasValue || dsp.BitCrushDepth > 0,
            onToggle: on =>
            {
                WorkingDsp.TubeDistortion         = on && dsp.TubeDistortion;
                WorkingDsp.TubeDistortionDist     = on ? dsp.TubeDistortionDist     : 5f;
                WorkingDsp.TubeDistortionQ        = on ? dsp.TubeDistortionQ        : -0.2f;
                WorkingDsp.DistortionMode         = on ? dsp.DistortionMode         : null;
                WorkingDsp.DistortionInputGainDb  = on ? dsp.DistortionInputGainDb  : 0f;
                WorkingDsp.DistortionOutputGainDb = on ? dsp.DistortionOutputGainDb : 0f;
                WorkingDsp.BitCrushDepth          = on ? dsp.BitCrushDepth          : 0;
                tubeCheck.IsChecked = on && dsp.TubeDistortion;
                if (!on) distModeCombo.SelectedIndex = 0;
            },
            body: new StackPanel { Spacing = 2, Children =
            {
                tubeCheck, _distModePanel, _distTubePanel,
                DspRow("Bit", MakeCompactSlider(0, 16, (float)dsp.BitCrushDepth, "0", " bit", v => WorkingDsp.BitCrushDepth = (int)Math.Round(v)), "Bit crush. 0 = off. 8 = lo-fi. 4 = extreme glitch.")
            }}));

        groups.Children.Add(MakeGroup("Chorus",
            isOn: dsp.ChorusWet > 0,
            onToggle: on =>
            {
                WorkingDsp.ChorusWet    = on ? dsp.ChorusWet    : 0f;
                WorkingDsp.ChorusRateHz = on ? dsp.ChorusRateHz : 1.5f;
                WorkingDsp.ChorusWidth  = on ? dsp.ChorusWidth  : 0.02f;
            },
            body: MakeDspRows(
                ("Wet",   MakeCompactSlider(0,      1,     dsp.ChorusWet,    "0.00",  "",    v => WorkingDsp.ChorusWet    = v), "Mix chorus voices in. Adds thickness and shimmer."),
                ("Rate",  MakeCompactSlider(0.1f,   4,     dsp.ChorusRateHz, "0.00",  " Hz", v => WorkingDsp.ChorusRateHz = v), "LFO detuning speed."),
                ("Width", MakeCompactSlider(0.005f, 0.04f, dsp.ChorusWidth,  "0.000", " s",  v => WorkingDsp.ChorusWidth  = v), "Max delay per voice.")
            )));

        groups.Children.Add(MakeGroup("Vibrato",
            isOn: dsp.VibratoWidth > 0,
            onToggle: on =>
            {
                WorkingDsp.VibratoWidth  = on ? dsp.VibratoWidth  : 0f;
                WorkingDsp.VibratoRateHz = on ? dsp.VibratoRateHz : 2f;
            },
            body: MakeDspRows(
                ("Width", MakeCompactSlider(0,    0.02f, dsp.VibratoWidth,  "0.000", " s",  v => WorkingDsp.VibratoWidth  = v), "Pitch wobble depth. 0.005 s = subtle."),
                ("Rate",  MakeCompactSlider(0.5f, 10,    dsp.VibratoRateHz, "0.0",   " Hz", v => WorkingDsp.VibratoRateHz = v), "LFO rate. 1–3 Hz = natural. 5+ = unsettling.")
            )));

        groups.Children.Add(MakeGroup("Phaser",
            isOn: dsp.PhaserWet > 0,
            onToggle: on =>
            {
                WorkingDsp.PhaserWet    = on ? dsp.PhaserWet    : 0f;
                WorkingDsp.PhaserRateHz = on ? dsp.PhaserRateHz : 0.5f;
            },
            body: MakeDspRows(
                ("Wet",  MakeCompactSlider(0,    1, dsp.PhaserWet,    "0.00", "",    v => WorkingDsp.PhaserWet    = v), "Sweeping notch filter — psychedelic shimmer."),
                ("Rate", MakeCompactSlider(0.1f, 4, dsp.PhaserRateHz, "0.00", " Hz", v => WorkingDsp.PhaserRateHz = v), "Phaser LFO speed.")
            )));

        groups.Children.Add(MakeGroup("Flanger",
            isOn: dsp.FlangerWet > 0,
            onToggle: on =>
            {
                WorkingDsp.FlangerWet      = on ? dsp.FlangerWet      : 0f;
                WorkingDsp.FlangerRateHz   = on ? dsp.FlangerRateHz   : 0.5f;
                WorkingDsp.FlangerFeedback = on ? dsp.FlangerFeedback : 0.5f;
            },
            body: MakeDspRows(
                ("Wet",  MakeCompactSlider(0,    1,    dsp.FlangerWet,      "0.00", "",    v => WorkingDsp.FlangerWet      = v), "Metallic comb filter swoosh."),
                ("Rate", MakeCompactSlider(0.1f, 4,    dsp.FlangerRateHz,   "0.00", " Hz", v => WorkingDsp.FlangerRateHz   = v), "Flanger LFO speed."),
                ("Fb",   MakeCompactSlider(0,    0.9f, dsp.FlangerFeedback, "0.00", "",    v => WorkingDsp.FlangerFeedback = v), "Resonance. Higher = more metallic tone.")
            )));

        groups.Children.Add(MakeGroup("AutoWah",
            isOn: dsp.AutoWahWet > 0,
            onToggle: on =>
            {
                WorkingDsp.AutoWahWet   = on ? dsp.AutoWahWet   : 0f;
                WorkingDsp.AutoWahMinHz = on ? dsp.AutoWahMinHz : 300f;
                WorkingDsp.AutoWahMaxHz = on ? dsp.AutoWahMaxHz : 3000f;
            },
            body: MakeDspRows(
                ("Wet", MakeCompactSlider(0,   1,    dsp.AutoWahWet,   "0.00", "",    v => WorkingDsp.AutoWahWet   = v), "Envelope-following wah. Reacts to voice dynamics."),
                ("Min", MakeCompactSlider(20,  2000, dsp.AutoWahMinHz, "0",    " Hz", v => WorkingDsp.AutoWahMinHz = v), "Lowest wah frequency."),
                ("Max", MakeCompactSlider(500, 8000, dsp.AutoWahMaxHz, "0",    " Hz", v => WorkingDsp.AutoWahMaxHz = v), "Highest wah frequency.")
            )));

        groups.Children.Add(MakeGroup("Tremolo",
            isOn: dsp.TremoloDepth > 0,
            onToggle: on =>
            {
                WorkingDsp.TremoloDepth  = on ? dsp.TremoloDepth  : 0f;
                WorkingDsp.TremoloRateHz = on ? dsp.TremoloRateHz : 3f;
            },
            body: MakeDspRows(
                ("Depth", MakeCompactSlider(0,    1,  dsp.TremoloDepth,  "0.00", "",    v => WorkingDsp.TremoloDepth  = v), "Amplitude LFO depth. Wavering undead quality."),
                ("Rate",  MakeCompactSlider(0.5f, 12, dsp.TremoloRateHz, "0.0",  " Hz", v => WorkingDsp.TremoloRateHz = v), "Tremolo speed.")
            )));

        groups.Children.Add(MakeGroup("Echo",
            isOn: dsp.EchoDelaySeconds > 0,
            onToggle: on =>
            {
                WorkingDsp.EchoDelaySeconds = on ? dsp.EchoDelaySeconds : 0f;
                WorkingDsp.EchoFeedback     = on ? dsp.EchoFeedback     : 0.4f;
                WorkingDsp.EchoWet          = on ? dsp.EchoWet          : 0f;
            },
            body: MakeDspRows(
                ("Delay", MakeCompactSlider(0,    1,    dsp.EchoDelaySeconds, "0.000", " s", v => WorkingDsp.EchoDelaySeconds = v), "Repeat delay. 0.15–0.4 s = cave ambiance."),
                ("Fb",    MakeCompactSlider(0,    0.9f, dsp.EchoFeedback,     "0.00",  "",   v => WorkingDsp.EchoFeedback     = v), "How much each repeat feeds back."),
                ("Wet",   MakeCompactSlider(0,    1,    dsp.EchoWet,          "0.00",  "",   v => WorkingDsp.EchoWet          = v), "Echo wet/dry blend.")
            )));

        groups.Children.Add(MakeGroup("Reverb",
            isOn: dsp.ReverbWet > 0,
            onToggle: on =>
            {
                WorkingDsp.ReverbWet      = on ? dsp.ReverbWet      : 0f;
                WorkingDsp.ReverbRoomSize = on ? dsp.ReverbRoomSize : 0.5f;
                WorkingDsp.ReverbDamping  = on ? dsp.ReverbDamping  : 0.5f;
            },
            body: MakeDspRows(
                ("Wet",  MakeCompactSlider(0, 1, dsp.ReverbWet,      "0.00", "", v => WorkingDsp.ReverbWet      = v), "Wet/dry. Even 0.1 adds noticeable space."),
                ("Room", MakeCompactSlider(0, 1, dsp.ReverbRoomSize, "0.00", "", v => WorkingDsp.ReverbRoomSize = v), "Room size. Larger = longer tail."),
                ("Damp", MakeCompactSlider(0, 1, dsp.ReverbDamping,  "0.00", "", v => WorkingDsp.ReverbDamping  = v), "High-freq damping. High = warm. Low = cold.")
            )));

        groups.Children.Add(MakeGroup("Spectral",
            isOn: dsp.RobotHopSize > 0 || dsp.WhisperHopSize > 0,
            onToggle: on =>
            {
                WorkingDsp.RobotHopSize   = on ? dsp.RobotHopSize   : 0;
                WorkingDsp.WhisperHopSize = on ? dsp.WhisperHopSize : 0;
                robotCombo.SelectedIndex   = on ? dsp.RobotHopSize   switch { 64=>1,128=>2,256=>3,512=>4,_=>0 } : 0;
                whisperCombo.SelectedIndex = on ? dsp.WhisperHopSize switch { 64=>1,128=>2,256=>3,512=>4,_=>0 } : 0;
            },
            body: new StackPanel { Spacing = 4, Children =
            {
                new TextBlock { Text = "Robot", FontSize = 11, Opacity = 0.7 },
                robotCombo,
                new TextBlock { Text = "Whisper", FontSize = 11, Opacity = 0.7, Margin = new Avalonia.Thickness(0, 4, 0, 0) },
                whisperCombo,
                new TextBlock { Text = "Use one at a time. Hop = smaller → more effect.", FontSize = 10, Opacity = 0.5, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 4, 0, 0) }
            }}));

        // ── DSP outer section ─────────────────────────────────────────────────

        _dspSummaryLine  = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, Text = BuildDspSummary(), VerticalAlignment = VerticalAlignment.Center };
        _dspSectionArrow = new TextBlock { Text = "▶", VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 6, 0) };

        var dspHeaderBtn = new Button
        {
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background                 = Brushes.Transparent,
            BorderThickness            = new Avalonia.Thickness(0),
            Padding                    = new Avalonia.Thickness(0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 4,
                Children =
                {
                    _dspSectionArrow,
                    new TextBlock { Text = "Audio Effects (DSP)", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = " — ", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.4 },
                    _dspSummaryLine
                }
            }
        };
        dspHeaderBtn.Click += DspSectionHeader_Click;

        var resetDspBtn = Btn("Reset All", 80);
        resetDspBtn.Click += (_, _) => ResetDsp(masterEnabledCheck, distModeCombo, tubeCheck, robotCombo, whisperCombo);

        _dspSectionBody = new StackPanel { Spacing = 6, IsVisible = false, Children =
        {
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { masterEnabledCheck, resetDspBtn } },
            groups
        }};

        var dspCard = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding         = new Avalonia.Thickness(10),
            CornerRadius    = new Avalonia.CornerRadius(6),
            Child = new StackPanel { Spacing = 8, Children = { dspHeaderBtn, _dspSectionBody } }
        };

        // ── Preview / Summary ─────────────────────────────────────────────────

        _previewText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, Text = "The tides of fate are shifting." };
        var previewBtn = Btn("Preview", 90); previewBtn.Click += PreviewButton_Click;
        var saveBtn    = Btn("Save & Close", 120); saveBtn.Click += (_, _) => { _saved = true; Close(_workingProfile.Clone()); };
        var cancelBtn  = Btn("Cancel", 90);        cancelBtn.Click += (_, _) => Close(null);
        _summaryText   = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // ── Section cards ─────────────────────────────────────────────────────

        _singleVoiceSection = Card("Single Voice", new StackPanel { Spacing = 8, Children = { _voiceCombo, _voiceSummaryText } });
        _blendVoiceSection  = Card("Blend Voices", new StackPanel { Spacing = 8, Children = { _blendSummaryText, editBlendBtn } });

        var presetCard    = Card("Voice Preset", new StackPanel { Spacing = 8, Children = { _presetCombo, _presetDescriptionText, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { useRecommendedBtn, applyPresetBtn } } } });
        var voiceModeCard = Card("Voice Mode",   new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _singleVoiceRadio, _blendVoiceRadio } } } });
        var languageCard  = Card("Dialect / Language", new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _languageNameText, chooseLangBtn } } } });

        var rateGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,80"), ColumnSpacing = 10 };
        rateGrid.Children.Add(_speechRateSlider);
        Grid.SetColumn(_speechRateText, 1);
        rateGrid.Children.Add(_speechRateText);
        var rateCard = Card("Voice Speech Rate", rateGrid);

        var topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 10 };
        AddToGrid(topGrid, presetCard,          0, 0);
        AddToGrid(topGrid, _blendVoiceSection,  0, 1);
        AddToGrid(topGrid, voiceModeCard,       1, 0);
        AddToGrid(topGrid, languageCard,        1, 1);
        AddToGrid(topGrid, _singleVoiceSection, 2, 0);
        AddToGrid(topGrid, rateCard,            2, 1);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(12), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = $"Applies to: {npcLabel}", FontWeight = FontWeight.Bold, FontSize = 16 },
                    new TextBlock { Text = $"Accent profile: {accentLabel}", Opacity = 0.8 },
                    new TextBlock { Text = "This changes how RuneReader reads detected text for this NPC type. It does not change WoW's built-in audio or settings.", TextWrapping = TextWrapping.Wrap },
                    topGrid,
                    dspCard,
                    Card("Live Preview", new StackPanel { Spacing = 8, Children =
                    {
                        _previewText, previewBtn,
                        new TextBlock { Text = "Re-synthesizes fresh. Bypasses cache. Includes current DSP.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                    }}),
                    Card("Summary", new StackPanel { Spacing = 6, Children = { _summaryText } }),
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancelBtn, saveBtn } }
                }
            }
        };

        ApplyProfileToControls();
        TrySelectMatchingPreset();
        RefreshVoiceModeUi();
        RefreshSingleVoiceSummary();
        RefreshBlendSummary();
        RefreshSummary();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DSP section expand/collapse
    // ─────────────────────────────────────────────────────────────────────────

    private void DspSectionHeader_Click(object? sender, RoutedEventArgs e)
    {
        _dspSectionExpanded       = !_dspSectionExpanded;
        _dspSectionBody.IsVisible = _dspSectionExpanded;
        _dspSectionArrow.Text     = _dspSectionExpanded ? "▼" : "▶";
    }

    private void ResetDsp(CheckBox masterCheck, ComboBox distModeCombo, CheckBox tubeCheck, ComboBox robotCombo, ComboBox whisperCombo)
    {
        _workingProfile.Dsp        = new DspProfile();
        masterCheck.IsChecked      = true;
        tubeCheck.IsChecked        = false;
        distModeCombo.SelectedIndex = 0;
        robotCombo.SelectedIndex   = 0;
        whisperCombo.SelectedIndex = 0;
        _distModePanel.IsVisible   = true;
        _distTubePanel.IsVisible   = false;
        _dspSummaryLine.Text       = BuildDspSummary();
        RefreshSummary();
        // Note: slider thumb positions don't auto-reset (no stored Slider refs in this version).
        // Values on WorkingDsp are correct; positions drift until user moves each slider.
    }

    private string BuildDspSummary()
    {
        var d = WorkingDsp;
        if (!d.Enabled || d.IsNeutral) return "no effects";
        var parts = new System.Collections.Generic.List<string>();
        if (d.CompressorThresholdDb < 0)    parts.Add($"comp {d.CompressorThresholdDb:0}dB");
        if (d.PitchSemitones != 0)          parts.Add($"pitch {d.PitchSemitones:+0.#;-0.#}st");
        if (d.TempoPercent != 0)            parts.Add($"tempo {d.TempoPercent:+0;-0}%");
        if (d.HighPassHz > 0)               parts.Add($"hpf {d.HighPassHz:0}Hz");
        if (d.LowShelfDb != 0 || d.MidGainDb != 0 || d.HighShelfDb != 0) parts.Add("eq");
        if (d.ExciterAmount > 0)            parts.Add($"exc {d.ExciterAmount:0.0}");
        if (d.TubeDistortion)               parts.Add("tube");
        else if (d.DistortionMode.HasValue) parts.Add(d.DistortionMode.Value.ToString().ToLowerInvariant());
        if (d.BitCrushDepth > 0)            parts.Add($"{d.BitCrushDepth}bit");
        if (d.ChorusWet > 0)               parts.Add($"chor {d.ChorusWet:0.0}");
        if (d.VibratoWidth > 0)             parts.Add("vib");
        if (d.PhaserWet > 0)               parts.Add($"phs {d.PhaserWet:0.0}");
        if (d.FlangerWet > 0)              parts.Add($"fln {d.FlangerWet:0.0}");
        if (d.AutoWahWet > 0)              parts.Add($"wah {d.AutoWahWet:0.0}");
        if (d.TremoloDepth > 0)            parts.Add($"trem {d.TremoloDepth:0.0}");
        if (d.EchoDelaySeconds > 0)         parts.Add($"echo {d.EchoDelaySeconds:0.00}s");
        if (d.ReverbWet > 0)               parts.Add($"rev {d.ReverbWet:0.0}");
        if (d.RobotHopSize > 0)             parts.Add("robot");
        if (d.WhisperHopSize > 0)           parts.Add("whisper");
        return parts.Count == 0 ? "no effects" : string.Join(" · ", parts);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Voice logic (unchanged from original)
    // ─────────────────────────────────────────────────────────────────────────

    private void PresetCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        => _presetDescriptionText.Text = _presetCombo.SelectedItem is SpeakerPreset p ? p.Description : "";

    private void UseRecommendedButton_Click(object? sender, RoutedEventArgs e) { var p = SpeakerPresetCatalog.GetRecommendedForSlot(_slot); if (p != null) ApplyPreset(p); }
    private void ApplyPresetButton_Click(object? sender, RoutedEventArgs e)    { if (_presetCombo.SelectedItem is SpeakerPreset p) ApplyPreset(p); }

    private void ApplyPreset(SpeakerPreset preset)
    {
        _workingProfile.VoiceId    = preset.Profile.VoiceId;
        _workingProfile.LangCode   = preset.Profile.LangCode;
        _workingProfile.SpeechRate = preset.Profile.SpeechRate;
        ApplyProfileToControls(); RefreshVoiceModeUi(); RefreshSingleVoiceSummary(); RefreshBlendSummary(); RefreshSummary();
    }

    private void TrySelectMatchingPreset()
    {
        var match = SpeakerPresetCatalog.GetForSlot(_slot).FirstOrDefault(p =>
            string.Equals(p.Profile.VoiceId, _workingProfile.VoiceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Profile.LangCode, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(p.Profile.SpeechRate - _workingProfile.SpeechRate) < 0.001f);
        _presetCombo.SelectedItem = match ?? SpeakerPresetCatalog.GetRecommendedForSlot(_slot);
    }

    private void ApplyProfileToControls()
    {
        var isBlend = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase);
        _singleVoiceRadio.IsChecked = !isBlend;
        _blendVoiceRadio.IsChecked  =  isBlend;
        if (!isBlend) SetVoiceSelection(_workingProfile.VoiceId);
        var lang = EspeakLanguageCatalog.All.FirstOrDefault(x => string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase));
        _languageNameText.Text  = lang?.DisplayName ?? _workingProfile.LangCode;
        _speechRateSlider.Value = _workingProfile.SpeechRate;
        _speechRateText.Text    = _workingProfile.SpeechRate.ToString("0.00", Inv);
    }

    private void SetVoiceSelection(string voiceId)
    {
        var match = _voiceCombo.ItemsSource?.OfType<VoiceChoice>().FirstOrDefault(v => string.Equals(v.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase));
        if (match != null) _voiceCombo.SelectedItem = match;
    }

    private void VoiceModeChanged(object? sender, RoutedEventArgs e) { RefreshVoiceModeUi(); RefreshSummary(); }

    private void RefreshVoiceModeUi()
    {
        var isBlend = _blendVoiceRadio.IsChecked == true;
        _singleVoiceSection.IsVisible = !isBlend;
        _blendVoiceSection.IsVisible  =  isBlend;
        if (isBlend)
        {
            if (!_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
                _workingProfile.VoiceId = $"{KokoroTtsProvider.MixPrefix}{(!string.IsNullOrWhiteSpace(_workingProfile.VoiceId) ? _workingProfile.VoiceId : KokoroTtsProvider.DefaultVoiceId)}:1.0";
            RefreshBlendSummary();
        }
        else
        {
            if (_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var first = ExtractFirstVoiceId(_workingProfile.VoiceId) ?? KokoroTtsProvider.DefaultVoiceId;
                _workingProfile.VoiceId = first;
                SetVoiceSelection(first);
            }
            RefreshSingleVoiceSummary();
        }
    }

    private void VoiceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_singleVoiceRadio.IsChecked == true && _voiceCombo.SelectedItem is VoiceChoice c)
        { _workingProfile.VoiceId = c.VoiceId; RefreshSingleVoiceSummary(); RefreshSummary(); }
    }

    private void RefreshSingleVoiceSummary()
        => _voiceSummaryText.Text = _voiceCombo.SelectedItem is VoiceChoice c ? c.Display : "Select a voice.";

    private void RefreshBlendSummary()
    {
        var parts = ParseBlend(_workingProfile.VoiceId);
        if (parts.Length == 0) { _blendSummaryText.Text = "No blend configured."; return; }
        _blendSummaryText.Text = string.Join(" + ", parts.Select(p =>
        {
            var v = KokoroTtsProvider.KnownVoices.FirstOrDefault(x => string.Equals(x.VoiceId, p.voiceId, StringComparison.OrdinalIgnoreCase));
            return $"{(v?.Name ?? p.voiceId)} {p.weight * 100:0.#}%";
        }));
    }

    private void RefreshSummary()
    {
        var lang      = EspeakLanguageCatalog.All.FirstOrDefault(x => string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? _workingProfile.LangCode;
        var mode      = _blendVoiceRadio.IsChecked == true ? "Blend Voices" : "Single Voice";
        var voiceText = _blendVoiceRadio.IsChecked == true ? _blendSummaryText.Text : _voiceSummaryText.Text;
        _summaryText.Text    = $"Mode: {mode}\nVoice: {voiceText}\nDialect / Language: {lang}\nSpeech Rate: {_workingProfile.SpeechRate:0.00}x\nEffects: {BuildDspSummary()}";
        _dspSummaryLine.Text = BuildDspSummary();
    }

    private async void EditBlendButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new VoiceMixDialog(KokoroTtsProvider.KnownVoices, _workingProfile.VoiceId);
        await dialog.ShowDialog(this);
        if (!string.IsNullOrWhiteSpace(dialog.ResultSpec)) { _workingProfile.VoiceId = dialog.ResultSpec!; RefreshBlendSummary(); RefreshSummary(); }
    }

    private async void ChooseLanguageButton_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new LanguagePickerDialog(_workingProfile.LangCode);
        var sel = await dlg.ShowDialog<EspeakLanguageOption?>(this);
        if (sel == null) return;
        _workingProfile.LangCode = sel.Code;
        _languageNameText.Text   = sel.DisplayName;
        RefreshSummary();
    }

    private async void PreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AppServices.Provider is not KokoroTtsProvider kokoro) return;
        if (sender is Button btn) btn.IsEnabled = false;
        var original = kokoro.ResolveVoiceProfile(_slot);
        try
        {
            kokoro.SetVoiceProfile(_slot, _workingProfile);
            var pcm = await kokoro.SynthesizeAsync(_previewText.Text ?? string.Empty, _slot, CancellationToken.None);
            if (_workingProfile.Dsp is { IsNeutral: false } dspProfile)
                pcm = DspFilterChain.Apply(pcm, dspProfile);
            await AppServices.Player.PlayAsync(pcm, CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (!_saved) kokoro.SetVoiceProfile(_slot, original);
            if (sender is Button b) b.IsEnabled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layout helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static Button Btn(string label, double width) => new() { Content = label, Width = width };

    private static Border Card(string title, Control content) => new()
    {
        BorderThickness = new Avalonia.Thickness(1), Padding = new Avalonia.Thickness(10), CornerRadius = new Avalonia.CornerRadius(6),
        Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, content } }
    };

    /// <summary>
    /// Compact collapsible DSP group card with per-group enable checkbox in header.
    ///
    /// onToggle is called IMMEDIATELY during construction with the initial isOn value,
    /// so the profile fields are always in sync with the checkbox from the start.
    /// Callers must handle both on=true (restore/set fields) and on=false (zero fields).
    /// </summary>
    private static Border MakeGroup(string title, bool isOn, Action<bool> onToggle, Control body)
    {
        var arrow    = new TextBlock { Text = "▶", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(0, 0, 3, 0) };
        var bodyWrap = new StackPanel { Children = { body }, IsVisible = false, Margin = new Avalonia.Thickness(0, 6, 0, 0) };

        var enableCheck = new CheckBox
        {
            IsChecked = isOn,
            Margin    = new Avalonia.Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Prevent click on checkbox from also toggling the expand arrow
        enableCheck.PointerPressed += (_, e) => e.Handled = true;
        enableCheck.IsCheckedChanged += (_, _) => onToggle(enableCheck.IsChecked == true);

        // Apply initial state immediately — profile fields must match the checkbox
        // from the moment the dialog opens, not just after the first toggle.
        onToggle(isOn);

        var titleBlock = new TextBlock
        {
            Text              = title,
            FontWeight        = FontWeight.SemiBold,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        var headerContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 0,
            Children    = { arrow, titleBlock, enableCheck }
        };

        var headerBtn = new Button
        {
            HorizontalAlignment        = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background                 = Brushes.Transparent,
            BorderThickness            = new Avalonia.Thickness(0),
            Padding                    = new Avalonia.Thickness(0),
            Content                    = headerContent
        };
        headerBtn.Click += (_, _) =>
        {
            bodyWrap.IsVisible = !bodyWrap.IsVisible;
            arrow.Text         = bodyWrap.IsVisible ? "▼" : "▶";
        };

        return new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding         = new Avalonia.Thickness(8),
            CornerRadius    = new Avalonia.CornerRadius(4),
            Margin          = new Avalonia.Thickness(3),
            Child           = new StackPanel { Children = { headerBtn, bodyWrap } }
        };
    }

    private static (Slider slider, TextBlock label) MakeCompactSlider(
        float min, float max, float initial,
        string fmt, string suffix, Action<float> onChange)
    {
        var valLabel = new TextBlock { Width = 52, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = 11 };
        void Upd(float v) => valLabel.Text = v.ToString(fmt, Inv) + suffix;
        Upd(initial);

        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, Width = 100, TickFrequency = (max - min) / 50f };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            var v = (float)slider.Value;
            Upd(v);
            onChange(v);
        };
        return (slider, valLabel);
    }

    /// <summary>[label 55px] [slider 100px] [value 52px] on one row.</summary>
    private static Grid DspRow(string rowLabel, (Slider s, TextBlock l) ctrl, string tooltip)
    {
        var g   = new Grid { ColumnDefinitions = new ColumnDefinitions("55,100,*"), ColumnSpacing = 4, Margin = new Avalonia.Thickness(0, 1, 0, 1) };
        var lbl = new TextBlock { Text = rowLabel, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(lbl, tooltip); ToolTip.SetTip(ctrl.s, tooltip);
        Grid.SetColumn(ctrl.s, 1); Grid.SetColumn(ctrl.l, 2);
        g.Children.Add(lbl); g.Children.Add(ctrl.s); g.Children.Add(ctrl.l);
        return g;
    }

    private static StackPanel MakeDspRows(params (string label, (Slider s, TextBlock l) ctrl, string tooltip)[] rows)
    {
        var panel = new StackPanel { Spacing = 2 };
        foreach (var (lbl, ctrl, tip) in rows)
            panel.Children.Add(DspRow(lbl, ctrl, tip));
        return panel;
    }

    private static void AddToGrid(Grid g, Control c, int row, int col)
    { Grid.SetRow(c, row); Grid.SetColumn(c, col); g.Children.Add(c); }

    // ─────────────────────────────────────────────────────────────────────────
    // Parse helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string? ExtractFirstVoiceId(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var first = spec[KokoroTtsProvider.MixPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return null;
        var c = first.LastIndexOf(':');
        return c > 0 ? first[..c] : first;
    }

    private static (string voiceId, float weight)[] ParseBlend(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<(string, float)>();
        return spec[KokoroTtsProvider.MixPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var c = part.LastIndexOf(':');
            if (c < 0) return (part, 1f);
            return (part[..c], float.TryParse(part[(c + 1)..], System.Globalization.NumberStyles.Float, Inv, out var w) ? w : 1f);
        }).ToArray();
    }
}
