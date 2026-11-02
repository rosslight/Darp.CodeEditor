using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Shows the signatures of the call the caret is in, after typing '(' or ',', above the line. Up and Down switch
/// between overloads while the completion list is closed; leaving the argument list, ')' or Escape close it.
/// </summary>
internal sealed class SignatureHelpController
{
    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private readonly TextBlock _header = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _documentation = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _border;
    private readonly TextPopup _popup;
    private CancellationTokenSource? _cancellation;
    private Shown? _shown;

    public SignatureHelpController(CodeEditor editor, LanguageFeatures features)
    {
        _editor = editor;
        _features = features;
        _header.FontFamily = editor.FontFamily;
        _documentation.Margin = new Thickness(0, 4, 0, 0);
        _border = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4),
            MaxWidth = 640,
            Child = new StackPanel { Children = { _header, _documentation } },
        };
        _popup = new TextPopup(editor, _border, above: true);
        _popup.Closed += (_, _) => Detach();
        ApplyTheme(EditorTheme.Default);
        editor.TextArea.TextEntered += OnTextEntered;
        editor.DocumentChanged += (_, _) => Close();
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _popup.Hide();
    }

    public void ApplyTheme(EditorTheme theme)
    {
        _border.Background = theme.WidgetBackground;
        _border.BorderBrush = theme.WidgetBorder;
        _header.Foreground = theme.WidgetForeground;
        _documentation.Foreground = theme.WidgetSecondaryForeground;
        if (_shown is not null)
            Render();
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (_features.Service is not { } service || e.Text is not [char c])
            return;
        if (service.Capabilities.SignatureHelpTriggerCharacters.Contains(c))
            Request(service);
        else if (c == ')')
            Close();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // The completion list handles its keys first and marks them handled.
        if (_shown is not { } shown || e.KeyModifiers != KeyModifiers.None)
            return;
        int count = shown.Help.Signatures.Count;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Up when count > 1:
                shown.Selected = (shown.Selected + count - 1) % count;
                Render();
                break;
            case Key.Down when count > 1:
                shown.Selected = (shown.Selected + 1) % count;
                Render();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private async void Request(ILanguageService service)
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        (DocumentSnapshot snapshot, ITextSourceVersion checkpoint) = _features.CreateSnapshot();
        int position = _editor.CaretOffset;
        long start = Stopwatch.GetTimestamp();
        SignatureHelp? help = await _features.RequestAsync(
            () => service.GetSignatureHelpAsync(snapshot, position, cancellation.Token),
            cancellation.Token
        );
        if (cancellation.IsCancellationRequested)
            return;
        if (help is null || help.Signatures.Count == 0)
        {
            Close();
            return;
        }

        TextDocument document = _editor.Document;
        int rangeStart = checkpoint.MoveOffsetTo(
            document.Version,
            help.ApplicableRange.Start,
            AnchorMovementType.BeforeInsertion
        );
        int rangeEnd = checkpoint.MoveOffsetTo(
            document.Version,
            help.ApplicableRange.End,
            AnchorMovementType.AfterInsertion
        );
        int caret = _editor.CaretOffset;
        if (caret < rangeStart)
        {
            Close();
            return;
        }
        Detach();
        _shown = new Shown(
            help,
            Math.Clamp(help.ActiveSignature, 0, help.Signatures.Count - 1),
            CreateAnchor(document, rangeStart, AnchorMovementType.BeforeInsertion),
            CreateAnchor(document, Math.Max(rangeEnd, caret), AnchorMovementType.AfterInsertion)
        );
        Render();
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        _editor.TextArea.LostFocus += OnLostFocus;
        _popup.Show(rangeStart);
        if (!_popup.IsOpen)
            Detach();
        _editor.ReportTiming("signature-help", Stopwatch.GetElapsedTime(start), help.Signatures.Count);
    }

    private static TextAnchor CreateAnchor(TextDocument document, int offset, AnchorMovementType movement)
    {
        TextAnchor anchor = document.CreateAnchor(offset);
        anchor.MovementType = movement;
        anchor.SurviveDeletion = true;
        return anchor;
    }

    private void Detach()
    {
        if (_shown is null)
            return;
        _shown = null;
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _editor.TextArea.LostFocus -= OnLostFocus;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        int caret = _editor.CaretOffset;
        if (_shown is { } shown && (caret < shown.Start.Offset || caret > shown.End.Offset))
            Close();
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_editor.TextArea.IsFocused)
                    Close();
            },
            DispatcherPriority.Background
        );

    /// <summary>Shows the selected signature with its active parameter in bold, and its documentation.</summary>
    private void Render()
    {
        if (_shown is not { } shown)
            return;
        SignatureHelp help = shown.Help;
        SignatureInfo signature = help.Signatures[shown.Selected];
        InlineCollection inlines = _header.Inlines ??= [];
        inlines.Clear();
        if (help.Signatures.Count > 1)
        {
            inlines.Add(
                new Run($"{shown.Selected + 1}/{help.Signatures.Count}  ")
                {
                    Foreground = _documentation.Foreground,
                }
            );
        }
        string label = signature.Label;
        int written = 0;
        int searchFrom = 0;
        ParameterInfo? active = null;
        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            string parameter = signature.Parameters[i].Label;
            int index = label.IndexOf(parameter, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                break;
            searchFrom = index + parameter.Length;
            if (i != help.ActiveParameter)
                continue;
            active = signature.Parameters[i];
            inlines.Add(new Run(label[..index]));
            inlines.Add(new Run(label[index..searchFrom]) { FontWeight = FontWeight.Bold });
            written = searchFrom;
            break;
        }
        inlines.Add(new Run(label[written..]));

        string? documentation = (signature.Documentation, active?.Documentation) switch
        {
            (null, null) => null,
            ({ } text, null) => text,
            (null, { } parameter) => $"{active?.Label}: {parameter}",
            ({ } text, { } parameter) => $"{text}{Environment.NewLine}{active?.Label}: {parameter}",
        };
        _documentation.Text = documentation;
        _documentation.IsVisible = !string.IsNullOrEmpty(documentation);
    }

    private sealed class Shown(SignatureHelp help, int selected, TextAnchor start, TextAnchor end)
    {
        public SignatureHelp Help { get; } = help;
        public int Selected { get; set; } = selected;
        public TextAnchor Start { get; } = start;
        public TextAnchor End { get; } = end;
    }
}
