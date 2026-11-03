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
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(100),
    };
    private CancellationTokenSource? _cancellation;
    private Shown? _shown;

    /// <summary>An edit made the shown or requested help stale; it is asked again once typing pauses.</summary>
    private bool _stale;

    /// <summary>A trigger character's request, sent once the keystroke's own edits, such as a closing ')', are done.</summary>
    private SignatureHelpTrigger? _triggered;

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
        ApplyTheme(editor.EditorTheme);
        editor.EditorThemeChanged += (_, _) => ApplyTheme(editor.EditorTheme);
        editor.TextArea.TextEntered += OnTextEntered;
        editor.TextChanged += (_, _) => OnTextChanged();
        editor.DocumentChanged += (_, _) => Close();
        _refreshTimer.Tick += (_, _) => Refresh();
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public void Close()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        _refreshTimer.Stop();
        _stale = false;
        _triggered = null;
        _popup.Hide();
    }

    /// <summary>
    /// An edit cancels the request in flight, which tells the server so; help that was shown or requested is asked
    /// again when typing pauses. Trigger characters ask at once, right after this.
    /// </summary>
    private void OnTextChanged()
    {
        if (_cancellation is null && _shown is null)
            return;
        _cancellation?.Cancel();
        _cancellation = null;
        // A trigger character's request follows anyway, with the text as the keystroke left it.
        if (_triggered is not null)
            return;
        _stale = true;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void Refresh()
    {
        _refreshTimer.Stop();
        if (!_stale || _features.Service is not { } service)
            return;
        Request(
            service,
            new SignatureHelpTrigger(SignatureHelpTriggerKind.ContentChange, IsRetrigger: _shown is not null)
        );
    }

    private void ApplyTheme(EditorTheme theme)
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
        {
            // After the keystroke's own edits, such as the ')' that closes the '(': they would cancel the request.
            bool posted = _triggered is not null;
            _triggered = new SignatureHelpTrigger(SignatureHelpTriggerKind.TriggerCharacter, c, _shown is not null);
            _refreshTimer.Stop();
            if (!posted)
                Dispatcher.UIThread.Post(RequestTriggered, DispatcherPriority.Background);
        }
        else if (c == ')')
            Close();
    }

    private void RequestTriggered()
    {
        if (_triggered is not { } trigger)
            return;
        _triggered = null;
        if (_features.Service is { } service)
            Request(service, trigger);
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

    private async void Request(ILanguageService service, SignatureHelpTrigger trigger)
    {
        _refreshTimer.Stop();
        _stale = false;
        // The older request is no longer needed: cancelling it tells the server so.
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        (DocumentVersion version, ITextSourceVersion checkpoint) = _features.CreateSnapshot();
        int position = _editor.CaretOffset;
        long start = Stopwatch.GetTimestamp();
        SignatureHelp? help = await _features.RequestAsync(
            () => service.GetSignatureHelpAsync(version, position, trigger, cancellation.Token),
            cancellation.Token
        );
        // An edit since the request cancelled it; a response for an older version is dropped all the same.
        if (cancellation.IsCancellationRequested || _features.CreateSnapshot().Document.Version != version.Version)
            return;
        _cancellation = null;
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
                new Run($"{shown.Selected + 1}/{help.Signatures.Count}  ") { Foreground = _documentation.Foreground }
            );
        }
        string label = signature.Label;
        int written = 0;
        int searchFrom = 0;
        int activeParameter = signature.ActiveParameter ?? help.ActiveParameter;
        ParameterInfo? active = null;
        for (int i = 0; i < signature.Parameters.Count; i++)
        {
            ParameterInfo parameter = signature.Parameters[i];
            // The service's offsets, or else the label's next occurrence.
            int index =
                parameter.LabelRange?.Start ?? label.IndexOf(parameter.Label, searchFrom, StringComparison.Ordinal);
            int length = parameter.LabelRange?.Length ?? parameter.Label.Length;
            if (index < searchFrom || index + length > label.Length)
                break;
            searchFrom = index + length;
            if (i != activeParameter)
                continue;
            active = parameter;
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
