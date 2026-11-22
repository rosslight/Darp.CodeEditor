using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The hover of one editor's view of a document: resting the pointer over text shows the messages of the diagnostics
/// there, above the server's hover, in the editor's popup (<see cref="HoverPopup"/>).
/// </summary>
/// <remarks>
/// <para>
/// The pointer rests on a character for 300 ms on the document's clock; moving to another character starts the wait
/// again. Once it is over, the diagnostics show and the server is asked; its hover joins them when it answers. Its
/// Markdown is read, and its code blocks highlighted, off the UI thread. <c>editor.action.showHover</c> does the same at
/// the caret at once; pressed again, it puts the keyboard into the popup.
/// </para>
/// <para>
/// The hovered text is the word under the pointer, and the range that the server's hover names. The popup stays while
/// the pointer is over it or over the popup. Clicked into, or with the keyboard put into it, it is held: it stays
/// wherever the pointer goes, until Escape or a click outside it.
/// </para>
/// <para>
/// <see cref="Close"/> ends the hover, for every reason it ends: the pointer leaves both, also for another word under
/// the same diagnostic, unless it is held; a click outside the popup; a default key in the text, unless it is held
/// (<see cref="CloseOnKey"/>); for a hover asked for by key, the caret moving; typing, scrolling and Escape; the keyboard
/// leaving the editor and its popups; a server that starts or stops serving the document, whose information the hover
/// showed; new colors, which its code was drawn in; and the editor no longer showing the document. Ending it cancels
/// the server's request and the reading of its answer if they are still under way, and drops what they would show. No
/// hover starts while a button is down, or where a click left the pointer.
/// </para>
/// </remarks>
internal sealed class HoverController : IEditorFeature
{
    /// <summary>How long the pointer rests on a character before its hover shows.</summary>
    private static readonly TimeSpan s_delay = TimeSpan.FromMilliseconds(300);

    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SquiggleLayer _squiggles;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly HoverPopup _popup;

    /// <summary>The window whose clicks end the hover; null outside one.</summary>
    private readonly TopLevel? _window;

    private PopupColors _colors = PopupColors.Default;

    /// <summary>The text hovered now, until the hover ends; null for none. Until its delay is over, it waits.</summary>
    private Hovered? _hovered;

    /// <summary>Where the pointer moved last over the text: a move that stays there, as after a click, starts nothing.</summary>
    private Point? _pointer;

    public HoverController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        SquiggleLayer squiggles,
        BackgroundTextMateHighlighter highlighter
    )
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _squiggles = squiggles;
        _highlighter = highlighter;
        _popup = new HoverPopup(editor, textArea, () => Close());
        _popup.Paint(_colors);
        _window = TopLevel.GetTopLevel(editor);
        _textView.PointerMoved += OnPointerMoved;
        _textView.PointerExited += OnPointerExited;
        _textView.ScrollOffsetChanged += OnClosingChange;
        _buffer.Changed += OnClosingChange;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _popup.Root.PointerExited += OnPointerExited;
        // Also where a part handles the press, as the text does to select.
        _window?.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
    }

    /// <summary>The popup's colors, from the color theme once it is loaded. New colors end the hover.</summary>
    public PopupColors Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            _popup.Paint(value);
            Close();
        }
    }

    public void AddCommands(CommandTable commands) => commands.Add(CodeEditorCommands.ShowHover, ShowAtCaret);

    /// <summary>
    /// Ends the current hover: closes the popup, cancels the server's request and the reading of its answer if they are
    /// still under way, and drops what they would show.
    /// </summary>
    /// <returns>Whether a popup was open.</returns>
    public bool Close()
    {
        bool wasOpen = _popup.IsOpen;
        _hovered?.Stop();
        _hovered = null;
        _popup.Hide();
        return wasOpen;
    }

    /// <summary>Ends the hover for a default key in the text, unless it is held.</summary>
    public void CloseOnKey()
    {
        if (!_popup.IsHeld)
            Close();
    }

    public void Dispose()
    {
        Close();
        _textView.PointerMoved -= OnPointerMoved;
        _textView.PointerExited -= OnPointerExited;
        _textView.ScrollOffsetChanged -= OnClosingChange;
        _buffer.Changed -= OnClosingChange;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _popup.Root.PointerExited -= OnPointerExited;
        _window?.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
    }

    /// <summary>Shows the hover at the caret; when one shows already, the keyboard goes into it.</summary>
    private void ShowAtCaret()
    {
        if (_popup.IsOpen)
        {
            _popup.TakeKeyboard();
            return;
        }
        int caret = _textArea.Caret.Offset;
        // Right after a word, as the caret is when typing ends, the hover is the word's.
        int offset = WordRules.WordAt(_buffer, caret) is { Length: > 0 } word && word.End == caret ? caret - 1 : caret;
        Due(Begin(offset, fromKeyboard: true));
    }

    /// <summary>Ends the hover there is, and makes the hover of <paramref name="offset"/> the current one.</summary>
    private Hovered Begin(int offset, bool fromKeyboard)
    {
        Close();
        var hovered = Hovered.At(_buffer, offset, _document.CurrentSnapshot, fromKeyboard);
        _hovered = hovered;
        return hovered;
    }

    /// <summary>The pointer arrived on a character: its hover shows once it has rested there for the delay.</summary>
    private void StartDelayed(int offset)
    {
        Hovered hovered = Begin(offset, fromKeyboard: false);
        hovered.Delay = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_hovered == hovered)
                        Due(hovered);
                }),
            null,
            s_delay,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>The delay is over: the diagnostics there show, and the server is asked for its hover.</summary>
    private void Due(Hovered hovered)
    {
        hovered.IsDue = true;
        // Each diagnostic takes a line at least, so a hover never shows more of them than it has lines.
        hovered.Messages = HoverBlocks.Of(_squiggles.At(hovered.Offset, HoverBudget.MaxLines), hovered.Budget);
        if (hovered.Messages.Count > 0)
            _popup.Show(hovered.Messages, Look(), hovered.Word);
        ShowServerHover(hovered);
    }

    /// <summary>
    /// Shows the server's hover below the messages, once it answered and its Markdown is read, unless the hover ended
    /// meanwhile. Failures of reading it go to the log.
    /// </summary>
    private async void ShowServerHover(Hovered hovered)
    {
        CancellationToken cancellation = hovered.Cancellation.Token;
        try
        {
            TextLocation location = _buffer.GetLocation(hovered.Offset);
            HoverInfo? hover = await _document.HoverAsync(
                hovered.Snapshot,
                new TextPosition(location.Line, location.Column),
                cancellation
            );
            if (hover is null || _hovered != hovered)
                return;
            TextMateGrammar? grammar = _highlighter.Grammar;
            IReadOnlyList<HoverBlock> blocks = await Task.Run(
                () => HoverBlocks.Of(hover, grammar, hovered.Budget, cancellation),
                cancellation
            );
            if (_hovered != hovered || blocks.Count == 0)
                return;
            if (hover.Range is { } range)
                hovered.Range = Union(hovered.Range, range);
            if (hovered.Messages.Count > 0)
                _popup.Append([new HoverRule(), .. blocks], Look());
            else
                _popup.Show(blocks, Look(), hovered.Word);
        }
        // The hover ended while its answer was read.
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing the hover failed: {Exception}", e);
        }
    }

    private CodeLook Look() => new(_editor.FontFamily, _editor.FontSize, _colors);

    /// <summary>Over other text, the hover ends, and the wait for that text's hover starts.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point point = e.GetPosition(_textView);
        if (point == _pointer)
            return;
        _pointer = point;
        if (_popup.IsHeld || (_hovered is { FromKeyboard: true } && _popup.IsOpen))
            return;
        PointerPointProperties buttons = e.GetCurrentPoint(_textView).Properties;
        bool pressed = buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed;
        int? offset = pressed ? null : OffsetAt(point);
        if (_hovered is { } hovered && offset is { } at && (hovered.IsDue ? hovered.IsOver(at) : hovered.Offset == at))
            return;
        Close();
        if (offset is { } next)
            StartDelayed(next);
    }

    /// <summary>
    /// Ends the hover once the pointer has left both the text and the popup, unless it is held or was asked for by key:
    /// posted, as the pointer enters the other only after it left the one.
    /// </summary>
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender == _textView)
            _pointer = null;
        _editor.Dispatcher.Post(() =>
        {
            if (
                !_popup.IsHeld
                && _hovered is { FromKeyboard: false }
                && !_textView.IsPointerOver
                && !_popup.Root.IsPointerOver
            )
                Close();
        });
    }

    /// <summary>A click in the popup holds it; anywhere else in the window, it ends the hover.</summary>
    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && _popup.Root.IsVisualAncestorOf(source))
            _popup.Hold();
        else
            Close();
    }

    /// <summary>A hover asked for by key closes when the caret moves.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_hovered is { FromKeyboard: true })
            Close();
    }

    private void OnClosingChange(object? sender, EventArgs e) => Close();

    /// <summary>
    /// The offset of the character under <paramref name="point"/>, in the shown part of its line, or of a line's end
    /// within a space's width after it, where an empty diagnostic there is drawn; null elsewhere, as over the "..." of a
    /// long line's hidden tail, which shows no text.
    /// </summary>
    private int? OffsetAt(Point point)
    {
        if (_textView.GetPositionFloor(point + _textView.ScrollOffset) is not { } position)
            return null;
        DocumentLine line = _buffer.GetLineByNumber(position.Line);
        int offset = _buffer.GetOffset(position.Location);
        if (offset < VisibleText.End(line))
            return offset;
        if (VisibleText.IsCollapsed(line))
            return null;
        Rect end = TextViewBoxes.CharacterAt(_textView, line.EndOffset);
        return point.X < end.Right ? line.EndOffset : null;
    }

    private static TextRange Union(TextRange a, TextRange b) =>
        TextRange.FromBounds(Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));
}
