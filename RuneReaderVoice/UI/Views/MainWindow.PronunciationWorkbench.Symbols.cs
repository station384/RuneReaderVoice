using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private void PopulatePronunciationSymbolGroup(WrapPanel panel, string category)
    {
        panel.Children.Clear();

        foreach (var symbol in PronunciationWorkbenchCatalog.GetByCategory(category))
        {
            var button = BuildPronunciationSymbolButton(symbol);
            panel.Children.Add(button);
        }
    }

    private Button BuildPronunciationSymbolButton(PronunciationSymbol symbol)
    {
        var button = new Button
        {
            Content = symbol.ButtonLabel,
            Tag = symbol,
            MinWidth = 78,
            Height = 28,
            Margin = new Avalonia.Thickness(0, 0, 6, 6),
            Background = Avalonia.Media.SolidColorBrush.Parse("#0F3460"),
            Foreground = Avalonia.Media.Brushes.WhiteSmoke,
            FontSize = 13
        };

        ToolTip.SetTip(
            button,
            $"{symbol.Symbol} — {symbol.Name}\n{symbol.Description}\nExample: {symbol.Example}");

        button.Click += OnPronunciationSymbolClicked;
        return button;
    }

    private void OnPronunciationSymbolClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PronunciationSymbol symbol })
            return;

        ShowPronunciationSymbolDetails(symbol);
        InsertSymbolIntoPhonemeText(symbol.Symbol);

        SavePronunciationWorkbenchState();
        UpdatePronunciationPreview();
        PronPhonemeText.Focus();
    }

    private void ShowPronunciationSymbolDetails(PronunciationSymbol symbol)
    {
        PronSymbolTitle.Text = $"{symbol.Symbol}  —  {symbol.Name} ({symbol.Category})";
        PronSymbolDescription.Text = symbol.Description;
        PronSymbolExample.Text = $"Example: {symbol.Example}";
    }

    private void InsertSymbolIntoPhonemeText(string insertText)
    {
        var tb = PronPhonemeText;
        var existing = tb.Text ?? string.Empty;
        var caret = tb.CaretIndex;

        if (caret < 0 || caret > existing.Length)
            caret = existing.Length;

        tb.Text = existing.Insert(caret, insertText);
        tb.CaretIndex = caret + insertText.Length;
    }
}