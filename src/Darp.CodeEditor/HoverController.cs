using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor;

/// <summary>Shows diagnostics and quick info for the text under the pointer, below its line.</summary>
internal sealed class HoverController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private readonly TextBlock _text;
    private readonly Border _border;
    private readonly TextPopup _popup;
    private CancellationTokenSource? _cancellation;

    public HoverController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        _text = new TextBlock
        {
            MaxWidth = 600,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = editor.FontFamily,
        };
        _border = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4),
            Child = _text,
        };
        // Below the hovered line, or above it: a popup over the pointer would end the hover that opened it.
        _popup = new TextPopup(editor, _border, above: false);
        ApplyTheme(editor.EditorTheme);
        editor.EditorThemeChanged += (_, _) => ApplyTheme(editor.EditorTheme);
        editor.PointerHover += OnPointerHover;
        editor.PointerHoverStopped += (_, _) => Close();
    }

    private void ApplyTheme(EditorTheme theme)
    {
        _border.Background = theme.WidgetBackground;
        _border.BorderBrush = theme.WidgetBorder;
        _text.Foreground = theme.WidgetForeground;
    }

    private async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        Close();
        TextView textView = _editor.TextArea.TextView;
        Point pointer = e.GetPosition(textView);
        TextViewPosition? position = textView.GetPositionFloor(pointer + textView.ScrollOffset);
        if (position is not { } p)
            return;
        int offset = _editor.Document.GetOffset(p.Location);

        string diagnostics = string.Join(
            Environment.NewLine,
            _features.DiagnosticsAt(offset).Select(d => $"{d.Id}: {d.Message}")
        );
        Show(diagnostics, offset, pointer.X);

        if (_features.Service is not { } service)
            return;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        (DocumentSnapshot snapshot, _) = _features.CreateSnapshot();
        QuickInfo? info = await _features.RequestAsync(
            () => service.GetQuickInfoAsync(snapshot, offset, cancellation.Token),
            cancellation.Token
        );
        if (info is null || cancellation.IsCancellationRequested)
            return;
        Show(
            diagnostics.Length == 0 ? info.Text : $"{info.Text}{Environment.NewLine}{Environment.NewLine}{diagnostics}",
            offset,
            pointer.X
        );
    }

    private void Show(string text, int offset, double x)
    {
        if (text.Length == 0)
            return;
        _text.Text = text;
        _popup.Show(offset, x);
    }

    private void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _popup.Hide();
    }
}
