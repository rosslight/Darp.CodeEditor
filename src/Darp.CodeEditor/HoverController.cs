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
        // Paragraphs in the UI font; code blocks in the editor's font.
        _text = new TextBlock { MaxWidth = 600, TextWrapping = TextWrapping.Wrap };
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
        // Typing makes the information stale; cancelling the request tells the server it is no longer needed.
        editor.TextChanged += (_, _) => Close();
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

        MarkupBlock[] diagnostics =
        [
            .. _features
                .DiagnosticsAt(offset)
                .Select(d => new MarkupBlock(MarkupBlockKind.Paragraph, $"{d.Id}: {d.Message}")),
        ];
        Show(diagnostics, offset, pointer.X);

        if (_features.Service is not { } service)
            return;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        (DocumentVersion document, _) = _features.CreateSnapshot();
        QuickInfo? info = await _features.RequestAsync(
            () => service.GetQuickInfoAsync(document, offset, cancellation.Token),
            cancellation.Token
        );
        if (info is null || cancellation.IsCancellationRequested)
            return;
        Show([.. info.Content, .. diagnostics], offset, pointer.X);
    }

    private void Show(IReadOnlyList<MarkupBlock> content, int offset, double x)
    {
        if (content.Count == 0)
            return;
        MarkupText.Show(_text, content, _editor.FontFamily);
        _popup.Show(offset, x);
    }

    private void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _popup.Hide();
    }
}
