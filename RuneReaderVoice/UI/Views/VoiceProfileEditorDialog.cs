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

    // F5-TTS specific controls
    private readonly Slider?     _cfgStrengthSlider;
    private readonly TextBox?    _cfgStrengthText;
    private readonly Slider?     _nfeStepSlider;
    private readonly TextBox?    _nfeStepText;
    private readonly Slider?     _swaySlider;
    private readonly TextBox?    _swayText;
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

        var presetItems = SpeakerPresetCatalog.GetForSlot(slot).ToArray();
        var isKokoroProvider = supportsPresets;
        _voiceSourceLabel = string.IsNullOrWhiteSpace(voiceSourceLabel) ? "voice" : voiceSourceLabel;
        _presetCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = presetItems };
        _presetCombo.SelectionChanged += PresetCombo_SelectionChanged;
        _presetDescriptionText = new TextBlock { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        var useRecommendedBtn = Btn("Use Recommended", 130); useRecommendedBtn.Click += UseRecommendedButton_Click; useRecommendedBtn.IsEnabled = isKokoroProvider;
        var applyPresetBtn    = Btn("Apply Preset",    110); applyPresetBtn.Click    += ApplyPresetButton_Click; applyPresetBtn.IsEnabled = isKokoroProvider;

        // ── Voice mode ────────────────────────────────────────────────────────

        _singleVoiceRadio = new RadioButton { Content = "Single Voice", IsChecked = !_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode" };
        _blendVoiceRadio  = new RadioButton { Content = "Blend Voices", IsChecked  = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase), GroupName = "voiceMode", IsEnabled = supportsBlend };
        _singleVoiceRadio.Checked += VoiceModeChanged;
        _blendVoiceRadio.Checked  += VoiceModeChanged;

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

        _speechRateSlider = new Slider { Minimum = 0.25, Maximum = 4.00, Value = _workingProfile.SpeechRate, TickFrequency = 0.05, HorizontalAlignment = HorizontalAlignment.Stretch };
        _speechRateText   = new TextBox { Width = 80, Text = FormatPercent(_workingProfile.SpeechRate), HorizontalContentAlignment = HorizontalAlignment.Center };
        _speechRateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            _workingProfile.SpeechRate = (float)_speechRateSlider.Value;
            var t = FormatPercent(_workingProfile.SpeechRate);
            if (_speechRateText.Text != t) _speechRateText.Text = t;
            RefreshSummary();
        };
        _speechRateText.TextChanged += (_, _) =>
        {
            if (!TryParsePercentOrNumber(_speechRateText.Text, out var v)) return;
            v = Math.Clamp(v, 0.25f, 4.00f);
            _workingProfile.SpeechRate = v;
            if (Math.Abs(_speechRateSlider.Value - v) > 0.001) _speechRateSlider.Value = v;
            RefreshSummary();
        };


        // ── Provider-specific render controls ───────────────────────────────

        controls ??= new Dictionary<string, RemoteControlDescriptor>(StringComparer.OrdinalIgnoreCase);
        controls.TryGetValue("cfg_weight", out var cfgControl);
        controls.TryGetValue("exaggeration", out var exagControl);

        Border? chatterboxControlsCard = null;
        _cfgWeightSlider    = null;
        _cfgWeightText      = null;
        _exaggerationSlider = null;
        _exaggerationText   = null;

        Border? f5ControlsCard = null;
        _cfgStrengthSlider = null;
        _cfgStrengthText   = null;
        _nfeStepSlider     = null;
        _nfeStepText       = null;
        _swaySlider        = null;
        _swayText          = null;

        if (cfgControl != null || exagControl != null)
        {
            float cfgMin = cfgControl?.Min ?? 0f;
            float cfgMax = cfgControl?.Max ?? 3f;
            float cfgDefault = cfgControl?.Default ?? 0f;
            float exMin = exagControl?.Min ?? 0f;
            float exMax = exagControl?.Max ?? 3f;
            float exDefault = exagControl?.Default ?? 0f;

            if (!_workingProfile.CfgWeight.HasValue)
                _workingProfile.CfgWeight = cfgDefault;
            if (!_workingProfile.Exaggeration.HasValue)
                _workingProfile.Exaggeration = exDefault;

            var controlRows = new StackPanel { Spacing = 8 };

            if (cfgControl != null)
            {
                _cfgWeightSlider = new Slider { Minimum = cfgMin, Maximum = cfgMax, Value = _workingProfile.CfgWeight ?? cfgDefault, TickFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _cfgWeightText = new TextBox { Width = 80, Text = (_workingProfile.CfgWeight ?? cfgDefault).ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                _cfgWeightSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Slider.ValueProperty) return;
                    _workingProfile.CfgWeight = (float)_cfgWeightSlider.Value;
                    var t = _workingProfile.CfgWeight.Value.ToString("0.00", Inv);
                    if (_cfgWeightText.Text != t) _cfgWeightText.Text = t;
                    RefreshSummary();
                };
                _cfgWeightText.TextChanged += (_, _) =>
                {
                    if (!float.TryParse(_cfgWeightText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, cfgMin, cfgMax);
                    _workingProfile.CfgWeight = v;
                    if (Math.Abs(_cfgWeightSlider.Value - v) > 0.001) _cfgWeightSlider.Value = v;
                    RefreshSummary();
                };

                var cfgGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,70,70"), ColumnSpacing = 8 };
                cfgGrid.Children.Add(new TextBlock { Text = "CFG Weight", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_cfgWeightSlider, 1);
                cfgGrid.Children.Add(_cfgWeightSlider);
                Grid.SetColumn(_cfgWeightText, 2);
                cfgGrid.Children.Add(_cfgWeightText);
                var cfgResetBtn = Btn("Default", 70);
                cfgResetBtn.Click += (_, _) =>
                {
                    _workingProfile.CfgWeight = cfgDefault;
                    _cfgWeightSlider.Value = cfgDefault;
                    _cfgWeightText.Text = cfgDefault.ToString("0.00", Inv);
                    RefreshSummary();
                };
                Grid.SetColumn(cfgResetBtn, 3);
                cfgGrid.Children.Add(cfgResetBtn);
                controlRows.Children.Add(cfgGrid);
                if (!string.IsNullOrWhiteSpace(cfgControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = cfgControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
            }

            if (exagControl != null)
            {
                _exaggerationSlider = new Slider { Minimum = exMin, Maximum = exMax, Value = _workingProfile.Exaggeration ?? exDefault, TickFrequency = 0.1, HorizontalAlignment = HorizontalAlignment.Stretch };
                _exaggerationText = new TextBox { Width = 80, Text = (_workingProfile.Exaggeration ?? exDefault).ToString("0.00", Inv), HorizontalContentAlignment = HorizontalAlignment.Center };
                _exaggerationSlider.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Slider.ValueProperty) return;
                    _workingProfile.Exaggeration = (float)_exaggerationSlider.Value;
                    var t = _workingProfile.Exaggeration.Value.ToString("0.00", Inv);
                    if (_exaggerationText.Text != t) _exaggerationText.Text = t;
                    RefreshSummary();
                };
                _exaggerationText.TextChanged += (_, _) =>
                {
                    if (!float.TryParse(_exaggerationText.Text, System.Globalization.NumberStyles.Float, Inv, out var v)) return;
                    v = Math.Clamp(v, exMin, exMax);
                    _workingProfile.Exaggeration = v;
                    if (Math.Abs(_exaggerationSlider.Value - v) > 0.001) _exaggerationSlider.Value = v;
                    RefreshSummary();
                };

                var exGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*,70,70"), ColumnSpacing = 8 };
                exGrid.Children.Add(new TextBlock { Text = "Exaggeration", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
                Grid.SetColumn(_exaggerationSlider, 1);
                exGrid.Children.Add(_exaggerationSlider);
                Grid.SetColumn(_exaggerationText, 2);
                exGrid.Children.Add(_exaggerationText);
                var exResetBtn = Btn("Default", 70);
                exResetBtn.Click += (_, _) =>
                {
                    _workingProfile.Exaggeration = exDefault;
                    _exaggerationSlider.Value = exDefault;
                    _exaggerationText.Text = exDefault.ToString("0.00", Inv);
                    RefreshSummary();
                };
                Grid.SetColumn(exResetBtn, 3);
                exGrid.Children.Add(exResetBtn);
                controlRows.Children.Add(exGrid);
                if (!string.IsNullOrWhiteSpace(exagControl.Description))
                    controlRows.Children.Add(new TextBlock { Text = exagControl.Description, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 0, 0, 4) });
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
                float def = cfgStrControl.Default ?? 2.0f;
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
                float def = nfeControl.Default ?? 48f;
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
                float def = swayControl.Default ?? -1.0f;
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

        var dspCard = Card("Audio Effects (DSP)", new StackPanel
        {
            Spacing = 8,
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
        });

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

        _singleVoiceSection = Card("Single Voice", new StackPanel { Spacing = 8, Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Children = { _voiceBaseCombo, _voiceVariantCombo }
            },
            _voiceSummaryText
        }});
        _blendVoiceSection  = Card("Blend Voices", new StackPanel { Spacing = 8, Children = { _blendSummaryText, editBlendBtn } });

        var presetCard    = Card("Voice Preset", new StackPanel { Spacing = 8, Children = { _presetCombo, _presetDescriptionText, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { useRecommendedBtn, applyPresetBtn } } } });
        var voiceModeCard = Card("Voice Mode",   new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _singleVoiceRadio, _blendVoiceRadio } } } });
        var languageCard  = Card("Dialect / Language", new StackPanel { Spacing = 8, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _languageNameText, chooseLangBtn } } } });
        if (isSampleDefaultsEditor)
        {
            presetCard.IsVisible = false;
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
            topGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 10 };
            AddToGrid(topGrid, presetCard,          0, 0);
            AddToGrid(topGrid, _blendVoiceSection,  0, 1);
            AddToGrid(topGrid, voiceModeCard,       1, 0);
            AddToGrid(topGrid, languageCard,        1, 1);
            AddToGrid(topGrid, _singleVoiceSection, 2, 0);
            AddToGrid(topGrid, rateCard,            2, 1);
        }

        var contentStack = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Applies to: {npcLabel}", FontWeight = FontWeight.Bold, FontSize = 16 },
                new TextBlock { Text = $"Accent profile: {accentLabel}", Opacity = 0.8 },
                new TextBlock { Text = "This changes how RuneReader reads detected text for this NPC type. It does not change WoW's built-in audio or settings.", TextWrapping = TextWrapping.Wrap },
                topGrid,
                dspCard,
                Card("Live Preview", new StackPanel { Spacing = 8, Children =
                {
                    previewPresetButtons,
                    _previewText, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _previewButton, _stopPreviewButton, _previewStatusText } },
                    new TextBlock { Text = "Re-synthesizes fresh. Bypasses cache. Includes current DSP.", TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                }}),
                Card("Summary", new StackPanel { Spacing = 6, Children = { _summaryText } }),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancelBtn, saveBtn } }
            }
        };
        if (chatterboxControlsCard != null)
            contentStack.Children.Insert(4, chatterboxControlsCard);
        if (f5ControlsCard != null)
            contentStack.Children.Insert(chatterboxControlsCard != null ? 5 : 4, f5ControlsCard);

        Content = new ScrollViewer
        {
            Content = contentStack
        };

        ApplyProfileToControls();
        TrySelectMatchingPreset();
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

    private static string FormatPercent(float value)
        => $"{value * 100f:0.#}%";

    private static (Slider slider, TextBlock label) MakeCompactSlider(
        float min, float max, float initial,
        string fmt, string suffix, Action<float> onChange, bool displayAsPercent = false)
    {
        var valLabel = new TextBlock { Width = 60, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, FontSize = 11 };
        void Upd(float v) => valLabel.Text = displayAsPercent ? FormatPercent(v) : v.ToString(fmt, Inv) + suffix;
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

    private static Grid DspRow(string rowLabel, (Slider s, TextBlock l) ctrl, string tooltip)
    {
        var g   = new Grid { ColumnDefinitions = new ColumnDefinitions("55,100,*"), ColumnSpacing = 4, Margin = new Avalonia.Thickness(0, 1, 0, 1) };
        var lbl = new TextBlock { Text = rowLabel, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(lbl, tooltip); ToolTip.SetTip(ctrl.s, tooltip);
        Grid.SetColumn(ctrl.s, 1); Grid.SetColumn(ctrl.l, 2);
        g.Children.Add(lbl); g.Children.Add(ctrl.s); g.Children.Add(ctrl.l);
        return g;
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