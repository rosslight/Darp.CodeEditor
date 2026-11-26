using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Signature help in one editor's view of a document: a card with one signature of the call around the caret at a time
/// (<see cref="SignatureView"/>), its parameter at the caret in bold, and its documentation, in the editor's popup in
/// the color theme's hover colors, above the caret's line, so that completion's list below it shows too. It is as wide as
/// its content, up to a hover's width, where its text wraps. While the call has several signatures, a column at its left
/// says which one shows, as <c>2/5</c>, between buttons that choose the one before and after, as Up and Down do.
/// </summary>
/// <remarks>
/// <para>
/// It opens on its command, and when the user types one of the server's trigger characters, such as <c>(</c>, and asks again at once on a
/// retrigger character, such as <c>,</c>, while it is open. While it is open, each edit and caret move restarts a pause
/// of 120 ms, VS Code's; once it passes, it asks again, telling the server the signature that shows. A newer request,
/// and every edit, cancels the request under way, and an answer about older text is dropped. Up and Down, while
/// completion's list is closed, and Alt+Up and Alt+Down choose the signature while there are several.
/// </para>
/// <para>
/// Choosing another signature keeps the card's left edge and its side of the line, and the column keeps to the edge
/// next to the line, which stays where it is: the buttons stay under the pointer while the card's size changes.
/// </para>
/// <para>
/// <see cref="Close"/> ends it, for every reason it ends: Escape, leaving the call, a click or drag that moves the caret,
/// when the server answers with none, the editor becoming read-only, the editor losing the keyboard focus, a server that
/// starts or stops serving the document, new colors, the caret's line scrolling out of view, and the editor no longer
/// showing the document.
/// </para>
/// </remarks>
internal sealed class SignatureHelpController : IEditorFeature
{
    /// <summary>The pause after an edit or a caret move before signature help is asked again: VS Code's.</summary>
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(120);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly CaretMoves _caretMoves;
    private readonly PopupScrollViewer _content = new();
    private readonly PopupButton _previous = new(up: true) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly PopupText _counter = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly PopupButton _next = new(up: false) { HorizontalAlignment = HorizontalAlignment.Center };

    /// <summary>The buttons and the count between them.</summary>
    private readonly PopupStack _choice;

    /// <summary>The column at the card's left that holds <see cref="_choice"/>, with a line at its right.</summary>
    private readonly PopupBox _column;
    private readonly PopupFrame _frame;
    private readonly EditorPopup _popup;
    private PopupColors _colors = PopupColors.Default;

    /// <summary>The signature help that shows; null while none does.</summary>
    private SignatureHelpInfo? _shown;

    /// <summary>Cancels the request under way; null for none.</summary>
    private CancellationTokenSource? _request;

    /// <summary>The pause before asking again; null while none runs.</summary>
    private ITimer? _pause;

    /// <summary>Cancels the reading of the documentation that is about to show.</summary>
    private CancellationTokenSource? _reading;

    public SignatureHelpController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        CaretMoves caretMoves
    )
    {
        _editor = editor;
        _caretMoves = caretMoves;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _choice = new PopupStack { Children = { _previous, _counter, _next } };
        _column = new PopupBox
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(4, 2),
            Child = _choice,
        };
        DockPanel.SetDock(_column, Dock.Left);
        _previous.Clicked += () => Cycle(forward: false);
        _next.Clicked += () => Cycle(forward: true);
        _frame = new PopupFrame
        {
            Name = "SignatureHelp",
            Child = new PopupDock { Children = { _column, _content } },
        };
        Paint();
        _popup = new EditorPopup(editor, _frame);
        // At the edge next to the line, which stays where it is as the card's height changes.
        _popup.PlacedChanged += (_, _) =>
            _choice.VerticalAlignment = _popup.Placed is { Above: false }
                ? VerticalAlignment.Top
                : VerticalAlignment.Bottom;
        _textArea.TextEntered += OnTextEntered;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _buffer.Changed += OnEdit;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _popup.LeftView += OnClosingChange;
    }

    /// <summary>The popup's colors, from the color theme once it is loaded. New colors end signature help.</summary>
    public PopupColors Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            Paint();
            Close();
        }
    }

    /// <summary>Whether signature help shows, waits for an answer, or waits to ask again.</summary>
    private bool IsActive => _shown is not null || _request is not null || _pause is not null;

    /// <summary>Whether signature help shows a call with several signatures, among which the keys choose.</summary>
    private bool HasSeveral => _popup.IsOpen && _shown is { Signatures.Count: > 1 };

    public void AddCommands(CommandTable commands)
    {
        commands.Add(
            CodeEditorCommands.TriggerParameterHints,
            () => Ask(SignatureHelpTriggerKind.Invoked, null),
            CommandNeeds.TextFocus | CommandNeeds.Writable,
            () =>
                _document.Features?.SignatureHelp is not null
                && _textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset)
        );
        commands.Add(CodeEditorCommands.ShowPrevParameterHint, () => Cycle(forward: false), applies: () => HasSeveral);
        commands.Add(CodeEditorCommands.ShowNextParameterHint, () => Cycle(forward: true), applies: () => HasSeveral);
    }

    /// <summary>
    /// Up and Down, from the editor's key handler after completion's list had them, while several signatures show: the
    /// signature before or after the active one.
    /// </summary>
    /// <returns>Whether the key was signature help's.</returns>
    public bool OnKeyDown(KeyEventArgs e)
    {
        if (!HasSeveral || e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down))
            return false;
        Cycle(forward: e.Key == Key.Down);
        return true;
    }

    /// <summary>
    /// Shows the signature after or before the one that shows, around the ends, in place; the server learns it when it
    /// is asked again.
    /// </summary>
    private void Cycle(bool forward)
    {
        if (_shown is not { } shown)
            return;
        int count = shown.Signatures.Count;
        Show(
            shown with
            {
                ActiveSignature = (shown.ActiveSignature + (forward ? 1 : count - 1)) % count,
            },
            inPlace: true
        );
    }

    /// <summary>Ends signature help: closes the popup, and cancels what is under way.</summary>
    /// <returns>Whether the popup showed.</returns>
    public bool Close()
    {
        bool wasOpen = _shown is not null;
        _shown = null;
        StopWaiting();
        _reading?.Cancel();
        _reading = null;
        _popup.Hide();
        return wasOpen;
    }

    public void Dispose()
    {
        Close();
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _buffer.Changed -= OnEdit;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _popup.LeftView -= OnClosingChange;
    }

    /// <summary>Asks on the server's trigger characters, and on its retrigger characters while active.</summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } text || _document.Features?.SignatureHelp is not { } provider)
            return;
        char typed = text[^1];
        if (provider.TriggerCharacters.Contains(typed) || (IsActive && provider.RetriggerCharacters.Contains(typed)))
            Ask(SignatureHelpTriggerKind.TriggerCharacter, typed);
    }

    /// <summary>While active, an edit, also one that leaves the caret where it was, restarts the pause.</summary>
    private void OnEdit(object? sender, EventArgs e)
    {
        if (IsActive)
            Wait();
    }

    /// <summary>A caret that the pointer moves ends signature help; any other move restarts the pause.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_caretMoves.Mover == CaretMover.Pointer)
            Close();
        else if (IsActive)
            Wait();
    }

    /// <summary>Cancels the request under way, and asks again once the pause has passed.</summary>
    private void Wait()
    {
        StopWaiting();
        ITimer? pause = null;
        pause = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_pause == pause)
                        Ask(SignatureHelpTriggerKind.ContentChange, null);
                }),
            null,
            s_pause,
            Timeout.InfiniteTimeSpan
        );
        _pause = pause;
    }

    private void StopWaiting()
    {
        _pause?.Dispose();
        _pause = null;
        _request?.Cancel();
        _request = null;
    }

    private void Ask(int kind, char? character)
    {
        StopWaiting();
        // Nothing to help with where the user cannot type.
        if (!_textArea.ReadOnlySectionProvider.CanInsert(_textArea.Caret.Offset))
        {
            Close();
            return;
        }
        var cancellation = new CancellationTokenSource();
        _request = cancellation;
        AskAsync(
            _document.CurrentSnapshot,
            _textArea.Caret.Offset,
            new SignatureHelpTrigger(kind, character, _shown),
            cancellation
        );
    }

    /// <summary>Shows the server's answer, unless it is outdated; closes when the server has none there.</summary>
    private async void AskAsync(
        TextSnapshot snapshot,
        int caret,
        SignatureHelpTrigger trigger,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            SignatureHelpInfo? help = await _document.SignatureHelpAsync(snapshot, caret, trigger, cancellation.Token);
            if (_request != cancellation)
                return;
            _request = null;
            // Changed elsewhere meanwhile, as in another editor: the answer is about older text.
            if (snapshot != _document.CurrentSnapshot)
            {
                Wait();
                return;
            }
            if (help is null)
                Close();
            else
                Show(help);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Signature help failed: {Exception}", e);
        }
    }

    /// <summary>
    /// Shows <paramref name="help"/>'s active signature above the caret, once its documentation is read off the UI
    /// thread, as the hover reads its Markdown: as much as a hover shows, the signature first, so that a server's long
    /// signatures or documentation cost the UI thread no more than the largest hover.
    /// </summary>
    /// <param name="help">The signature help.</param>
    /// <param name="inPlace">Whether the card keeps its place, as when the user chose another signature.</param>
    private async void Show(SignatureHelpInfo help, bool inPlace = false)
    {
        _shown = help;
        _reading?.Cancel();
        var reading = new CancellationTokenSource();
        _reading = reading;
        SignatureInfo signature = help.Signatures[help.ActiveSignature];
        HoverText? parameter =
            signature.ActiveParameter >= 0 ? signature.Parameters[signature.ActiveParameter].Documentation : null;
        TextMateGrammar? grammar = _highlighter.Grammar;
        var budget = new HoverBudget();
        string label = budget.Take(signature.Label) ?? "";
        try
        {
            IReadOnlyList<HoverBlock> documentation =
                parameter is null && signature.Documentation is null
                    ? []
                    : await Task.Run(
                        // Apart, without the line between a hover's parts.
                        () => (IReadOnlyList<HoverBlock>)[.. Read(parameter), .. Read(signature.Documentation)],
                        reading.Token
                    );
            if (_reading != reading)
                return;
            var look = new CodeLook(_editor.FontFamily, _editor.FontSize, _colors);
            _frame.MaxWidth = PopupFrame.MaxTextWidth(_editor.Bounds.Width);
            _content.Content = SignatureView.Create(signature, label, documentation, look);
            _content.Offset = default;
            _column.IsVisible = help.Signatures.Count > 1;
            // As wide for each signature, in the editor's font, so that the buttons stay where they are.
            string count = help.Signatures.Count.ToString(CultureInfo.InvariantCulture);
            string number = (help.ActiveSignature + 1).ToString(CultureInfo.InvariantCulture).PadLeft(count.Length);
            (_counter.Text, _counter.FontFamily, _counter.FontSize) = (
                $"{number}/{count}",
                look.FontFamily,
                look.FontSize
            );
            _popup.Show(
                _textView,
                () =>
                    TextViewBoxes.CharacterAt(
                        _textView,
                        Math.Min(_textArea.Caret.Offset, _textArea.Document.TextLength)
                    ),
                inPlace
            );
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing signature help failed: {Exception}", e);
        }

        IReadOnlyList<HoverBlock> Read(HoverText? text) =>
            text is { } part ? HoverBlocks.Of(new HoverInfo([part], null), grammar, budget, reading.Token) : [];
    }

    private void Paint()
    {
        _frame.PaintDocs(_colors);
        _column.BorderBrush = _colors.HoverBorder;
        _previous.Paint(_colors.HoverForeground, _colors.ListHoverBackground);
        _next.Paint(_colors.HoverForeground, _colors.ListHoverBackground);
    }

    private void OnClosingChange(object? sender, EventArgs e) => Close();
}
