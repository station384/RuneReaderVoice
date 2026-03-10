using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class VoiceProfileEditorDialog : Window
{
    private readonly VoiceSlot _slot;
    private readonly VoiceProfile _workingProfile;

    private readonly RadioButton _singleVoiceRadio;
    private readonly RadioButton _blendVoiceRadio;
    private readonly ComboBox _voiceCombo;
    private readonly TextBox _blendSpecText;
    private readonly TextBlock _languageNameText;
    private readonly TextBlock _languageCodeText;
    private readonly Slider _speechRateSlider;
    private readonly TextBox _speechRateText;
    private readonly TextBox _previewText;

    public VoiceProfileEditorDialog(
        VoiceSlot slot,
        string npcLabel,
        string accentLabel,
        VoiceProfile initialProfile)
    {
        _slot = slot;
        _workingProfile = initialProfile.Clone();

        Title = $"Edit NPC Voice Profile — {npcLabel}";
        Width = 700;
        Height = 620;
        MinWidth = 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _singleVoiceRadio = new RadioButton { Content = "Single Voice", IsChecked = !_workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase) };
        _blendVoiceRadio = new RadioButton { Content = "Blend Voices", IsChecked = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase) };

        _voiceCombo = new ComboBox
        {
            Width = 320,
            ItemsSource = KokoroTtsProvider.KnownVoices.OrderBy(v => v.Name).ToArray()
        };
        _voiceCombo.SelectionChanged += VoiceCombo_SelectionChanged;

        var selectedVoice = KokoroTtsProvider.KnownVoices.FirstOrDefault(v => string.Equals(v.VoiceId, _workingProfile.VoiceId, StringComparison.OrdinalIgnoreCase));
        if (selectedVoice != null)
            _voiceCombo.SelectedItem = selectedVoice;

        _blendSpecText = new TextBox
        {
            Text = _workingProfile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase)
                ? _workingProfile.VoiceId
                : "",
            Watermark = "mix:af_bella:0.50|bm_lewis:0.50"
        };
        _blendSpecText.TextChanged += (_, _) =>
        {
            if (_blendVoiceRadio.IsChecked == true)
                _workingProfile.VoiceId = _blendSpecText.Text ?? "";
        };

        var currentLang = EspeakLanguageCatalog.All.FirstOrDefault(x =>
            string.Equals(x.Code, _workingProfile.LangCode, StringComparison.OrdinalIgnoreCase));

        _languageNameText = new TextBlock
        {
            Text = currentLang?.DisplayName ?? _workingProfile.LangCode
        };

        _languageCodeText = new TextBlock
        {
            Text = $"Code: {_workingProfile.LangCode}"
        };

        var chooseLanguageButton = new Button { Content = "Choose...", Width = 100 };
        chooseLanguageButton.Click += ChooseLanguageButton_Click;

        _speechRateSlider = new Slider
        {
            Minimum = 0.50,
            Maximum = 1.50,
            Value = _workingProfile.SpeechRate,
            TickFrequency = 0.05
        };
        _speechRateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == Slider.ValueProperty)
            {
                _workingProfile.SpeechRate = (float)_speechRateSlider.Value;
                _speechRateText.Text = _workingProfile.SpeechRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
        };

        _speechRateText = new TextBox
        {
            Width = 80,
            Text = _workingProfile.SpeechRate.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
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
            }
        };

        _previewText = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 90,
            Text = "The tides of fate are shifting."
        };

        var previewButton = new Button { Content = "Preview", Width = 90 };
        previewButton.Click += PreviewButton_Click;

        var applyButton = new Button { Content = "Save & Close", Width = 120 };
        applyButton.Click += (_, _) => Close(_workingProfile.Clone());

        var cancelButton = new Button { Content = "Cancel", Width = 90 };
        cancelButton.Click += (_, _) => Close(null);

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
                        FontWeight = Avalonia.Media.FontWeight.Bold,
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
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(8),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Voice Mode", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    Spacing = 12,
                                    Children = { _singleVoiceRadio, _blendVoiceRadio }
                                }
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(8),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Single Voice", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                                _voiceCombo,
                                new TextBlock { Text = "Blend Spec", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Avalonia.Thickness(0,6,0,0) },
                                _blendSpecText
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(8),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Dialect / Language", FontWeight = Avalonia.Media.FontWeight.SemiBold },
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
                        Padding = new Avalonia.Thickness(8),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Voice Speech Rate", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                                new Grid
                                {
                                    ColumnDefinitions = new ColumnDefinitions("*,80"),
                                    ColumnSpacing = 10,
                                    Children =
                                    {
                                        _speechRateSlider,
                                        _speechRateText
                                    }
                                }
                            }
                        }
                    },

                    new Border
                    {
                        BorderThickness = new Avalonia.Thickness(1),
                        Padding = new Avalonia.Thickness(8),
                        Child = new StackPanel
                        {
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Live Preview", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                                _previewText,
                                previewButton,
                                new TextBlock
                                {
                                    Text = "Preview uses current unsaved settings and should not use cache or repeat suppression.",
                                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                    Opacity = 0.8
                                }
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
    }

    private void VoiceCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_singleVoiceRadio.IsChecked == true && _voiceCombo.SelectedItem is VoiceInfo vi)
            _workingProfile.VoiceId = vi.VoiceId;
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
            var path = await AppServices.Cache.StoreAsync(
                pcm,
                Guid.NewGuid().ToString("N"),
                $"preview|{_workingProfile.BuildIdentityKey()}",
                "preview",
                CancellationToken.None);
            await AppServices.Player.PlayAsync(path, CancellationToken.None);
        }
        finally
        {
            kokoro.SetVoiceProfile(_slot, original);
        }
    }
}