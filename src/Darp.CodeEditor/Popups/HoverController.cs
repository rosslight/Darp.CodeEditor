using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
/// The hover of one editor's view of a document, as VS Code's (<c>editor.action.showHover</c>,
/// <c>editor.hover.delay</c>): resting the pointer over text shows the messages of the diagnostics there, above the
/// server's hover, in the editor's own popup (<see cref="HoverPopup"/>).
/// </summary>
/// <remarks>
/// <para>
/// The pointer rests on a character for <see cref="HoverOptions.Delay"/> on the document's clock; moving to another
/// character starts the wait again. Halfway through, the server is asked; once the delay is over, the diagnostics there
/// show, with the server's hover if it answered by then, which otherwise joins them when it answers. While it has not
/// answered by three times the delay, "Loading…" shows below them. Its Markdown is read, and its code blocks
/// highlighted, off the UI thread. Asked for by key, the hover starts at once, without "Loading…".
/// </para>
/// <para>
/// The hovered text is the word under the pointer, and the range that the server's hover names. The popup stays while
/// the pointer is over it or over the popup, and a moment after it left, so that it can cross other text on its way in.
/// Once the user clicked into the popup, as to select its text, or put the keyboard into it, it is held: it stays
/// wherever the pointer goes, and resting on other text shows no other hover, as in VS Code.
/// </para>
/// <para>
/// <see cref="Close"/> ends the hover, for every reason it ends: the pointer leaves both, unless it is held; a click in
/// the editor, or outside a held popup; a key in the text that the view leaves to it (<see cref="CloseOnKey"/>),
/// unless it is held; typing, scrolling, Escape and the window losing the focus; the keyboard leaving the editor and
/// its popups; a server that starts or stops serving the document, whose information the hover showed; new colors,
/// which its code was drawn in; and the editor no longer showing the document. Ending it cancels the server's request
/// and the reading of its answer if they are still under way, and drops what they would show. While a button is down,
/// as when a drag selects, and after a click until the pointer moves on, no hover starts, as in VS Code.
/// </para>
/// </remarks>
internal sealed class HoverController : IEditorFeature
{
    /// <summary>VS Code's delay before a hover closes once the pointer left it, so that it can reach the popup.</summary>
    private static readonly TimeSpan s_hidingDelay = TimeSpan.FromMilliseconds(300);

    private readonly TemplatedControl _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SquiggleLayer _squiggles;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly Func<CodeEditorOptions> _options;
    private readonly HoverPopup _popup;
    private PopupColors _colors = PopupColors.Default;

    /// <summary>The text hovered now, until the hover ends; null for none. Until the popup opens, it waits.</summary>
    private Hovered? _hovered;

    /// <summary>Where the pointer moved last over the text, without a button down; null otherwise.</summary>
    private Point? _pointer;

    /// <summary>The delay before the hover closes, once the pointer left it; null while none runs.</summary>
    private ITimer? _hiding;

    /// <summary>
    /// Where the last click in the editor pressed or released its button, in the text view, until the pointer moves
    /// away from there; null otherwise. Meanwhile no hover starts.
    /// </summary>
    private Point? _clickedAt;

    public HoverController(
        TemplatedControl editor,
        TextArea textArea,
        CodeDocument document,
        SquiggleLayer squiggles,
        BackgroundTextMateHighlighter highlighter,
        Func<CodeEditorOptions> options
    )
    {
        _editor = editor;
        _textArea = textArea;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _squiggles = squiggles;
        _highlighter = highlighter;
        _options = options;
        _popup = new HoverPopup(editor, textArea, () => Close());
        _textView.PointerMoved += OnPointerMoved;
        _textView.PointerExited += OnPointerExited;
        _textView.ScrollOffsetChanged += OnClosingChange;
        _buffer.Changed += OnClosingChange;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _popup.Root.PointerExited += OnPointerExited;
        _popup.Root.PointerEntered += OnPointerEnteredPopup;
        // Also where the editor's parts handle the press, as the text does to select. The popup is not in the editor.
        _editor.AddHandler(InputElement.PointerPressedEvent, OnEditorPressed, RoutingStrategies.Tunnel, true);
        _editor.AddHandler(InputElement.PointerReleasedEvent, OnEditorReleased, RoutingStrategies.Tunnel, true);
    }

    /// <summary>The popup's colors, from the color theme once it is loaded. New colors end the hover.</summary>
    public PopupColors Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            Close();
        }
    }

    /// <summary>Whether the server's answer to the hover that waits or shows is in, its Markdown read; for tests.</summary>
    internal bool IsAnswered => _hovered is { IsAnswered: true };

    public void AddCommands(CommandTable commands) => commands.Add(CodeEditorCommands.ShowHover, ShowAtCaret);

    /// <summary>
    /// Ends the current hover: closes the popup, cancels the server's request and the reading of its answer if they are
    /// still under way, and drops what they would show. The keyboard goes back to the text from the popup.
    /// </summary>
    /// <returns>Whether a popup was open.</returns>
    public bool Close()
    {
        StayOpen();
        bool wasOpen = _popup.IsOpen;
        _hovered?.Stop();
        _hovered = null;
        _popup.Hide();
        return wasOpen;
    }

    /// <summary>
    /// Ends the hover for a key in the text, unless it is held, as VS Code does: the view leaves out the keys that keep
    /// it, those that start a key sequence or show the hover.
    /// </summary>
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
        _popup.Root.PointerEntered -= OnPointerEnteredPopup;
        _editor.RemoveHandler(InputElement.PointerPressedEvent, OnEditorPressed);
        _editor.RemoveHandler(InputElement.PointerReleasedEvent, OnEditorReleased);
    }

    /// <summary>
    /// Shows the hover at the caret, the diagnostics there and the server's hover; when a hover shows already, also one
    /// the pointer opened, the keyboard goes into it, as in VS Code, and it is held.
    /// </summary>
    private void ShowAtCaret()
    {
        if (_popup.IsOpen)
        {
            StayOpen();
            _popup.Hold();
            _popup.TakeKeyboard();
            return;
        }
        int caret = _textArea.Caret.Offset;
        DocumentLine line = _buffer.GetLineByOffset(caret);
        // Right after a word, as the caret is when typing ends, the hover is the word's.
        int offset =
            caret > line.Offset
            && (caret == line.EndOffset || !WordCharacters.IsWordCharacter(_buffer.GetCharAt(caret)))
            && WordCharacters.IsWordCharacter(_buffer.GetCharAt(caret - 1))
                ? caret - 1
                : caret;
        Hovered hovered = Begin(offset, fromKeyboard: true);
        Ask(hovered);
        Due(hovered);
    }

    /// <summary>Ends the hover there is, and makes the hover of <paramref name="offset"/> the current one.</summary>
    private Hovered Begin(int offset, bool fromKeyboard)
    {
        Close();
        _popup.Reset();
        var hovered = Hovered.At(_buffer, offset, _document.CurrentSnapshot, fromKeyboard);
        _hovered = hovered;
        return hovered;
    }

    /// <summary>
    /// Starts the hover of the pointer at <paramref name="offset"/>, as VS Code's hover operation starts: the server is
    /// asked halfway through the delay, what is known shows once it is over, and "Loading…" joins it when the server has
    /// not answered by three times the delay.
    /// </summary>
    private void StartDelayed(int offset)
    {
        Hovered hovered = Begin(offset, fromKeyboard: false);
        TimeSpan delay = _options().Hover.Delay;
        hovered.Timers = [After(hovered, delay / 2, Ask), After(hovered, delay, Due), After(hovered, 3 * delay, Late)];
    }

    /// <summary>A timer that runs <paramref name="action"/> on the UI thread after <paramref name="time"/>, while <paramref name="hovered"/> is the current hover.</summary>
    private ITimer After(Hovered hovered, TimeSpan time, Action<Hovered> action) =>
        _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_hovered == hovered)
                        action(hovered);
                }),
            null,
            time,
            Timeout.InfiniteTimeSpan
        );

    /// <summary>The delay is over: the diagnostics there now, and what else is known, show.</summary>
    private void Due(Hovered hovered)
    {
        hovered.IsDue = true;
        // Each diagnostic takes a line at least, so a hover never shows more of them than a part has lines.
        hovered.Messages = HoverBlocks.Of(_squiggles.At(hovered.Offset, HoverParts.MaxLines));
        Render(hovered);
    }

    /// <summary>The server has not answered by three times the delay: "Loading…" shows until it does.</summary>
    private void Late(Hovered hovered)
    {
        if (hovered.IsAnswered)
            return;
        hovered.IsLate = true;
        Render(hovered);
    }

    /// <summary>
    /// Asks the server for its hover, and reads its Markdown, off the UI thread; it shows once the delay is over, unless
    /// the hover ended meanwhile, as by an edit or another document. Failures of reading it go to the log.
    /// </summary>
    private async void Ask(Hovered hovered)
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
            if (hover is not null && _hovered == hovered)
            {
                TextMateGrammar? grammar = _highlighter.Grammar;
                IReadOnlyList<HoverBlock> blocks = await Task.Run(
                    () => HoverBlocks.Of(hover, grammar, cancellation),
                    cancellation
                );
                hovered.Answered(blocks, hover.Range);
            }
        }
        // The hover ended while its answer was read.
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing the hover failed: {Exception}", e);
        }
        if (_hovered != hovered)
            return;
        hovered.IsAnswered = true;
        Render(hovered);
    }

    /// <summary>
    /// Shows what is known of <paramref name="hovered"/> once its delay is over (<see cref="Hovered.Blocks"/>); with
    /// nothing to show, no popup shows.
    /// </summary>
    private void Render(Hovered hovered)
    {
        IEnumerable<HoverBlock> blocks = hovered.Blocks();
        if (!blocks.Any())
        {
            StayOpen();
            _popup.Hide();
            return;
        }
        _popup.Show(blocks, _colors, hovered.Word);
    }

    /// <summary>
    /// While a hover shows, the pointer over other text closes it after <see cref="s_hidingDelay"/>, unless the pointer
    /// reaches it or comes back meanwhile. While none shows, the pointer on a character starts its hover, and on another
    /// character starts again, as in VS Code; not while a button is down, nor after a click until the pointer moves on.
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point point = e.GetPosition(_textView);
        PointerPointProperties buttons = e.GetCurrentPoint(_textView).Properties;
        bool pressed = buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed;
        _pointer = pressed ? null : point;
        if (_clickedAt is { } clicked && point != clicked)
            _clickedAt = null;
        // A held hover stays, as does one asked for by key, as in VS Code.
        if (_popup.IsHeld || (_hovered is { FromKeyboard: true } && _popup.IsOpen))
            return;
        if (_popup.IsOpen && _hovered is { } shown)
        {
            if (!pressed && OffsetAt(point) is { } offset && shown.IsOver(offset))
                StayOpen();
            else
                HideSoon();
            return;
        }
        RestAt(_pointer);
    }

    /// <summary>
    /// The pointer is at <paramref name="point"/> of the text view while no hover shows: on the character of the hover
    /// that waits, the wait goes on; on another character, its hover starts; elsewhere, or for null, none waits.
    /// </summary>
    private void RestAt(Point? point)
    {
        if (_clickedAt is not null || point is not { } at || OffsetAt(at) is not { } offset)
        {
            if (_hovered is not null && !_popup.IsOpen)
                Close();
            return;
        }
        if (_hovered is { FromKeyboard: false } waiting && waiting.Offset == offset)
            return;
        StartDelayed(offset);
    }

    /// <summary>
    /// The pointer left the text or the popup: a hover that shows closes after <see cref="s_hidingDelay"/> unless the
    /// pointer is over it by then, and one that waits ends. Posted, as the pointer enters the other only after it left
    /// the one.
    /// </summary>
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender == _textView)
            _pointer = null;
        _editor.Dispatcher.Post(() =>
        {
            if (_hovered is not { FromKeyboard: false } || _popup.IsHeld || _popup.Root.IsPointerOver)
                return;
            if (_popup.IsOpen)
                HideSoon();
            else
                Close();
        });
    }

    private void OnPointerEnteredPopup(object? sender, PointerEventArgs e) => StayOpen();

    /// <summary>A click in the editor ends the hover, as in VS Code, also one on the hovered word.</summary>
    private void OnEditorPressed(object? sender, PointerPressedEventArgs e)
    {
        _clickedAt = e.GetPosition(_textView);
        Close();
    }

    private void OnEditorReleased(object? sender, PointerReleasedEventArgs e) => _clickedAt = e.GetPosition(_textView);

    /// <summary>Closes the hover after the hiding delay, unless something keeps it open meanwhile.</summary>
    private void HideSoon()
    {
        if (_hiding is not null || _hovered is not { } hovered)
            return;
        ITimer? hiding = null;
        hiding = _document.TimeProvider.CreateTimer(
            _ =>
                _editor.Dispatcher.Post(() =>
                {
                    if (_hiding != hiding || _hovered != hovered)
                        return;
                    if (_popup.IsHeld || _popup.Root.IsPointerOver || _popup.IsDragging)
                    {
                        StayOpen();
                        return;
                    }
                    // As VS Code reacts to where the pointer went once the delay is over.
                    Close();
                    RestAt(_textView.IsPointerOver ? _pointer : null);
                }),
            null,
            s_hidingDelay,
            Timeout.InfiniteTimeSpan
        );
        _hiding = hiding;
    }

    private void StayOpen()
    {
        _hiding?.Dispose();
        _hiding = null;
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

    /// <inheritdoc cref="Hovered.MaxWordReach"/>
    internal const int MaxWordReach = Hovered.MaxWordReach;
}
