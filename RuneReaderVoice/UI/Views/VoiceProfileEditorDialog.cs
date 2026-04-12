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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        public string Summary { get; init; } = string.Empty;
        public override string ToString() => Display;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // State
    // ─────────────────────────────────────────────────────────────────────────

    private readonly VoiceSlot    _slot;
    private readonly VoiceProfile _workingProfile;
    private readonly string? _sampleProfileKey;
    private readonly string? _sampleProviderId;
    private bool _saved;


    // ─────────────────────────────────────────────────────────────────────────
    // Voice controls
    // ─────────────────────────────────────────────────────────────────────────

    private readonly ComboBox    _presetCombo;
    private readonly TextBlock   _presetDescriptionText;
    private readonly TextBlock   _standardStateText;
    private readonly VoiceProfile? _standardProfile;
    private readonly RadioButton _singleVoiceRadio;
    private readonly RadioButton _blendVoiceRadio;
    private readonly Border      _singleVoiceSection;
    private readonly Border      _blendVoiceSection;
    private readonly ComboBox    _voiceBaseCombo;
    private readonly ComboBox    _voiceVariantCombo;
    private readonly TextBlock   _voiceSummaryText;
    private readonly string      _voiceSourceLabel;
    private readonly TextBlock   _blendSummaryText;
    private readonly TextBlock   _languageNameText;
    private readonly Slider      _speechRateSlider;
    private readonly TextBox     _speechRateText;
    private readonly TextBox     _previewText;
    private readonly string      _previewShortText = "By the Light, keep your blade high and your courage higher.";
    private readonly string      _previewMediumText = "Champion, the road ahead winds through ruined watchtowers and restless woods. Stay to the lantern path, heed the warding stones, and return before moonrise if you value your hide.";
    private readonly string      _previewLongText = @"Ehh? You there—yes, you. Come closer. I have a task, and before you ask, no, I cannot do it myself, because the last time I walked that road my knees argued with me for three full days. Listen carefully, because this matters.

Take this satchel to Bramblewatch Hollow, speak to Warden Elira, and make certain she receives it before sunset. On the way, keep to the old stone path, avoid the shallow marsh to the east, and do not, under any circumstances, answer if something in the fog calls your name. It may sound like a friend. It will not be a friend.

Now then, there are three things you must remember. First, the bridge near the hollow looks sturdy, but the center planks are rotten. Second, the crows nesting in the watchtower startle easily, and when they scatter, the bandits nearby take it as a warning. Third—and this is the part people forget—if you find a silver lantern hanging from a branch, leave it where it is and walk the other way.

Years ago, before the road fell quiet, caravans used to pass through Bramblewatch every week. Traders, pilgrims, mercenaries, storytellers—always too loud, always in a hurry, always certain the woods were only woods. Then the disappearances began, and the village learned to keep its fires low and its doors barred after dusk. Some say the land remembers old grief. Some say it is only smugglers and superstition. Me? I say a wise traveler respects both.

So go quickly, keep your wits about you, and return by the main road if you value your skin. And if Warden Elira offers you tea, be polite and decline. Her tea is strong enough to wake the dead, and we have enough restless things wandering about already.";
    private readonly TextBlock   _summaryText;
    private readonly Slider?     _cfgWeightSlider;
    private readonly TextBox?    _cfgWeightText;
    private readonly Slider?     _exaggerationSlider;
    private readonly TextBox?    _exaggerationText;
    private readonly TextBlock?  _exaggerationWarningText;
    private readonly TextBox?    _seedText;
    private readonly Slider?     _cbTemperatureSlider;
    private readonly TextBox?    _cbTemperatureText;
    private readonly Slider?     _cbTopPSlider;
    private readonly TextBox?    _cbTopPText;
    private readonly Slider?     _cbRepetitionPenaltySlider;
    private readonly TextBox?    _cbRepetitionPenaltyText;

    // F5-TTS specific controls
    private readonly Slider?     _cfgStrengthSlider;
    private readonly TextBox?    _cfgStrengthText;
    private readonly Slider?     _nfeStepSlider;
    private readonly TextBox?    _nfeStepText;
    private readonly Slider?     _swaySlider;
    private readonly TextBox?    _swayText;
    private readonly Slider?     _longcatStepsSlider;
    private readonly TextBox?    _longcatStepsText;
    private readonly Slider?     _longcatCfgStrengthSlider;
    private readonly TextBox?    _longcatCfgStrengthText;
    private readonly ComboBox?   _longcatGuidanceCombo;
    private readonly ComboBox?   _stylePaceCombo;
    private readonly ComboBox?   _styleToneCombo;
    private readonly ComboBox?   _styleVolumeCombo;
    private readonly ComboBox?   _styleEmotionCombo;
    private readonly TextBox?    _styleInstructionText;
    private readonly TextBlock?  _styleHelpText;
    private bool                 _suppressStyleSync;
    private string               _lastBuiltStyleInstruction = string.Empty;
    private readonly Button      _previewButton;
    private readonly Button      _stopPreviewButton;
    private readonly TextBlock   _previewStatusText;
    private CancellationTokenSource? _previewCts;

    // ─────────────────────────────────────────────────────────────────────────
    // DSP chain section
    // ───────────────────────────────────────────────────────────────────────────

    private readonly StackPanel _dspChainPanel;
    private readonly TextBlock  _dspSummaryLine;


    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public VoiceProfileEditorDialog(
        VoiceSlot slot,
        string npcLabel,
        string accentLabel,
        VoiceProfile initialProfile,
        IReadOnlyList<VoiceInfo> availableVoices,
        bool supportsPresets,
        bool supportsBlend,
        bool supportsSynthesisSeed,
        string voiceSourceLabel,
        IReadOnlyDictionary<string, RemoteControlDescriptor>? controls = null,
        string? sampleProfileKey = null,
        string? sampleProviderId = null,
        bool isSampleDefaultsEditor = false)
    {
        _slot             = slot;
        _workingProfile   = initialProfile.Clone();
        _sampleProfileKey = sampleProfileKey;
        _sampleProviderId = sampleProviderId;
        _workingProfile.Dsp ??= new DspProfile();

        Title  = string.IsNullOrWhiteSpace(_sampleProfileKey)
            ? $"Edit NPC Voice Profile — {npcLabel}"
            : $"Edit Voice Default — {_sampleProfileKey}";
        Width  = 860;
        Height = 720;
        MinWidth  = 780;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        // Deep clone of the incoming DSP state used as the restore-shadow for
        // per-group enable/disable. Must be a clone, NOT a reference — otherwise

        // ── Presets ───────────────────────────────────────────────────────────

        _voiceSourceLabel = string.IsNullOrWhiteSpace(voiceSourceLabel) ? "voice" : voiceSourceLabel;
        _presetCombo = new ComboBox { IsVisible = false };
        _presetDescriptionText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _standardStateText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _standardProfile = ResolveStandardProfile(sampleProviderId ?? AppServices.Provider.ProviderId, slot, sampleProfileKey, availableVoices);
        _presetDescriptionText.Text = BuildStandardSummary();
        var restoreStandardBtn = Btn("Restore Standard", 140); restoreStandardBtn.Click += RestoreStandardButton_Click;

        // ── Voice mode ────────────────────────────────────────────────────────

        _singleVoiceRadio = new RadioButton { Content = "Single Voice", IsChecked = !_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode" };
        _blendVoiceRadio  = new RadioButton { Content = "Blend Voices", IsChecked  = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode", IsEnabled = supportsBlend };
        _singleVoiceRadio.IsCheckedChanged += VoiceModeChanged;
        _blendVoiceRadio.IsCheckedChanged  += VoiceModeChanged;

        // Build base samples list — strip known variant suffixes to get distinct bases.
        // Variant suffixes: slow, fast, quiet, loud, breathy
        static string GetBase(string id)
        {
            foreach (var s in new[] { "-slow", "-fast", "-quiet", "-loud", "-breathy" })
                if (id.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                    return id[..^s.Length];
            return id;
        }
        static string? GetVariant(string id)
        {
            foreach (var s in new[] { "slow", "fast", "quiet", "loud", "breathy" })
                if (id.EndsWith($"-{s}", StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        var providerForSorting = sampleProviderId ?? AppServices.Provider.ProviderId;
        var allVoiceChoices = SelectionRecencyHelper.SortByVoiceRecency(
                availableVoices,
                AppServices.Settings,
                providerForSorting,
                v => v.VoiceId,
                v => string.IsNullOrWhiteSpace(v.VoiceId) ? v.Name : v.VoiceId)
            .Select(v =>
            {
                var primary = string.IsNullOrWhiteSpace(v.VoiceId) ? v.Name : v.VoiceId;
                var summary = string.IsNullOrWhiteSpace(v.Description) ? primary : v.Description;
                return new VoiceChoice { VoiceId = v.VoiceId, Display = primary, Summary = summary };
            })
            .ToList();

        var baseChoices = allVoiceChoices
            .Where(v => GetVariant(v.VoiceId) == null)
            .ToArray();

        _voiceBaseCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth            = 180,
            ItemsSource         = baseChoices,
        };
        _voiceVariantCombo = new ComboBox
        {
            MinWidth    = 120,
            IsVisible   = false,
        };

        _voiceBaseCombo.SelectionChanged += (_, _) =>
        {
            var baseId = (_voiceBaseCombo.SelectedItem as VoiceChoice)?.VoiceId;
            if (baseId == null) return;

            // Repopulate variant combo
            var variants = allVoiceChoices
                .Where(v => GetBase(v.VoiceId) == baseId && GetVariant(v.VoiceId) != null)
                .ToArray();

            _voiceVariantCombo.SelectionChanged -= VoiceVariantCombo_SelectionChanged;
            _voiceVariantCombo.ItemsSource = variants.Length > 0
                ? new[] { new VoiceChoice { VoiceId = baseId, Display = "(default)", Summary = "" } }
                    .Concat(variants).ToArray()
                : null;
            _voiceVariantCombo.IsVisible = variants.Length > 0;
            _voiceVariantCombo.SelectedIndex = 0;
            _voiceVariantCombo.SelectionChanged += VoiceVariantCombo_SelectionChanged;

            // Profile gets base ID unless a variant is selected
            if (_singleVoiceRadio.IsChecked == true)
            {
                _workingProfile.VoiceId = baseId;
                RefreshSingleVoiceSummary();
                RefreshSummary();
            }
        };
        _voiceVariantCombo.SelectionChanged += VoiceVariantCombo_SelectionChanged;

        _voiceSummaryText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _blendSummaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

        var editBlendBtn = Btn("Edit Blend…", 110); editBlendBtn.Click += EditBlendButton_Click; editBlendBtn.IsEnabled = supportsBlend;

        // ── Language ──────────────────────────────────────────────────────────

        var currentLang = EspeakLanguageCatalog.All.FirstOrDefault(x => string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase));
        _languageNameText = new TextBlock { Text = currentLang?.DisplayName ?? _workingProfile.LangCode, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var chooseLangBtn = Btn("Choose…", 100); chooseLangBtn.Click += ChooseLanguageButton_Click;

        // ── Speech rate ───────────────────────────────────────────────────────

        _speechRateSlider = new Slider { Minimum = 0.25, Maximum = 4.00, Value = _workingProfile.SpeechRate, TickFrequency = 0.01, HorizontalAlignment = HorizontalAlignment.Stretch };
        _speechRateText   = new TextBox { Width = 80, Text = FormatPercent(_workingProfile.SpeechRate), HorizontalContentAlignment = HorizontalAlignment.Center };
        _speechRateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            var snapped = NormalizeSpeechRateForUi((float)_speechRateSlider.Value);
            if (Math.Abs(_speechRateSlider.Value - snapped) > 0.0005)
            {
                _speechRateSlider.Value = snapped;
                return;
            }
            _workingProfile.SpeechRate = snapped;
            var t = FormatPercent(_workingProfile.SpeechRate);
            if (_speechRateText.Text != t) _speechRateText.Text = t;
            RefreshSummary();
        };
        _speechRateText.TextChanged += (_, _) =>
        {
            if (!TryParsePercentOrNumber(_speechRateText.Text, out var v)) return;
            v = NormalizeSpeechRateForUi(Math.Clamp(v, 0.25f, 4.00f));
            _workingProfile.SpeechRate = v;
            if (Math.Abs(_speechRateSlider.Value - v) > 0.0005) _speechRateSlider.Value = v;
            RefreshSummary();
        };
        _speechRateText.LostFocus += (_, _) =>
        {
            var normalized = NormalizeSpeechRateForUi(_workingProfile.SpeechRate);
            _workingProfile.SpeechRate = normalized;
            var t = FormatPercent(normalized);
            if (_speechRateText.Text != t) _speechRateText.Text = t;
            if (Math.Abs(_speechRateSlider.Value - normalized) > 0.0005) _speechRateSlider.Value = normalized;
        };


        // ── Provider-specific render controls ───────────────────────────────

        controls ??= new Dictionary<string, RemoteControlDescriptor>(StringComparer.OrdinalIgnoreCase);
        controls.TryGetValue("cfg_weight", out var cfgControl);
        controls.TryGetValue("exaggeration", out var exagControl);
        controls.TryGetValue("cb_temperature", out var cbTemperatureControl);
        controls.TryGetValue("cb_top_p", out var cbTopPControl);
        controls.TryGetValue("cb_repetition_penalty", out var cbRepetitionPenaltyControl);
        controls.TryGetValue("longcat_steps", out var longcatStepsControl);
        controls.TryGetValue("longcat_cfg_strength", out var longcatCfgStrengthControl);
        controls.TryGetValue("longcat_guidance", out var longcatGuidanceControl);

        Border? chatterboxControlsCard = null;
        _cfgWeightSlider    = null;
        _cfgWeightText      = null;
        _exaggerationSlider = null;
        _exaggerationText   = null;
        _exaggerationWarningText = null;
        _seedText = null;
        _cbTemperatureSlider = null;
        _cbTemperatureText = null;
        _cbTopPSlider = null;
        _cbTopPText = null;
        _cbRepetitionPenaltySlider = null;
        _cbRepetitionPenaltyText = null;

        Border? f5ControlsCard = null;
        _cfgStrengthSlider = null;
        _cfgStrengthText   = null;
        _nfeStepSlider     = null;
        _nfeStepText       = null;
        _swaySlider        = null;
        _swayText          = null;
        _longcatStepsSlider = null;
        _longcatStepsText = null;
        _longcatCfgStrengthSlider = null;
        _longcatCfgStrengthText = null;
        _longcatGuidanceCombo = null;

        if (cfgControl != null || exagControl != null || cbTemperatureControl != null || cbTopPControl != null || cbRepetitionPenaltyControl != null || supportsSynthesisSeed)
        {
            float cfgMin = cfgControl?.Min ?? 0f;
            float cfgMax = cfgControl?.Max ?? 3f;
            float cfgDefault = ParseFloatDefault(cfgControl?.Default, 0f);
            float exMin = exagControl?.Min ?? 0f;
            float exMax = exagControl?.Max ?? 3f;
            float exDefault = ParseFloatDefault(exagControl?.Default, 0f);

            if (!_workingProfile.CfgWeight.HasValue && cfgControl != null)
                _workingProfile.CfgWeight = cfgDefault;
            if (!_workingProfile.Exaggeration.HasValue && exagControl != null)
                _workingProfile.Exaggeration = exDefault;

            var controlRows = new StackPanel { Spacing = 8 };

            Grid BuildFloatRow(string label, float min, float max, float value, Action<float> setter, out Slider slider, out TextBox textBox, float tick = 0.1f, string format = "0.00", Action? onValueChanged = null)
            {
                var sliderLocal = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick, HorizontalAlignment = HorizontalAlignment.Stretch };
                var textBoxLocal = new TextBox { Width = 80, Text = value.ToString(format, Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                sliderLocal.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Slider.ValueProperty) return;
                    var v = (float)sliderLocal.Value;
                    setter(v);
                    var t = v.ToString(format, Inv);
                    if (textBoxLocal.Text != t) textBoxLocal.Text = t;
                    onValueChanged?.Invoke();
                    RefreshSummary();
                };
                textBoxLocal.TextChanged += (_, _) =>
                {
                    if (!float.TryParse(textBoxLocal.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, min, max);
                    setter(v);
                    if (Math.Abs(sliderLocal.Value - v) > 0.001) sliderLocal.Value = v;
                    onValueChanged?.Invoke();
                    RefreshSummary();
                };
                textBoxLocal.LostFocus += (_, _) =>
                {
                    if (!float.TryParse(textBoxLocal.Text, System.Globalization.NumberStyles.Float, Inv, out var v))
                        v = (float)sliderLocal.Value;
                    v = Math.Clamp(v, min, max);
                    var t = v.ToString(format, Inv);
                    if (textBoxLocal.Text != t) textBoxLocal.Text = t;
                };
                slider = sliderLocal;
                textBox = textBoxLocal;
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(sliderLocal, 1); row.Children.Add(sliderLocal);
                Grid.SetColumn(textBoxLocal, 2); row.Children.Add(textBoxLocal);
                return row;
            }

            if (supportsSynthesisSeed)
            {
                _seedText = new TextBox
                {
                    Width = 120,
                    Text = _workingProfile.SynthesisSeed?.ToString(Inv) ?? string.Empty,
                    Watermark = "Blank = random"
                };
                _seedText.TextChanged += (_, _) =>
                {
                    var raw = (_seedText.Text ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        _workingProfile.SynthesisSeed = null;
                        RefreshSummary();
                        return;
                    }
                    if (int.TryParse(raw, out var parsed) && parsed >= 0)
                    {
                        _workingProfile.SynthesisSeed = parsed;
                        RefreshSummary();
                    }
                };
                var seedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), ColumnSpacing = 8 };
                seedRow.Children.Add(new TextBlock { Text = "Seed", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_seedText, 1);
                seedRow.Children.Add(_seedText);
                controlRows.Children.Add(seedRow);
                controlRows.Children.Add(new TextBlock
                {
                    Text = "Optional reproducibility seed. Leave blank for non-deterministic output. Different seeds must use different cache entries.",
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(0, 0, 0, 4)
                });
            }

            if (cfgControl != null)
            {
                _cfgWeightSlider = null; _cfgWeightText = null;
                var row = BuildFloatRow("CFG Weight", cfgMin, cfgMax, _workingProfile.CfgWeight ?? cfgDefault, v => _workingProfile.CfgWeight = v, out var cfgSlider, out var cfgText);
                _cfgWeightSlider = cfgSlider;
                _cfgWeightText = cfgText;
                controlRows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(cfgControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = cfgControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (exagControl != null)
            {
                void UpdateExaggerationWarning()
                {
                    if (_exaggerationWarningText == null) return;
                    var ex = _workingProfile.Exaggeration ?? exDefault;
                    _exaggerationWarningText.IsVisible = ex > 1.5f;
                }

                _exaggerationSlider = null; _exaggerationText = null;
                var row = BuildFloatRow("Exaggeration", exMin, exMax, _workingProfile.Exaggeration ?? exDefault, v => _workingProfile.Exaggeration = v, out var exSlider, out var exText, onValueChanged: UpdateExaggerationWarning);
                _exaggerationSlider = exSlider;
                _exaggerationText = exText;
                controlRows.Children.Add(row);
                _exaggerationWarningText = new TextBlock
                {
                    Text = "Warning: values above 1.5 can reduce Chatterbox reliability and may cause repetitions, garbled endings, or sequence drift.",
                    Foreground = Brushes.Gold,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = (_workingProfile.Exaggeration ?? exDefault) > 1.5f,
                    Margin = new Avalonia.Thickness(0, 0, 0, 4)
                };
                controlRows.Children.Add(_exaggerationWarningText);
                if (!string.IsNullOrWhiteSpace(exagControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = exagControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (cbTemperatureControl != null)
            {
                var min = cbTemperatureControl.Min ?? 0.1f;
                var max = cbTemperatureControl.Max ?? 2.0f;
                var def = ParseFloatDefault(cbTemperatureControl.Default, 0.8f);
                _cbTemperatureSlider = null; _cbTemperatureText = null;
                var row = BuildFloatRow("Temperature", min, max, _workingProfile.ChatterboxTemperature ?? def, v => _workingProfile.ChatterboxTemperature = v, out var tempSlider, out var tempText);
                _cbTemperatureSlider = tempSlider;
                _cbTemperatureText = tempText;
                controlRows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(cbTemperatureControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = cbTemperatureControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (cbTopPControl != null)
            {
                var min = cbTopPControl.Min ?? 0.01f;
                var max = cbTopPControl.Max ?? 1.0f;
                var def = ParseFloatDefault(cbTopPControl.Default, 1.0f);
                _cbTopPSlider = null; _cbTopPText = null;
                var row = BuildFloatRow("Top P", min, max, _workingProfile.ChatterboxTopP ?? def, v => _workingProfile.ChatterboxTopP = v, out var topPSlider, out var topPText);
                _cbTopPSlider = topPSlider;
                _cbTopPText = topPText;
                controlRows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(cbTopPControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = cbTopPControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (cbRepetitionPenaltyControl != null)
            {
                var min = cbRepetitionPenaltyControl.Min ?? 1.0f;
                var max = cbRepetitionPenaltyControl.Max ?? 3.0f;
                var def = ParseFloatDefault(cbRepetitionPenaltyControl.Default, 1.2f);
                _cbRepetitionPenaltySlider = null; _cbRepetitionPenaltyText = null;
                var row = BuildFloatRow("Repeat Penalty", min, max, _workingProfile.ChatterboxRepetitionPenalty ?? def, v => _workingProfile.ChatterboxRepetitionPenalty = v, out var repSlider, out var repText);
                _cbRepetitionPenaltySlider = repSlider;
                _cbRepetitionPenaltyText = repText;
                controlRows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(cbRepetitionPenaltyControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = cbRepetitionPenaltyControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            chatterboxControlsCard = Card("Voice Render Controls", controlRows);
        }

        // ── F5-TTS synthesis controls ─────────────────────────────────────────

        controls.TryGetValue("cfg_strength",       out var cfgStrControl);
        controls.TryGetValue("nfe_step",           out var nfeControl);
        controls.TryGetValue("sway_sampling_coef", out var swayControl);

        if (cfgStrControl != null || nfeControl != null || swayControl != null)
        {
            var f5Rows = new StackPanel { Spacing = 8 };

            if (cfgStrControl != null)
            {
                float min = cfgStrControl.Min ?? 0.5f;
                float max = cfgStrControl.Max ?? 5.0f;
                float def = ParseFloatDefault(cfgStrControl.Default, 2.0f);
                if (!_workingProfile.CfgStrength.HasValue) _workingProfile.CfgStrength = def;
                float cur = _workingProfile.CfgStrength ?? def;

                _cfgStrengthSlider = new Slider { Minimum = min, Maximum = max, Value = cur, TickFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _cfgStrengthText   = new TextBox { Width = 80, Text = cur.ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                _cfgStrengthSlider.PropertyChanged += (_, e) => {
                    if (e.Property != Slider.ValueProperty) return;
                    _workingProfile.CfgStrength = (float)_cfgStrengthSlider.Value;
                    var t = _workingProfile.CfgStrength.Value.ToString("0.00", Inv);
                    if (_cfgStrengthText.Text != t) _cfgStrengthText.Text = t;
                    RefreshSummary();
                };
                _cfgStrengthText.TextChanged += (_, _) => {
                    if (!float.TryParse(_cfgStrengthText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, min, max);
                    _workingProfile.CfgStrength = v;
                    if (Math.Abs(_cfgStrengthSlider.Value - v) > 0.001) _cfgStrengthSlider.Value = v;
                    RefreshSummary();
                };
                _cfgStrengthText.LostFocus += (_, _) => {
                    var v = _workingProfile.CfgStrength ?? (float)_cfgStrengthSlider.Value;
                    var t = v.ToString("0.00", Inv);
                    if (_cfgStrengthText.Text != t) _cfgStrengthText.Text = t;
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "Cfg Strength", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_cfgStrengthSlider, 1); row.Children.Add(_cfgStrengthSlider);
                Grid.SetColumn(_cfgStrengthText, 2);   row.Children.Add(_cfgStrengthText);
                var btn = Btn("Default", 70); btn.Click += (_, _) => { _workingProfile.CfgStrength = def; _cfgStrengthSlider.Value = def; _cfgStrengthText.Text = def.ToString("0.00", Inv); RefreshSummary(); };
                Grid.SetColumn(btn, 3); row.Children.Add(btn);
                f5Rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(cfgStrControl.Description))
                    f5Rows.Children.Add(new TextBlock { Text = cfgStrControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (nfeControl != null)
            {
                float min = nfeControl.Min ?? 8f;
                float max = nfeControl.Max ?? 64f;
                float def = ParseFloatDefault(nfeControl.Default, 48f);
                if (!_workingProfile.NfeStep.HasValue) _workingProfile.NfeStep = (int)def;
                float cur = _workingProfile.NfeStep ?? (int)def;

                _nfeStepSlider = new Slider { Minimum = min, Maximum = max, Value = cur, TickFrequency = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
                _nfeStepText   = new TextBox { Width = 80, Text = ((int)cur).ToString(), HorizontalContentAlignment = HorizontalAlignment.Center };
                _nfeStepSlider.PropertyChanged += (_, e) => {
                    if (e.Property != Slider.ValueProperty) return;
                    _workingProfile.NfeStep = (int)Math.Round(_nfeStepSlider.Value / 8) * 8;
                    _workingProfile.NfeStep = Math.Clamp(_workingProfile.NfeStep.Value, 8, 64);
                    var t = _workingProfile.NfeStep.Value.ToString();
                    if (_nfeStepText.Text != t) _nfeStepText.Text = t;
                    RefreshSummary();
                };
                _nfeStepText.TextChanged += (_, _) => {
                    if (!int.TryParse(_nfeStepText.Text, out var v)) return;
                    v = Math.Clamp(v, 8, 64);
                    _workingProfile.NfeStep = v;
                    if (Math.Abs(_nfeStepSlider.Value - v) > 0.5) _nfeStepSlider.Value = v;
                    RefreshSummary();
                };
                _nfeStepText.LostFocus += (_, _) => {
                    var v = _workingProfile.NfeStep ?? (int)Math.Round(_nfeStepSlider.Value / 8) * 8;
                    var t = v.ToString();
                    if (_nfeStepText.Text != t) _nfeStepText.Text = t;
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "NFE Steps", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_nfeStepSlider, 1); row.Children.Add(_nfeStepSlider);
                Grid.SetColumn(_nfeStepText, 2);   row.Children.Add(_nfeStepText);
                var btn = Btn("Default", 70); btn.Click += (_, _) => { _workingProfile.NfeStep = (int)def; _nfeStepSlider.Value = def; _nfeStepText.Text = ((int)def).ToString(); RefreshSummary(); };
                Grid.SetColumn(btn, 3); row.Children.Add(btn);
                f5Rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(nfeControl.Description))
                    f5Rows.Children.Add(new TextBlock { Text = nfeControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (swayControl != null)
            {
                float min = swayControl.Min ?? -1.0f;
                float max = swayControl.Max ?? 1.0f;
                float def = ParseFloatDefault(swayControl.Default, -1.0f);
                if (!_workingProfile.SwaysamplingCoef.HasValue) _workingProfile.SwaysamplingCoef = def;
                float cur = _workingProfile.SwaysamplingCoef ?? def;

                _swaySlider = new Slider { Minimum = min, Maximum = max, Value = cur, TickFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _swayText   = new TextBox { Width = 80, Text = cur.ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                _swaySlider.PropertyChanged += (_, e) => {
                    if (e.Property != Slider.ValueProperty) return;
                    _workingProfile.SwaysamplingCoef = (float)_swaySlider.Value;
                    var t = _workingProfile.SwaysamplingCoef.Value.ToString("0.00", Inv);
                    if (_swayText.Text != t) _swayText.Text = t;
                    RefreshSummary();
                };
                _swayText.TextChanged += (_, _) => {
                    if (!float.TryParse(_swayText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, min, max);
                    _workingProfile.SwaysamplingCoef = v;
                    if (Math.Abs(_swaySlider.Value - v) > 0.001) _swaySlider.Value = v;
                    RefreshSummary();
                };
                _swayText.LostFocus += (_, _) => {
                    var v = _workingProfile.SwaysamplingCoef ?? (float)_swaySlider.Value;
                    var t = v.ToString("0.00", Inv);
                    if (_swayText.Text != t) _swayText.Text = t;
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "Sway Coef", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_swaySlider, 1); row.Children.Add(_swaySlider);
                Grid.SetColumn(_swayText, 2);   row.Children.Add(_swayText);
                var btn = Btn("Default", 70); btn.Click += (_, _) => { _workingProfile.SwaysamplingCoef = def; _swaySlider.Value = def; _swayText.Text = def.ToString("0.00", Inv); RefreshSummary(); };
                Grid.SetColumn(btn, 3); row.Children.Add(btn);
                f5Rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(swayControl.Description))
                    f5Rows.Children.Add(new TextBlock { Text = swayControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            f5ControlsCard = Card("F5-TTS Synthesis Controls", f5Rows);
        }

        Border? longcatControlsCard = null;
        if (longcatStepsControl != null || longcatCfgStrengthControl != null || longcatGuidanceControl != null)
        {
            var rows = new StackPanel { Spacing = 8 };

            if (longcatStepsControl != null)
            {
                int min = (int)Math.Round(longcatStepsControl.Min ?? 4f);
                int max = (int)Math.Round(longcatStepsControl.Max ?? 64f);
                int def = ParseIntDefault(longcatStepsControl.Default, 16);
                if (!_workingProfile.LongcatSteps.HasValue) _workingProfile.LongcatSteps = def;

                _longcatStepsSlider = new Slider { Minimum = min, Maximum = max, Value = _workingProfile.LongcatSteps.Value, TickFrequency = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _longcatStepsText = new TextBox { Width = 80, Text = _workingProfile.LongcatSteps.Value.ToString(), HorizontalContentAlignment = HorizontalAlignment.Center };
                _longcatStepsSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Slider.ValueProperty) return;
                    var v = (int)Math.Round(_longcatStepsSlider.Value);
                    _workingProfile.LongcatSteps = v;
                    var t = v.ToString();
                    if (_longcatStepsText.Text != t) _longcatStepsText.Text = t;
                    RefreshSummary();
                };
                _longcatStepsText.TextChanged += (_, _) =>
                {
                    if (!int.TryParse(_longcatStepsText.Text, out var v)) return;
                    v = Math.Clamp(v, min, max);
                    _workingProfile.LongcatSteps = v;
                    if (Math.Abs(_longcatStepsSlider.Value - v) > 0.001) _longcatStepsSlider.Value = v;
                    RefreshSummary();
                };
                _longcatStepsText.LostFocus += (_, _) =>
                {
                    var v = _workingProfile.LongcatSteps ?? (int)Math.Round(_longcatStepsSlider.Value);
                    var t = v.ToString();
                    if (_longcatStepsText.Text != t) _longcatStepsText.Text = t;
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "LongCat Steps", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_longcatStepsSlider, 1); row.Children.Add(_longcatStepsSlider);
                Grid.SetColumn(_longcatStepsText, 2); row.Children.Add(_longcatStepsText);
                rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(longcatStepsControl.Description))
                    rows.Children.Add(new TextBlock { Text = longcatStepsControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (longcatCfgStrengthControl != null)
            {
                float min = longcatCfgStrengthControl.Min ?? 1.0f;
                float max = longcatCfgStrengthControl.Max ?? 10.0f;
                float def = ParseFloatDefault(longcatCfgStrengthControl.Default, 4.0f);
                if (!_workingProfile.LongcatCfgStrength.HasValue) _workingProfile.LongcatCfgStrength = def;

                _longcatCfgStrengthSlider = new Slider { Minimum = min, Maximum = max, Value = _workingProfile.LongcatCfgStrength.Value, TickFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _longcatCfgStrengthText = new TextBox { Width = 80, Text = _workingProfile.LongcatCfgStrength.Value.ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                _longcatCfgStrengthSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Slider.ValueProperty) return;
                    var v = (float)_longcatCfgStrengthSlider.Value;
                    _workingProfile.LongcatCfgStrength = v;
                    var t = v.ToString("0.00", Inv);
                    if (_longcatCfgStrengthText.Text != t) _longcatCfgStrengthText.Text = t;
                    RefreshSummary();
                };
                _longcatCfgStrengthText.TextChanged += (_, _) =>
                {
                    if (!float.TryParse(_longcatCfgStrengthText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, min, max);
                    _workingProfile.LongcatCfgStrength = v;
                    if (Math.Abs(_longcatCfgStrengthSlider.Value - v) > 0.001) _longcatCfgStrengthSlider.Value = v;
                    RefreshSummary();
                };
                _longcatCfgStrengthText.LostFocus += (_, _) =>
                {
                    var v = _workingProfile.LongcatCfgStrength ?? (float)_longcatCfgStrengthSlider.Value;
                    var t = v.ToString("0.00", Inv);
                    if (_longcatCfgStrengthText.Text != t) _longcatCfgStrengthText.Text = t;
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,70"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "Guidance Strength", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_longcatCfgStrengthSlider, 1); row.Children.Add(_longcatCfgStrengthSlider);
                Grid.SetColumn(_longcatCfgStrengthText, 2); row.Children.Add(_longcatCfgStrengthText);
                rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(longcatCfgStrengthControl.Description))
                    rows.Children.Add(new TextBlock { Text = longcatCfgStrengthControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (longcatGuidanceControl != null)
            {
                var opts = (longcatGuidanceControl.Options?.Count ?? 0) > 0
                    ? longcatGuidanceControl.Options?.ToList() ?? new List<string> { "apg", "cfg" }
                    : new List<string> { "apg", "cfg" };
                var selected = _workingProfile.LongcatGuidance;
                if (string.IsNullOrWhiteSpace(selected))
                    selected = !string.IsNullOrWhiteSpace(longcatGuidanceControl.Default) ? longcatGuidanceControl.Default : opts.FirstOrDefault() ?? "apg";
                _workingProfile.LongcatGuidance = selected;
                _longcatGuidanceCombo = new ComboBox { ItemsSource = opts, SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
                _longcatGuidanceCombo.SelectionChanged += (_, _) =>
                {
                    _workingProfile.LongcatGuidance = _longcatGuidanceCombo.SelectedItem as string;
                    RefreshSummary();
                };
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = "Guidance Mode", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_longcatGuidanceCombo, 1); row.Children.Add(_longcatGuidanceCombo);
                rows.Children.Add(row);
                if (!string.IsNullOrWhiteSpace(longcatGuidanceControl.Description))
                    rows.Children.Add(new TextBlock { Text = longcatGuidanceControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            longcatControlsCard = Card("LongCat Controls", rows);
        }

        controls.TryGetValue("cosy_instruct", out var styleInstructionControl);
        Border? styleInstructionCard = null;
        _stylePaceCombo = null;
        _styleToneCombo = null;
        _styleVolumeCombo = null;
        _styleEmotionCombo = null;
        _styleInstructionText = null;
        _styleHelpText = null;

        if (styleInstructionControl != null)
        {
            _workingProfile.CosyInstruct ??= string.Empty;

            static ComboBox StyleCombo(IEnumerable<string> items) => new()
            {
                ItemsSource = items.ToArray(),
                SelectedIndex = 0,
                MinWidth = 180,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _stylePaceCombo = StyleCombo(new[] { "Normal", "Slow and deliberate", "Fast and urgent", "Custom" });
            _styleToneCombo = StyleCombo(new[] { "Neutral", "Gravitas", "Weary", "Reverent", "Commanding", "Mysterious", "Excited", "Fearful", "Custom" });
            _styleVolumeCombo = StyleCombo(new[] { "Normal", "Quiet and soft", "Loud and booming", "Custom" });
            _styleEmotionCombo = StyleCombo(new[] { "Neutral", "Angry", "Sad", "Joyful", "Tense", "Custom" });
            _styleInstructionText = new TextBox
            {
                Text = _workingProfile.CosyInstruct ?? string.Empty,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                Watermark = "Describe how this voice should speak...",
                MinWidth = 320
            };
            _styleHelpText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(styleInstructionControl.Description)
                    ? "Optional natural-language speaking style. Leave blank to use normal zero-shot voice cloning."
                    : styleInstructionControl.Description,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap
            };

            void RebuildInstructionFromPresets()
            {
                if (_suppressStyleSync || _styleInstructionText == null || _stylePaceCombo == null || _styleToneCombo == null || _styleVolumeCombo == null || _styleEmotionCombo == null)
                    return;

                string pace = _stylePaceCombo.SelectedItem as string ?? "Normal";
                string tone = _styleToneCombo.SelectedItem as string ?? "Neutral";
                string volume = _styleVolumeCombo.SelectedItem as string ?? "Normal";
                string emotion = _styleEmotionCombo.SelectedItem as string ?? "Neutral";

                string built = BuildStyleInstruction(pace, tone, volume, emotion);
                _lastBuiltStyleInstruction = built;
                _workingProfile.CosyInstruct = built;
                _styleInstructionText.Text = built;
                RefreshSummary();
            }

            void MarkCustomFromManualText()
            {
                if (_suppressStyleSync || _styleInstructionText == null || _stylePaceCombo == null || _styleToneCombo == null || _styleVolumeCombo == null || _styleEmotionCombo == null)
                    return;

                var text = (_styleInstructionText.Text ?? string.Empty).Trim();
                _workingProfile.CosyInstruct = text;
                if (string.Equals(text, _lastBuiltStyleInstruction, StringComparison.Ordinal))
                {
                    RefreshSummary();
                    return;
                }

                _suppressStyleSync = true;
                _stylePaceCombo.SelectedItem = "Custom";
                _styleToneCombo.SelectedItem = "Custom";
                _styleVolumeCombo.SelectedItem = "Custom";
                _styleEmotionCombo.SelectedItem = "Custom";
                _suppressStyleSync = false;
                RefreshSummary();
            }

            _stylePaceCombo.SelectionChanged += (_, _) => RebuildInstructionFromPresets();
            _styleToneCombo.SelectionChanged += (_, _) => RebuildInstructionFromPresets();
            _styleVolumeCombo.SelectionChanged += (_, _) => RebuildInstructionFromPresets();
            _styleEmotionCombo.SelectionChanged += (_, _) => RebuildInstructionFromPresets();
            _styleInstructionText.TextChanged += (_, _) => MarkCustomFromManualText();

            var resetStyleBtn = Btn("Reset to Presets", 130);
            resetStyleBtn.Click += (_, _) =>
            {
                if (_styleInstructionText == null || _stylePaceCombo == null || _styleToneCombo == null || _styleVolumeCombo == null || _styleEmotionCombo == null)
                    return;
                _suppressStyleSync = true;
                _stylePaceCombo.SelectedItem = "Normal";
                _styleToneCombo.SelectedItem = "Neutral";
                _styleVolumeCombo.SelectedItem = "Normal";
                _styleEmotionCombo.SelectedItem = "Neutral";
                _suppressStyleSync = false;
                _lastBuiltStyleInstruction = string.Empty;
                _styleInstructionText.Text = string.Empty;
                _workingProfile.CosyInstruct = string.Empty;
                RefreshSummary();
            };

            var clearStyleBtn = Btn("Clear", 80);
            clearStyleBtn.Click += (_, _) =>
            {
                if (_styleInstructionText == null) return;
                _styleInstructionText.Text = string.Empty;
            };

            var presetsGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
                ColumnSpacing = 8,
                RowSpacing = 8
            };
            AddLabeledRow(presetsGrid, 0, "Pace", _stylePaceCombo);
            AddLabeledRow(presetsGrid, 1, "Tone", _styleToneCombo);
            AddLabeledRow(presetsGrid, 2, "Volume", _styleVolumeCombo);
            AddLabeledRow(presetsGrid, 3, "Emotion", _styleEmotionCombo);

            styleInstructionCard = Card("Speech Style", new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    _styleHelpText,
                    presetsGrid,
                    new TextBlock { Text = "Style Instruction", FontWeight = FontWeight.SemiBold },
                    _styleInstructionText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { resetStyleBtn, clearStyleBtn } },
                    new TextBlock { Text = "Empty text disables style instruction and uses the provider's normal voice generation path.", Opacity = 0.8, TextWrapping = TextWrapping.Wrap }
                }
            });
        }

        // ── DSP chain ─────────────────────────────────────────────────────────

        _workingProfile.Dsp ??= new DspProfile();
        var dsp = _workingProfile.Dsp;

        // Master enabled toggle
        var masterEnabledCheck = new CheckBox
        {
            Content   = "Effects enabled",
            IsChecked = dsp.Enabled,
            Margin    = new Avalonia.Thickness(0, 0, 0, 4),
        };
        masterEnabledCheck.IsCheckedChanged += (_, _) =>
        {
            _workingProfile.Dsp!.Enabled = masterEnabledCheck.IsChecked == true;
            RefreshDspSummary();
            RefreshSummary();
        };

        // Chain panel — populated by RebuildChainPanel
        _dspChainPanel = new StackPanel { Spacing = 4 };
        RebuildChainPanel();

        // Buttons
        var addEffectBtn = Btn("+ Add Effect", 110);
        addEffectBtn.Click += async (_, _) =>
        {
            var picker = new DspEffectPickerDialog();
            await picker.ShowDialog(this);
            if (picker.ChosenEffect.HasValue)
            {
                _workingProfile.Dsp!.Effects.Add(DspEffectItem.CreateDefault(picker.ChosenEffect.Value));
                RebuildChainPanel();
                RefreshDspSummary();
                RefreshSummary();
            }
        };

        var clearAllBtn = Btn("Clear All", 80);
        clearAllBtn.Click += (_, _) =>
        {
            _workingProfile.Dsp!.Effects.Clear();
            RebuildChainPanel();
            RefreshDspSummary();
            RefreshSummary();
        };

        _dspSummaryLine = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize   = 11,
            Text       = BuildDspSummary(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var dspContent = new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing     = 8,
                    Children    = { masterEnabledCheck },
                },
                _dspChainPanel,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing     = 8,
                    Children    = { addEffectBtn, clearAllBtn, _dspSummaryLine },
                },
            }
        };

        // ── Preview / Summary ─────────────────────────────────────────────────

        _previewText = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 52, Text = _previewShortText };
        _previewButton = Btn("Preview", 90); _previewButton.Click += PreviewButton_Click;
        _stopPreviewButton = Btn("Stop", 80); _stopPreviewButton.Click += StopPreviewButton_Click; _stopPreviewButton.IsEnabled = false;
        _previewStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Avalonia.Media.Brushes.Gold, FontSize = 12, Text = string.Empty };
        var shortPreviewBtn = Btn("Short Preview", 110); shortPreviewBtn.Click += (_, _) => _previewText.Text = _previewShortText;
        var mediumPreviewBtn = Btn("Medium Preview", 120); mediumPreviewBtn.Click += (_, _) => _previewText.Text = _previewMediumText;
        var longPreviewBtn = Btn("Long Preview", 100); longPreviewBtn.Click += (_, _) => _previewText.Text = _previewLongText;
        var previewPresetButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { shortPreviewBtn, mediumPreviewBtn, longPreviewBtn } };
        var saveBtn    = Btn("Save & Close", 120); saveBtn.Click += (_, _) => {
            SelectionRecencyHelper.BumpVoice(AppServices.Settings, providerForSorting, _workingProfile.VoiceId);
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
            _saved = true; Close(_workingProfile.Clone()); };
        saveBtn.IsEnabled = true;
        var cancelBtn  = Btn("Cancel", 90);        cancelBtn.Click += (_, _) => Close(null);
        _summaryText   = new TextBlock { TextWrapping = TextWrapping.Wrap };

        // ── Section cards ─────────────────────────────────────────────────────

        _singleVoiceSection = Card(string.Empty, new StackPanel { Spacing = 8, Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children =
                {
                    new TextBlock { Text = "Single Voice", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 110 },
                    _voiceBaseCombo,
                    _voiceVariantCombo
                }
            },
            _voiceSummaryText
        }});
        _blendVoiceSection  = Card("Blend Voices", new StackPanel { Spacing = 8, Children = { _blendSummaryText, editBlendBtn } });

        var presetCard    = Card("Standard Setup", new StackPanel { Spacing = 8, Children = { _presetDescriptionText, _standardStateText, new TextBlock { Text = "Restore Standard resets cache-affecting voice settings for this entry. DSP is left as-is.", Opacity = 0.8, TextWrapping = TextWrapping.Wrap }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { restoreStandardBtn } } } });
        var voiceModeCard = Card(string.Empty, new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { new TextBlock { Text = "Voice Mode", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Width = 110 }, _singleVoiceRadio, _blendVoiceRadio } } } });
        var languageCard  = Card("Dialect / Language", new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _languageNameText, chooseLangBtn } } } });
        if (isSampleDefaultsEditor)
        {
            voiceModeCard.IsVisible = false;
            _singleVoiceSection.IsVisible = false;
            _blendVoiceSection.IsVisible = false;
        }

        var rateGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,80"), ColumnSpacing = 10 };
        rateGrid.Children.Add(_speechRateSlider);
        Grid.SetColumn(_speechRateText, 1);
        rateGrid.Children.Add(_speechRateText);
        var rateCard = Card("Voice Speech Rate", rateGrid);

        Grid topGrid;
        if (isSampleDefaultsEditor)
        {
            topGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto"),
                ColumnSpacing = 10,
                RowSpacing = 10
            };
            AddToGrid(topGrid, languageCard, 0, 0);
            AddToGrid(topGrid, rateCard,     0, 1);
        }
        else
        {
            // Keep only the controls used constantly in the fixed area.
            // Standard setup, restore, warnings, DSP, and advanced render knobs
            // live in the lower scroll region so the common voice->preview loop
            // does not require scrolling a full-page form on a 1080p display.
            topGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
                ColumnSpacing = 10,
                RowSpacing = 10
            };
            AddToGrid(topGrid, _blendVoiceSection,  0, 0);
            AddToGrid(topGrid, voiceModeCard,       0, 1);
            AddToGrid(topGrid, languageCard,        1, 0);
            AddToGrid(topGrid, rateCard,            1, 1);
            AddToGrid(topGrid, _singleVoiceSection, 2, 0);
        }

        var headerStack = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Applies to: {npcLabel}", FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = $"Accent profile: {accentLabel}", Opacity = 0.8 },
                new TextBlock { Text = "This changes how RuneReader reads detected text for this NPC type. It does not change WoW's built-in audio or settings.", TextWrapping = TextWrapping.Wrap },
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#33AA0000")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#CCFF3333")),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(4),
                    Padding = new Avalonia.Thickness(8),
                    Child = new TextBlock
                    {
                        Text = "Warning: changing voice, language, speed, blend, seed, provider controls, or other synthesis-affecting settings changes cache identity and may force regeneration instead of using cached audio.",
                        Foreground = new SolidColorBrush(Color.Parse("#FFFF6666")),
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeight.SemiBold
                    }
                }
            }
        };

        var previewStatusRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var previewStatusSpacer = new Border();
        previewStatusRow.Children.Add(previewStatusSpacer);
        _previewStatusText.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_previewStatusText, 1);
        previewStatusRow.Children.Add(_previewStatusText);

        var previewActionsRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        previewActionsRow.Children.Add(previewPresetButtons);
        var previewButtonsRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _previewButton, _stopPreviewButton } };
        Grid.SetColumn(previewButtonsRight, 1);
        previewActionsRow.Children.Add(previewButtonsRight);

        var previewCard = Card("Live Preview", new StackPanel { Spacing = 8, Children =
        {
            previewStatusRow,
            previewActionsRow,
            _previewText,
            new TextBlock { Text = "Re-synthesizes fresh. Bypasses cache. Includes current DSP.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
        }});

        Control WrapAdvancedCard(string title, Control content)
            => new Expander
            {
                Header = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
                IsExpanded = false,
                Content = content
            };

        var scrollStack = new StackPanel
        {
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                new Expander
                {
                    Header = new TextBlock { Text = "Audio Effects (DSP)", FontWeight = FontWeight.SemiBold },
                    IsExpanded = false,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = dspContent
                },
                headerStack,
                WrapAdvancedCard("Standard Setup", presetCard),
                Card("Summary", new StackPanel { Spacing = 6, Children = { _summaryText } })
            }
        };
        var insertIndex = 1;
        if (chatterboxControlsCard != null)
            scrollStack.Children.Insert(insertIndex++, WrapAdvancedCard("Advanced Render Controls", chatterboxControlsCard));
        if (f5ControlsCard != null)
            scrollStack.Children.Insert(insertIndex++, WrapAdvancedCard("Advanced Render Controls", f5ControlsCard));
        if (longcatControlsCard != null)
            scrollStack.Children.Insert(insertIndex++, WrapAdvancedCard("Advanced Render Controls", longcatControlsCard));
        if (styleInstructionCard != null)
            scrollStack.Children.Insert(insertIndex++, WrapAdvancedCard("Style Instruction", styleInstructionCard));

        var fixedTop = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                topGrid,
                previewCard
            }
        };

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelBtn, saveBtn }
        };

        var optionsScrollViewer = new ScrollViewer
        {
            Content = scrollStack,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var rootGrid = new Grid
        {
            Margin = new Avalonia.Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10
        };
        rootGrid.Children.Add(fixedTop);
        Grid.SetRow(optionsScrollViewer, 1);
        rootGrid.Children.Add(optionsScrollViewer);
        Grid.SetRow(footerButtons, 2);
        rootGrid.Children.Add(footerButtons);
        Content = rootGrid;

        ApplyProfileToControls();
        RefreshStandardStatus();
        RefreshVoiceModeUi();
        RefreshSingleVoiceSummary();
        RefreshBlendSummary();
        RefreshSummary();
        // Subscribe to operation status to show synthesis progress in the dialog.
        // Must dispatch to UI thread since status fires from background threads.
        AppServices.OperationStatusChanged += _OnPreviewStatusChanged;
        Closing += (_, _) =>
        {
            AppServices.OperationStatusChanged -= _OnPreviewStatusChanged;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            AppServices.Player.Stop();
            AppServices.ClearOperationStatus();
        };
    }


    // ── DSP chain management ──────────────────────────────────────────────────

    private void RebuildChainPanel()
    {
        _dspChainPanel.Children.Clear();
        var effects = _workingProfile.Dsp?.Effects ?? new System.Collections.Generic.List<DspEffectItem>();

        for (int i = 0; i < effects.Count; i++)
        {
            var row = BuildEffectRow(effects[i], i, effects.Count);
            _dspChainPanel.Children.Add(row);
        }

        if (effects.Count == 0)
            _dspChainPanel.Children.Add(new TextBlock { Text = "No effects. Click '+ Add Effect' to begin.", Foreground = Brushes.Gray, FontSize = 11, Margin = new Avalonia.Thickness(0, 4) });
    }

    private Border BuildEffectRow(DspEffectItem item, int idx, int total)
    {
        var effects = _workingProfile.Dsp!.Effects;

        var enabledCheck = new CheckBox { IsChecked = item.Enabled, Margin = new Avalonia.Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        enabledCheck.IsCheckedChanged += (_, _) => { item.Enabled = enabledCheck.IsChecked == true; RefreshDspSummary(); RefreshSummary(); };

        var nameLabel = new TextBlock { Text = DspEffectItem.DisplayName(item.Kind), FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(nameLabel, DspEffectItem.Description(item.Kind));

        var upBtn = new Button { Content = "▲", FontSize = 10, Padding = new Avalonia.Thickness(4, 1), IsEnabled = idx > 0 };
        upBtn.Click += (_, _) => { effects.RemoveAt(idx); effects.Insert(idx - 1, item); RebuildChainPanel(); RefreshDspSummary(); };

        var downBtn = new Button { Content = "▼", FontSize = 10, Padding = new Avalonia.Thickness(4, 1), IsEnabled = idx < total - 1 };
        downBtn.Click += (_, _) => { effects.RemoveAt(idx); effects.Insert(idx + 1, item); RebuildChainPanel(); RefreshDspSummary(); };

        var removeBtn = new Button { Content = "✕", FontSize = 10, Padding = new Avalonia.Thickness(4, 1), Foreground = Brushes.IndianRed };
        removeBtn.Click += (_, _) => { effects.RemoveAt(idx); RebuildChainPanel(); RefreshDspSummary(); RefreshSummary(); };

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { enabledCheck, nameLabel } };
        var btnRow    = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Right, Children = { upBtn, downBtn, removeBtn } };
        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headerGrid.Children.Add(headerRow);
        Grid.SetColumn(btnRow, 1); headerGrid.Children.Add(btnRow);

        var paramsPanel = BuildParamsPanel(item);
        var content = new StackPanel { Spacing = 4, Margin = new Avalonia.Thickness(4), Children = { headerGrid, paramsPanel } };

        return new Border
        {
            Background    = SolidColorBrush.Parse("#16213E"),
            BorderBrush   = SolidColorBrush.Parse("#0F3460"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(4),
            Margin        = new Avalonia.Thickness(0, 2),
            Child         = content,
        };
    }

    private StackPanel BuildParamsPanel(DspEffectItem item)
    {
        var panel = new StackPanel { Spacing = 3 };

        void Row(string label, float min, float max, string key, float def, string fmt, string suffix, string tip)
        {
            var cur = item.Get(key, def);
            var displayAsPercent = string.IsNullOrEmpty(suffix) && min >= 0f && max <= 1f && item.Kind != DspEffectKind.WarmthGrit;
            var (slider, tb) = MakeCompactSlider(min, max, cur, fmt, suffix, v => { item.Set(key, v); RefreshDspSummary(); RefreshSummary(); }, displayAsPercent);
            panel.Children.Add(DspRow(label, (slider, tb), tip));
        }

        switch (item.Kind)
        {
            case DspEffectKind.EvenOut:
                Row("Amount",   -60,  0,   "threshold", -18f, "0",         " dB", "How loud the voice needs to get before it's reined in. -18 dB is a good starting point.");
                Row("Strength",   1, 20,   "ratio",       4f, "0.0",       ":1",  "How aggressively loud parts are brought down. 4:1 is natural; 10:1 is heavy.");
                break;
            case DspEffectKind.Level:
                panel.Children.Add(new TextBlock { Text = "No settings — automatically brings peak volume to 0 dBFS.", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
                break;
            case DspEffectKind.Pitch:
                Row("Pitch", -12, 12, "semitones", 0f, "+0.0;-0.0;0", " st", "How many semitones to shift. +12 is an octave up, -12 is an octave down. ±4 sounds natural.");
                break;
            case DspEffectKind.Speed:
                Row("Speed", -50, 50, "percent", 0f, "+0;-0;0", "%", "Positive = faster, negative = slower. Does not change the pitch.");
                break;
            case DspEffectKind.RumbleRemover:
                Row("Cut below", 20, 500, "hz", 100f, "0", " Hz", "Removes sound below this frequency. 80–150 Hz cleans up boxiness without thinning the voice.");
                break;
            case DspEffectKind.Bass:
                Row("Amount", -12, 12, "db", 0f, "+0.0;-0.0;0", " dB", "Positive adds warmth and body. Negative thins out a heavy or muddy voice.");
                break;
            case DspEffectKind.Presence:
                Row("Amount", -12,   12, "db",   0f,    "+0.0;-0.0;0", " dB", "Positive makes the voice push forward. Negative makes it sit back.");
                Row("Focus",  100, 8000, "hz",  1000f,  "0",           " Hz", "Where in the voice to boost or cut. 800–2000 Hz is the most noticeable range.");
                break;
            case DspEffectKind.Brightness:
                Row("Amount", -12, 12, "db", 0f, "+0.0;-0.0;0", " dB", "Positive makes the voice crisper. Negative makes it warmer and softer.");
                break;
            case DspEffectKind.Air:
                Row("Amount", 0, 1, "amount", 0.3f, "0.00", "", "How much sparkle to add. Start low — a little goes a long way.");
                break;
            case DspEffectKind.Grit:
            {
                var modeCombo = new ComboBox { ItemsSource = new[] { "Soft (warm crunch)", "Hard (harsh edge)", "Asymmetric (gritty drive)", "Full rectify (extreme buzz)", "Half rectify (fuzzy octave)" }, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Avalonia.Thickness(0, 0, 0, 4) };
                modeCombo.SelectedIndex = Math.Max(0, (int)item.Get("mode", 1f) - 1);
                modeCombo.SelectionChanged += (_, _) => { item.Set("mode", modeCombo.SelectedIndex + 1f); RefreshDspSummary(); };
                ToolTip.SetTip(modeCombo, "What kind of distortion character to use.");
                panel.Children.Add(modeCombo);
                Row("Drive",   0,   40, "inDb",   12f,  "0.0", " dB", "How much the signal is pushed into distortion. Higher = more grit.");
                Row("Output", -40,   0, "outDb", -12f,  "0.0", " dB", "Compensates for volume increase from distortion. Usually kept negative.");
                break;
            }
            case DspEffectKind.WarmthGrit:
                Row("Drive",  1,   20, "drive",  5f,   "0.0", "",  "How hard the tube is pushed. Higher = more grit. 3–8 is natural saturation.");
                Row("Tone",  -1,    0, "q",     -0.2f, "0.00","",  "Changes character at low volumes. More negative = cleaner at quiet parts.");
                break;
            case DspEffectKind.LoFi:
                Row("Degradation", 1, 16, "bits", 8f, "0", " bit", "Fewer bits = more degraded. 8 bit sounds like old games. 4 bit is extreme.");
                break;
            case DspEffectKind.Thickness:
                Row("Mix",    0,     1,    "wet",   0.5f,  "0.00", "",    "How much thickness to blend in. 0.3–0.5 is subtle.");
                Row("Speed",  0.1f,  4,    "rate",  1.5f,  "0.00", " Hz", "How fast the doubling effect pulses.");
                Row("Spread", 0.005f,0.04f,"width", 0.02f, "0.000"," s",  "How wide the doubling effect spreads.");
                break;
            case DspEffectKind.Wobble:
                Row("Depth", 0,    0.02f, "width", 0.005f, "0.000", " s",  "How much the pitch wavers. 0.003–0.008 is a gentle wobble.");
                Row("Speed", 0.5f, 10,    "rate",  3f,     "0.0",   " Hz", "How fast the wobble oscillates. 5+ Hz is unsettling.");
                break;
            case DspEffectKind.Swirl:
                Row("Mix",   0,    1,    "wet",   0.5f,  "0.00", "",    "How much of the swirling effect to add.");
                Row("Speed", 0.1f, 4,    "rate",  1f,    "0.00", " Hz", "How fast the sweep moves.");
                Row("Low",   20,   2000, "minHz", 300f,  "0",    " Hz", "Where the sweep starts at the bottom.");
                Row("High",  500,  8000, "maxHz", 3000f, "0",    " Hz", "Where the sweep reaches at the top.");
                break;
            case DspEffectKind.Jet:
                Row("Mix",       0,    1,   "wet",      0.5f, "0.00", "",    "How much of the metallic effect to blend in.");
                Row("Speed",     0.1f, 4,   "rate",     1f,   "0.00", " Hz", "How fast the whooshing oscillates.");
                Row("Resonance", 0,    0.9f,"feedback", 0.5f, "0.00", "",    "How pronounced and metallic the effect becomes.");
                break;
            case DspEffectKind.Wah:
                Row("Mix",  0,   1,    "wet",   0.5f,  "0.00", "",    "How much the wah effect blends in.");
                Row("Low",  20,  2000, "minHz", 300f,  "0",    " Hz", "The lowest point of the wah sweep.");
                Row("High", 500, 8000, "maxHz", 3000f, "0",    " Hz", "The highest point of the wah sweep.");
                break;
            case DspEffectKind.Tremor:
                Row("Depth", 0,    1,  "depth", 0.5f, "0.00", "",    "How much the volume pulses. Higher = more dramatic wavering.");
                Row("Speed", 0.5f, 12, "rate",  4f,   "0.0",  " Hz", "How fast the pulsing happens. 1–3 Hz = slow haunt. 8+ Hz = nervous tremor.");
                break;
            case DspEffectKind.Room:
                Row("Size",    0, 1, "roomSize", 0.5f, "0.00", "", "How large the simulated room is.");
                Row("Damping", 0, 1, "damping",  0.5f, "0.00", "", "Higher = warmer room. Lower = cold, bright, reflective space.");
                Row("Mix",     0, 1, "wet",      0.3f, "0.00", "", "How much of the room sound to blend in. 0.1–0.3 is subtle.");
                break;
            case DspEffectKind.Echo:
                Row("Delay",  0.05f, 1,   "delay",    0.3f, "0.00", " s", "How long before the echo repeats. 0.3 s is a quick slap. 0.7+ s is a large cave.");
                Row("Decay",  0,     0.9f,"feedback", 0.4f, "0.00", "",   "How many times the echo repeats before fading.");
                Row("Mix",    0,     1,   "wet",      0.5f, "0.00", "",   "How loud the echo is relative to the original voice.");
                break;
            case DspEffectKind.Robot:
            {
                var combo = new ComboBox { ItemsSource = new[] { "Subtle", "Moderate", "Strong", "Maximum" }, HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectedIndex = (int)Math.Clamp(item.Get("strength", 2f), 0, 3);
                combo.SelectionChanged += (_, _) => { item.Set("strength", combo.SelectedIndex); RefreshDspSummary(); };
                ToolTip.SetTip(combo, "How strongly the mechanical effect is applied.");
                panel.Children.Add(combo);
                break;
            }
            case DspEffectKind.Whisper:
            {
                var combo = new ComboBox { ItemsSource = new[] { "Subtle", "Moderate", "Strong", "Maximum" }, HorizontalAlignment = HorizontalAlignment.Stretch };
                combo.SelectedIndex = (int)Math.Clamp(item.Get("strength", 1f), 0, 3);
                combo.SelectionChanged += (_, _) => { item.Set("strength", combo.SelectedIndex); RefreshDspSummary(); };
                ToolTip.SetTip(combo, "How strongly the whispery effect is applied.");
                panel.Children.Add(combo);
                break;
            }
        }

        return panel;
    }

    private void RefreshDspSummary()
        => _dspSummaryLine.Text = BuildDspSummary();

    private string BuildDspSummary()
    {
        var dsp = _workingProfile.Dsp;
        if (dsp is null || !dsp.Enabled || dsp.IsNeutral) return "no effects";
        var active = dsp.Effects.Where(e => e.Enabled).Select(e => DspEffectItem.DisplayName(e.Kind));
        return string.Join(" \u2192 ", active);
    }


    // ─────────────────────────────────────────────────────────────────────────
    // Voice logic (unchanged from original)
    // ─────────────────────────────────────────────────────────────────────────

    private void RestoreStandardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_standardProfile == null)
            return;

        _workingProfile.CopyCacheAffectingFieldsFrom(_standardProfile);
        ApplyProfileToControls();
        RefreshVoiceModeUi();
        RefreshSingleVoiceSummary();
        RefreshBlendSummary();
        RefreshSummary();
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
        _speechRateText.Text    = FormatPercent(_workingProfile.SpeechRate);
        if (_cfgWeightSlider != null)
        {
            var cfg = _workingProfile.CfgWeight ?? (float)_cfgWeightSlider.Value;
            _cfgWeightSlider.Value = cfg;
            if (_cfgWeightText != null) _cfgWeightText.Text = cfg.ToString("0.00", Inv);
        }
        if (_exaggerationSlider != null)
        {
            var ex = _workingProfile.Exaggeration ?? (float)_exaggerationSlider.Value;
            _exaggerationSlider.Value = ex;
            if (_exaggerationText != null) _exaggerationText.Text = ex.ToString("0.00", Inv);
        }
        if (_cfgStrengthSlider != null)
        {
            var v = _workingProfile.CfgStrength ?? (float)_cfgStrengthSlider.Value;
            _cfgStrengthSlider.Value = v;
            if (_cfgStrengthText != null) _cfgStrengthText.Text = v.ToString("0.00", Inv);
        }
        if (_nfeStepSlider != null)
        {
            var v = (float)(_workingProfile.NfeStep ?? (int)_nfeStepSlider.Value);
            _nfeStepSlider.Value = v;
            if (_nfeStepText != null) _nfeStepText.Text = ((int)v).ToString();
        }
        if (_swaySlider != null)
        {
            var v = _workingProfile.SwaysamplingCoef ?? (float)_swaySlider.Value;
            _swaySlider.Value = v;
            if (_swayText != null) _swayText.Text = v.ToString("0.00", Inv);
        }
        if (_cbTemperatureSlider != null)
        {
            var v = _workingProfile.ChatterboxTemperature ?? (float)_cbTemperatureSlider.Value;
            _cbTemperatureSlider.Value = v;
            if (_cbTemperatureText != null) _cbTemperatureText.Text = v.ToString("0.00", Inv);
        }
        if (_cbTopPSlider != null)
        {
            var v = _workingProfile.ChatterboxTopP ?? (float)_cbTopPSlider.Value;
            _cbTopPSlider.Value = v;
            if (_cbTopPText != null) _cbTopPText.Text = v.ToString("0.00", Inv);
        }
        if (_cbRepetitionPenaltySlider != null)
        {
            var v = _workingProfile.ChatterboxRepetitionPenalty ?? (float)_cbRepetitionPenaltySlider.Value;
            _cbRepetitionPenaltySlider.Value = v;
            if (_cbRepetitionPenaltyText != null) _cbRepetitionPenaltyText.Text = v.ToString("0.00", Inv);
        }
        if (_seedText != null)
            _seedText.Text = _workingProfile.SynthesisSeed?.ToString(Inv) ?? string.Empty;
        if (_longcatStepsSlider != null)
        {
            var v = (float)(_workingProfile.LongcatSteps ?? (int)_longcatStepsSlider.Value);
            _longcatStepsSlider.Value = v;
            if (_longcatStepsText != null) _longcatStepsText.Text = ((int)v).ToString();
        }
        if (_longcatCfgStrengthSlider != null)
        {
            var v = _workingProfile.LongcatCfgStrength ?? (float)_longcatCfgStrengthSlider.Value;
            _longcatCfgStrengthSlider.Value = v;
            if (_longcatCfgStrengthText != null) _longcatCfgStrengthText.Text = v.ToString("0.00", Inv);
        }
        if (_longcatGuidanceCombo != null)
            _longcatGuidanceCombo.SelectedItem = _workingProfile.LongcatGuidance;
        if (_styleInstructionText != null)
        {
            _suppressStyleSync = true;
            _styleInstructionText.Text = _workingProfile.CosyInstruct ?? string.Empty;
            ApplyStyleInstructionToPresetControls(_workingProfile.CosyInstruct ?? string.Empty);
            _suppressStyleSync = false;
        }
        // Rebuild DSP chain to reflect any preset changes
        RebuildChainPanel();
        RefreshDspSummary();
    }

    private void SetVoiceSelection(string voiceId)
    {
        // Determine base and variant
        string baseId = voiceId;
        foreach (var s in new[] { "-slow", "-fast", "-quiet", "-loud", "-breathy" })
            if (voiceId.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            { baseId = voiceId[..^s.Length]; break; }

        // Select base
        var baseMatch = _voiceBaseCombo.ItemsSource?.OfType<VoiceChoice>()
            .FirstOrDefault(v => string.Equals(v.VoiceId, baseId, StringComparison.OrdinalIgnoreCase));
        if (baseMatch != null)
        {
            _voiceBaseCombo.SelectedItem = baseMatch;
            // Variant combo repopulates via SelectionChanged; now select the right variant
            if (!string.Equals(baseId, voiceId, StringComparison.OrdinalIgnoreCase))
            {
                var varMatch = _voiceVariantCombo.ItemsSource?.OfType<VoiceChoice>()
                    .FirstOrDefault(v => string.Equals(v.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase));
                if (varMatch != null)
                    _voiceVariantCombo.SelectedItem = varMatch;
            }
        }
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
                var first = ExtractFirstVoiceId(_workingProfile.VoiceId) ?? string.Empty;

                // Try to select the extracted voice. If it's not in the available list
                // (e.g. remote Kokoro uses samples, not Kokoro base voice IDs), fall back
                // to the first available voice in the combo instead of failing silently.
                var found = _voiceBaseCombo.ItemsSource?.OfType<VoiceChoice>()
                    .FirstOrDefault(v => string.Equals(v.VoiceId, first, StringComparison.OrdinalIgnoreCase));

                if (found != null)
                {
                    _workingProfile.VoiceId = first;
                    SetVoiceSelection(first);
                }
                else
                {
                    // Voice not in list — select first available
                    var firstAvailable = _voiceBaseCombo.ItemsSource?.OfType<VoiceChoice>().FirstOrDefault();
                    if (firstAvailable != null)
                    {
                        _workingProfile.VoiceId = firstAvailable.VoiceId;
                        _voiceBaseCombo.SelectedItem = firstAvailable;
                    }
                    else
                    {
                        _workingProfile.VoiceId = string.Empty;
                    }
                }
            }
            RefreshSingleVoiceSummary();
        }
    }

    private void VoiceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    private void VoiceVariantCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_singleVoiceRadio.IsChecked != true) return;
        // Variant combo tag holds full ID (base or base+variant)
        if (_voiceVariantCombo.SelectedItem is VoiceChoice c)
        {
            _workingProfile.VoiceId = c.VoiceId;
            RefreshSingleVoiceSummary();
            RefreshSummary();
        }
    }

    private void RefreshSingleVoiceSummary()
    {
        // Show summary from variant combo if it has a selection, else base combo
        var choice = _voiceVariantCombo.IsVisible && _voiceVariantCombo.SelectedItem is VoiceChoice vc && !string.IsNullOrWhiteSpace(vc.Summary)
            ? vc
            : _voiceBaseCombo.SelectedItem as VoiceChoice;
        _voiceSummaryText.Text = choice?.Summary ?? "Select a voice.";
    }

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
        _summaryText.Text    = $"Mode: {mode}\nVoice: {voiceText}\nDialect / Language: {lang}\nSpeech Rate: {FormatPercent(_workingProfile.SpeechRate)}\nEffects: {BuildDspSummary()}";
        _dspSummaryLine.Text = BuildDspSummary();
        RefreshStandardStatus();
    }

    private void RefreshStandardStatus()
    {
        if (_standardProfile == null)
        {
            _standardStateText.Text = "No standard setup is defined for this entry yet.";
            return;
        }

        _standardStateText.Text = _workingProfile.CacheAffectingEquals(_standardProfile)
            ? "Using Standard Setup"
            : "Customized";
    }

    private string BuildStandardSummary()
    {
        if (_standardProfile == null)
            return "No standard setup is defined for this entry yet.";

        var lang = EspeakLanguageCatalog.All.FirstOrDefault(x => string.Equals(x.Code, _standardProfile.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? _standardProfile.LangCode;
        var voiceText = string.IsNullOrWhiteSpace(_standardProfile.VoiceId)
            ? "(not selected)"
            : _standardProfile.VoiceId;
        return $"Standard voice: {voiceText}\nStandard dialect / language: {lang}\nStandard speech rate: {FormatPercent(_standardProfile.SpeechRate)}";
    }

    private static VoiceProfile? ResolveStandardProfile(string providerId, VoiceSlot slot, string? sampleProfileKey, IReadOnlyList<VoiceInfo> availableVoices)
    {
        if (!string.IsNullOrWhiteSpace(sampleProfileKey))
            return StandardVoiceProfileCatalog.TryGetSampleStandard(providerId, sampleProfileKey)
                ?? VoiceProfileDefaults.Create(sampleProfileKey);

        var standard = StandardVoiceProfileCatalog.TryGetVoiceStandard(providerId, slot);
        if (standard != null)
            return standard;

        var fallbackVoiceId = availableVoices.FirstOrDefault()?.VoiceId ?? string.Empty;
        return VoiceProfileDefaults.Create(fallbackVoiceId);
    }

    private async void EditBlendButton_Click(object? sender, RoutedEventArgs e)
    {
        var voices = _voiceBaseCombo.ItemsSource?.OfType<VoiceChoice>()
            .Select(v => new VoiceInfo { VoiceId = v.VoiceId, Name = v.VoiceId, Description = v.Summary, Language = string.Empty, Gender = Gender.Unknown })
            .ToArray() ?? Array.Empty<VoiceInfo>();
        var dialog = new VoiceMixDialog(voices, _workingProfile.VoiceId);
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
        // async void handlers must never throw — wrap entire body so exceptions
        // don't escape to the Avalonia dispatcher and crash the app.
        try { await PreviewButton_ClickAsync(sender, e); }
        catch (Exception ex)
        {
            AppServices.SetOperationStatus($"Preview error: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Preview] Unhandled: {ex}");
            _stopPreviewButton.IsEnabled = false;
            _previewButton.IsEnabled     = true;
        }
    }

    private async Task PreviewButton_ClickAsync(object? sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        _previewButton.IsEnabled = false;
        _stopPreviewButton.IsEnabled = true;
        var provider = AppServices.Provider;
        if (_singleVoiceRadio.IsChecked == true && string.IsNullOrWhiteSpace(_workingProfile.VoiceId))
            return;

        var original = provider.ResolveProfile(_slot)?.Clone();
        VoiceProfile? originalSampleProfile = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_sampleProfileKey))
            {
                if (!AppServices.Settings.PerProviderSampleProfiles.TryGetValue(provider.ProviderId, out var sampleDict))
                {
                    sampleDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    AppServices.Settings.PerProviderSampleProfiles[provider.ProviderId] = sampleDict;
                }

                if (sampleDict.TryGetValue(_sampleProfileKey, out var existingSample) && existingSample != null)
                    originalSampleProfile = existingSample.Clone();

                var sampleProfile = _workingProfile.Clone();
                sampleProfile.VoiceId = _sampleProfileKey;
                sampleDict[_sampleProfileKey] = sampleProfile;
            }
            else if (provider is KokoroTtsProvider kokoro)
                kokoro.SetVoiceProfile(_slot, _workingProfile);
            else if (AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(provider.ProviderId, out var dict))
                dict[_slot.ToString()] = _workingProfile.Clone();

            var previewText = _previewText.Text ?? string.Empty;
            PcmAudio pcm;
            if (provider is RemoteTtsProvider remote)
            {
                AppServices.SetOperationStatus("Requesting preview from server…");

                string voiceId;
                if (!string.IsNullOrWhiteSpace(_sampleProfileKey))
                {
                    var effectiveSample = remote.ResolveSampleProfile(_sampleProfileKey, _slot);
                    voiceId = $"sample:{effectiveSample.BuildIdentityKey()}";
                }
                else
                {
                    voiceId = provider.ResolveVoiceId(_slot);
                }

                var cached = await AppServices.Cache.TryGetDecodedAsync(previewText, voiceId, provider.ProviderId, "", ct);
                if (cached == null)
                {
                    var oggBytes = !string.IsNullOrWhiteSpace(_sampleProfileKey)
                        ? await remote.SynthesizeOggAsync(previewText, _slot, ct, bespokeSampleId: _sampleProfileKey)
                        : await remote.SynthesizeOggAsync(previewText, _slot, ct);
                    await AppServices.Cache.StoreOggAsync(oggBytes, previewText, voiceId, provider.ProviderId, "", ct);
                    AppServices.SetOperationStatus("Decoding preview…");
                    cached = await AppServices.Cache.TryGetDecodedAsync(previewText, voiceId, provider.ProviderId, "", ct);
                    if (cached == null)
                        throw new InvalidOperationException("Remote audio was cached but could not be decoded.");
                }
                pcm = cached;
            }
            else
            {
                AppServices.SetOperationStatus("Generating preview…");
                pcm = await provider.SynthesizeAsync(previewText, _slot, ct);
            }

            if (_workingProfile.Dsp is { IsNeutral: false } dspProfile)
                pcm = DspFilterChain.Apply(pcm, dspProfile);
            AppServices.SetOperationStatus("Playing preview…");
            await AppServices.Player.PlayAsync(pcm, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppServices.SetOperationStatus($"Preview failed: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[Preview] Error: {ex.Message}");
        }
        finally
        {
            AppServices.ClearOperationStatus();
            if (!_saved)
            {
                if (!string.IsNullOrWhiteSpace(_sampleProfileKey))
                {
                    if (AppServices.Settings.PerProviderSampleProfiles.TryGetValue(provider.ProviderId, out var sampleDict))
                    {
                        if (originalSampleProfile != null) sampleDict[_sampleProfileKey] = originalSampleProfile;
                        else sampleDict.Remove(_sampleProfileKey);
                    }
                }
                else if (provider is KokoroTtsProvider kokoro)
                    kokoro.SetVoiceProfile(_slot, original ?? VoiceProfileDefaults.Create(""));
                else if (AppServices.Settings.PerProviderVoiceProfiles.TryGetValue(provider.ProviderId, out var dict))
                {
                    if (original != null) dict[_slot.ToString()] = original;
                    else dict.Remove(_slot.ToString());
                }
            }
            _stopPreviewButton.IsEnabled = false;
            _previewButton.IsEnabled = true;
        }
    }

    private void _OnPreviewStatusChanged(string status)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            _previewStatusText.Text = status ?? string.Empty);
    }

    private void StopPreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        _previewCts?.Cancel();
        AppServices.Player.Stop();
        AppServices.ClearOperationStatus();
        _stopPreviewButton.IsEnabled = false;
        _previewButton.IsEnabled = true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Layout helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Parse a control default value that may be a numeric string ("0.5") or
    /// a non-numeric string ("en"). Returns fallback for non-numeric values.
    /// Needed because RemoteControlDescriptor.Default is now string? to support
    /// both float and string defaults from the server capability response.
    /// </summary>
    private static float ParseFloatDefault(string? value, float fallback)
        => float.TryParse(value, System.Globalization.NumberStyles.Float, Inv, out var f) ? f : fallback;

    private static int ParseIntDefault(string? value, int fallback)
        => int.TryParse(value, out var i) ? i : fallback;

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

    private static bool TryParsePercentOrNumber(string? text, out float value)
    {
        value = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.EndsWith("%", StringComparison.Ordinal))
            s = s[..^1].TrimEnd();

        if (!float.TryParse(s, System.Globalization.NumberStyles.Float, Inv, out var parsed))
            return false;

        value = parsed / 100f;
        return true;
    }

    private static float NormalizeSpeechRateForUi(float value)
        => Math.Clamp((float)Math.Round(value * 100f, MidpointRounding.AwayFromZero) / 100f, 0.25f, 4.00f);

    private static string FormatPercent(float value)
        => $"{Math.Clamp((int)Math.Round(value * 100f, MidpointRounding.AwayFromZero), 25, 400)}%";

    private static (Slider slider, TextBlock label) MakeCompactSlider(
        float min, float max, float initial,
        string fmt, string suffix, Action<float> onChange, bool displayAsPercent = false)
    {
        var valLabel = new TextBlock { Width = 72, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = 11 };
        void Upd(float v) => valLabel.Text = displayAsPercent ? FormatPercent(v) : v.ToString(fmt, Inv) + suffix;
        Upd(initial);

        var slider = new Slider { Minimum = min, Maximum = max, Value = initial, HorizontalAlignment = HorizontalAlignment.Stretch, TickFrequency = (max - min) / 50f };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            var v = (float)slider.Value;
            Upd(v);
            onChange(v);
        };
        return (slider, valLabel);
    }

    private static Grid DspRow(string rowLabel, (Slider s, TextBlock l) ctrl, string tooltip)
    {
        var g   = new Grid { ColumnDefinitions = new ColumnDefinitions("70,*,72"), ColumnSpacing = 8, Margin = new Avalonia.Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
        var lbl = new TextBlock { Text = rowLabel, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(lbl, tooltip); ToolTip.SetTip(ctrl.s, tooltip);
        Grid.SetColumn(ctrl.s, 1); Grid.SetColumn(ctrl.l, 2);
        g.Children.Add(lbl); g.Children.Add(ctrl.s); g.Children.Add(ctrl.l);
        return g;
    }

    private static void AddLabeledRow(Grid grid, int row, string label, Control control)
    {
        var lbl = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    private static string BuildStyleInstruction(string pace, string tone, string volume, string emotion)
    {
        var parts = new List<string>();

        if (pace == "Slow and deliberate") parts.Add("speak slowly and deliberately");
        else if (pace == "Fast and urgent") parts.Add("speak quickly with urgency");

        if (tone == "Gravitas") parts.Add("with weight and gravitas");
        else if (tone == "Weary") parts.Add("with a tired and weary tone");
        else if (tone == "Reverent") parts.Add("with reverence and solemnity");
        else if (tone == "Commanding") parts.Add("with authority and command");
        else if (tone == "Mysterious") parts.Add("with a mysterious and hushed quality");
        else if (tone == "Excited") parts.Add("with excitement and energy");
        else if (tone == "Fearful") parts.Add("with tension and barely concealed fear");

        if (volume == "Quiet and soft") parts.Add("speak softly and quietly");
        else if (volume == "Loud and booming") parts.Add("speak with full volume and projection");

        if (emotion == "Angry") parts.Add("with barely contained anger");
        else if (emotion == "Sad") parts.Add("with sorrow and heaviness");
        else if (emotion == "Joyful") parts.Add("with warmth and joy");
        else if (emotion == "Tense") parts.Add("with palpable tension");

        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    private void ApplyStyleInstructionToPresetControls(string text)
    {
        if (_stylePaceCombo == null || _styleToneCombo == null || _styleVolumeCombo == null || _styleEmotionCombo == null)
            return;

        if (string.IsNullOrWhiteSpace(text))
        {
            _stylePaceCombo.SelectedItem = "Normal";
            _styleToneCombo.SelectedItem = "Neutral";
            _styleVolumeCombo.SelectedItem = "Normal";
            _styleEmotionCombo.SelectedItem = "Neutral";
            _lastBuiltStyleInstruction = string.Empty;
            return;
        }

        _stylePaceCombo.SelectedItem = "Custom";
        _styleToneCombo.SelectedItem = "Custom";
        _styleVolumeCombo.SelectedItem = "Custom";
        _styleEmotionCombo.SelectedItem = "Custom";
        _lastBuiltStyleInstruction = text.Trim();
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