using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class VoiceProfileEditorDialog : Window
{
    private readonly VoiceSlot _slot;
    private readonly VoiceProfile _workingProfile;

    private readonly RadioButton _singleVoiceRadio;
    private readonly RadioButton _blendVoiceRadio;
    private readonly Border _singleVoiceSection;
    private readonly Border _blendVoiceSection;
    private readonly ComboBox _voiceCombo;
    private readonly TextBlock _voiceSummaryText;
    private readonly TextBlock _blendSummaryText;
    private readonly TextBlock _blendRawText;
    private readonly TextBlock _languageNameText;
    private readonly TextBlock _languageCodeText;
    private readonly Slider _speechRateSlider;
    private readonly TextBox _speechRateText;
    private readonly TextBox _previewText;
    private readonly TextBlock _summaryText;

    public VoiceProfileEditorDialog(
        VoiceSlot slot,
        string npcLabel,
        string accentLabel,
        VoiceProfile initialProfile)
    {
        _slot = slot;
        _workingProfile = initialProfile.Clone();

        Title = $"Edit NPC Voice Profile — {npcLabel}";
        Width = 760;
        Height = 700;
        MinWidth = 680;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = true;

        _singleVoiceRadio = new RadioButton
        {
            Content = "Single Voice",
            IsChecked = !_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)
        };
        _blendVoiceRadio = new RadioButton
        {
            Content = "Blend Voices",
            IsChecked = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase),
            GroupName = "voiceMode"
        };
        _singleVoiceRadio.GroupName = "voiceMode";
        _singleVoiceRadio.Checked += VoiceModeChanged;
        _blendVoiceRadio.Checked += VoiceModeChanged;

        _voiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = KokoroTtsProvider.KnownVoices.OrderBy(v => v.Name).ToArray()
        };
        _voiceCombo.SelectionChanged += VoiceCombo_SelectionChanged;

        _voiceSummaryText = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };

        var selectedVoice = KokoroTtsProvider.KnownVoices.FirstOrDefault(v =>
            string.Equals(v.VoiceId, _workingProfile.VoiceId, StringComparison.OrdinalIgnoreCase));
        if (selectedVoice != null)
            _voiceCombo.SelectedItem = selectedVoice;

        var editBlendButton = new Button { Content = "Edit Blend…", Width = 110 };
        editBlendButton.Click += EditBlendButton_Click;

        _blendSummaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        };

        _blendRawText = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };

        var currentLang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
            string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase));

        _languageNameText = new TextBlock
        {
            Text = currentLang?.DisplayName ?? _workingProfile.LangCode,
            VerticalAlignment = VerticalAlignment.Center
        };

        _languageCodeText = new TextBlock
        {
            Text = $"Code: {_workingProfile.LangCode}",
            Foreground = Brushes.Gray,
            FontSize = 11
        };

        var chooseLanguageButton = new Button { Content = "Choose…", Width = 100 };
        chooseLanguageButton.Click += ChooseLanguageButton_Click;

        _speechRateSlider = new Slider
        {
            Minimum = 0.50,
            Maximum = 1.50,
            Value = _workingProfile.SpeechRate,
            TickFrequency = 0.05,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _speechRateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _workingProfile.SpeechRate = (float)_speechRateSlider.Value;
                var text = _workingProfile.SpeechRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                if (_speechRateText.Text != text)
                    _speechRateText.Text = text;
                RefreshSummary();
            }
        };

        _speechRateText = new TextBox
        {
            Width = 80,
            Text = _workingProfile.SpeechRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _speechRateText.TextChanged += (_, _) =>
        {
            if (float.TryParse(_speechRateText.Text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                value = Math.Clamp(value, 0.50f, 1.50f);
                _workingProfile.SpeechRate = value;
                if (Math.Abs(_speechRateSlider.Value - value) > 0.001)
                    _speechRateSlider.Value = value;
                RefreshSummary();
            }
        };

        _previewText = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90,
            Text = "The tides of fate are shifting."
        };

        var previewButton = new Button { Content = "Preview", Width = 90 };
        previewButton.Click += PreviewButton_Click;

        var applyButton = new Button { Content = "Save & Close", Width = 120 };
        applyButton.Click += (_, _) => Close(_workingProfile.Clone());

        var cancelButton = new Button { Content = "Cancel", Width = 90 };
        cancelButton.Click += (_, _) => Close(null);

        _singleVoiceSection = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(10),
            CornerRadius = new Avalonia.CornerRadius(6),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Single Voice", FontWeight = FontWeight.SemiBold },
                    _voiceCombo,
                    _voiceSummaryText
                }
            }
        };

        _blendVoiceSection = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(10),
            CornerRadius = new Avalonia.CornerRadius(6),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Blend Voices", FontWeight = FontWeight.SemiBold },
                    _blendSummaryText,
                    editBlendButton,
                    new TextBlock
                    {
                        Text = "Advanced detail:",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 11,
                        Foreground = Brushes.Gray
                    },
                    _blendRawText
                }
            }
        };

        _summaryText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(12),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Applies to: {npcLabel}",
                        FontWeight = FontWeight.Bold,
                        FontSize = 16
                    },
                    new TextBlock
                    {
                        Text = $"Accent profile: {accentLabel}",
                        Opacity = 0.8
                    },
                    new TextBlock
                    {
                        Text = "This changes how RuneReader reads detected text for this NPC type. It does not change WoW’s built-in audio or settings.",
                        TextWrapping = TextWrapping.Wrap
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(10),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Voice Mode", FontWeight = FontWeight.SemiBold },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    Spacing = 16,
                                    Children = { _singleVoiceRadio, _blendVoiceRadio }
                                }
                            }
                        }
                    },

                    _singleVoiceSection,
                    _blendVoiceSection,

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(10),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Dialect / Language", FontWeight = FontWeight.SemiBold },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    Spacing = 8,
                                    Children = { _languageNameText, chooseLanguageButton }
                                },
                                _languageCodeText
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(10),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Voice Speech Rate", FontWeight = FontWeight.SemiBold },
                                new Grid
                                {
                                    ColumnDefinitions = new ColumnDefinitions("*,80"),
                                    ColumnSpacing = 10,
                                    Children =
                                    {
                                        _speechRateSlider,
                                        _speechRateText
                                    }
                                },
                                new TextBlock
                                {
                                    Text = "Affects synthesis voice delivery, not playback speed.",
                                    Foreground = Brushes.Gray,
                                    FontSize = 11,
                                    TextWrapping = TextWrapping.Wrap
                                }
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(10),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Live Preview", FontWeight = FontWeight.SemiBold },
                                _previewText,
                                previewButton,
                                new TextBlock
                                {
                                    Text = "Preview uses current unsaved settings and should not use cache or repeat suppression.",
                                    TextWrapping = TextWrapping.Wrap,
                                    Opacity = 0.8
                                }
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(10),
                        CornerRadius = new Avalonia.CornerRadius(6),
                        Child = new StackPanel
                        {
                            Spacing = 6,
                            Children =
                            {
                                new TextBlock { Text = "Summary", FontWeight = FontWeight.SemiBold },
                                _summaryText
                            }
                        }
                    },

                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancelButton, applyButton }
                    }
                }
            }
        };

        Grid.SetColumn(_speechRateText, 1);

        RefreshVoiceModeUi();
        RefreshSingleVoiceSummary();
        RefreshBlendSummary();
        RefreshSummary();
    }

    private void VoiceModeChanged(object? sender, RoutedEventArgs e)
    {
        RefreshVoiceModeUi();
        RefreshSummary();
    }

    private void RefreshVoiceModeUi()
    {
        var isBlend = _blendVoiceRadio.IsChecked == true;
        _singleVoiceSection.IsVisible = !isBlend;
        _blendVoiceSection.IsVisible = isBlend;

        if (isBlend)
        {
            if (!_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var fallbackVoice = !string.IsNullOrWhiteSpace(_workingProfile.VoiceId)
                    ? _workingProfile.VoiceId
                    : KokoroTtsProvider.DefaultVoiceId;
                _workingProfile.VoiceId = $"{KokoroTtsProvider.MixPrefix}{fallbackVoice}:1.0";
            }
            RefreshBlendSummary();
        }
        else
        {
            if (_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var first = ExtractFirstVoiceId(_workingProfile.VoiceId) ?? KokoroTtsProvider.DefaultVoiceId;
                _workingProfile.VoiceId = first;
                var match = KokoroTtsProvider.KnownVoices.FirstOrDefault(v => string.Equals(v.VoiceId, first, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    _voiceCombo.SelectedItem = match;
            }
            RefreshSingleVoiceSummary();
        }
    }

    private void VoiceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_singleVoiceRadio.IsChecked == true && _voiceCombo.SelectedItem is VoiceInfo vi)
        {
            _workingProfile.VoiceId = vi.VoiceId;
            RefreshSingleVoiceSummary();
            RefreshSummary();
        }
    }

    private void RefreshSingleVoiceSummary()
    {
        if (_voiceCombo.SelectedItem is VoiceInfo vi)
            _voiceSummaryText.Text = $"{vi.Name} · {vi.Language}";
        else
            _voiceSummaryText.Text = string.IsNullOrWhiteSpace(_workingProfile.VoiceId)
                ? "Select a voice."
                : _workingProfile.VoiceId;
    }

    private void RefreshBlendSummary()
    {
        var parts = ParseBlend(_workingProfile.VoiceId);
        if (parts.Length == 0)
        {
            _blendSummaryText.Text = "No blend configured.";
            _blendRawText.Text = "";
            return;
        }

        var summary = string.Join(" + ", parts.Select(p =>
        {
            var voice = KokoroTtsProvider.KnownVoices.FirstOrDefault(v => string.Equals(v.VoiceId, p.voiceId, StringComparison.OrdinalIgnoreCase));
            var label = voice?.Name ?? p.voiceId;
            return $"{label} {(p.weight * 100):0.#}%";
        }));

        _blendSummaryText.Text = summary;
        _blendRawText.Text = _workingProfile.VoiceId;
    }

    private void RefreshSummary()
    {
        var lang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
            string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? _workingProfile.LangCode;

        var mode = _blendVoiceRadio.IsChecked == true ? "Blend Voices" : "Single Voice";
        var voiceText = _blendVoiceRadio.IsChecked == true ? _blendSummaryText.Text : _voiceSummaryText.Text;

        _summaryText.Text =
            $"Mode: {mode}\n" +
            $"Voice: {voiceText}\n" +
            $"Dialect / Language: {lang}\n" +
            $"Code: {_workingProfile.LangCode}\n" +
            $"Voice Speech Rate: {_workingProfile.SpeechRate:0.00}x";
    }

    private async void EditBlendButton_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new VoiceMixDialog(KokoroTtsProvider.KnownVoices, _workingProfile.VoiceId);
        await dialog.ShowDialog(this);

        if (!string.IsNullOrWhiteSpace(dialog.ResultSpec))
        {
            _workingProfile.VoiceId = dialog.ResultSpec!;
            RefreshBlendSummary();
            RefreshSummary();
        }
    }

    private async void ChooseLanguageButton_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new LanguagePickerDialog(_workingProfile.LangCode);
        var selected = await dlg.ShowDialog<EspeakLanguageOption?>(this);
        if (selected == null)
            return;

        _workingProfile.LangCode = selected.Code;
        _languageNameText.Text = selected.DisplayName;
        _languageCodeText.Text = $"Code: {selected.Code}";
        RefreshSummary();
    }

    private async void PreviewButton_Click(object? sender, RoutedEventArgs e)
    {
        if (AppServices.Provider is not KokoroTtsProvider kokoro)
            return;

        var original = kokoro.ResolveVoiceProfile(_slot);
        try
        {
            kokoro.SetVoiceProfile(_slot, _workingProfile);
            var pcm = await kokoro.SynthesizeAsync(_previewText.Text ?? "", _slot, CancellationToken.None);

            var tempPath = Path.Combine(Path.GetTempPath(), $"rrv_preview_{Guid.NewGuid():N}.wav");
            WriteWaveFile(tempPath, pcm);
            await AppServices.Player.PlayAsync(tempPath, CancellationToken.None);
        }
        finally
        {
            kokoro.SetVoiceProfile(_slot, original);
        }
    }

    private static string? ExtractFirstVoiceId(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var raw = spec[KokoroTtsProvider.MixPrefix.Length..];
        var first = raw.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return null;

        var colon = first.LastIndexOf(':');
        return colon > 0 ? first[..colon] : first;
    }

    private static (string voiceId, float weight)[] ParseBlend(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || !spec.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<(string voiceId, float weight)>();

        var raw = spec[KokoroTtsProvider.MixPrefix.Length..];
        var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return parts.Select(part =>
        {
            var colon = part.LastIndexOf(':');
            if (colon < 0)
                return (part, 1f);

            var id = part[..colon];
            var weight = float.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1f;
            return (id, weight);
        }).ToArray();
    }

    private static void WriteWaveFile(string path, PcmAudio audio)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        int channels = audio.Channels <= 0 ? 1 : audio.Channels;
        int sampleRate = audio.SampleRate;
        short bitsPerSample = 16;
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;
        int dataSize = audio.Samples.Length * 2;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        foreach (var sample in audio.Samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            short s = (short)Math.Round(clamped * short.MaxValue);
            bw.Write(s);
        }
    }
}