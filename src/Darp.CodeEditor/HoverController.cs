using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor;

/// <summary>Shows diagnostics and quick info for the text under the pointer.</summary>
internal sealed class HoverController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private readonly Popup _popup;
    private readonly TextBlock _text;
    private CancellationTokenSource? _cancellation;

    public HoverController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        _text = new TextBlock
        {
            MaxWidth = 600,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gainsboro,
            FontFamily = editor.FontFamily,
        };
        _popup = new Popup
        {
            // Below the pointer: a popup under the pointer would end the hover that opened it.
            Placement = PlacementMode.Pointer,
            VerticalOffset = 18,
            PlacementTarget = editor.TextArea,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#252526")),
                BorderBrush = new SolidColorBrush(Color.Parse("#454545")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 4),
                Child = _text,
            },
        };
        editor.AddPopup(_popup);
        editor.PointerHover += OnPointerHover;
        editor.PointerHoverStopped += (_, _) => Close();
    }

    private async void OnPointerHover(object? sender, PointerEventArgs e)
    {
        Close();
        TextView textView = _editor.TextArea.TextView;
        TextViewPosition? position = textView.GetPositionFloor(e.GetPosition(textView) + textView.ScrollOffset);
        if (position is not { } p)
            return;
        int offset = _editor.Document.GetOffset(p.Location);

        string diagnostics = string.Join(
            Environment.NewLine,
            _features.DiagnosticsAt(offset).Select(d => $"{d.Id}: {d.Message}")
        );
        Show(diagnostics);

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
            diagnostics.Length == 0 ? info.Text : $"{info.Text}{Environment.NewLine}{Environment.NewLine}{diagnostics}"
        );
    }

    private void Show(string text)
    {
        if (text.Length == 0)
            return;
        _text.Text = text;
        _popup.IsOpen = true;
    }

    private void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _popup.IsOpen = false;
    }
}
