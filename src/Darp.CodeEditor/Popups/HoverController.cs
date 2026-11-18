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
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The hover of one editor's view of a document: resting the pointer over text shows the messages of the diagnostics
/// there, above the server's hover, in a popup that looks like the app's tooltips.
/// </summary>
/// <remarks>
/// <para>
/// The hover's timing is VS Code's: the pointer rests on a character for <see cref="CodeEditor.HoverDelay"/>, 300 ms by
/// default; moving to another character starts the wait again. Halfway through, the server is asked for its hover; once
/// the delay is over, the diagnostics there show, with the server's hover if it answered by then, and otherwise it
/// joins them when it answers. While it has not answered by three times the delay, "Loading…" shows below them. Its
/// Markdown is read, and its code blocks highlighted, off the UI thread. Asked for by key, the hover starts at once,
/// without "Loading…".
/// </para>
/// <para>
/// The hovered text is the word under the pointer, and the range that the server's hover names. The popup stays while
/// the pointer is over it or over the popup, so that its links can be clicked and it can be scrolled. Once the user
/// clicked into the popup, as to select its text, or put the keyboard into it (<see cref="ShowAtCaret"/>), it stays
/// wherever the pointer goes, also while a drag crosses its edge, and resting the pointer on other text shows no other
/// hover, as in VS Code.
/// </para>
/// <para>
/// <see cref="Close"/> ends the hover, for every reason it ends: the pointer leaves both, also for another word under
/// the same diagnostic, unless the user clicked into the popup; then a click outside it, or the window losing the
/// focus; a click in the editor, and a key in the text, as the editor decides (<see cref="CloseOnKey"/>), unless the
/// user clicked into the popup; typing, scrolling and Escape; the editor losing the keyboard focus; a server that
/// starts or stops serving the document, whose information the hover showed, while a hover that waits starts again;
/// new colors, which its code was drawn in; and the editor no longer showing the document. Ending it cancels the
/// server's request and the reading of its answer if they are still under way, and drops what they would show, also
/// before any popup opened. While a button is down, as when a drag selects, and after a click in the editor until the
/// pointer moves on, no hover starts, as in VS Code.
/// </para>
/// </remarks>
internal sealed class HoverController : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SquiggleLayer _squiggles;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly PopupScroller _content = new();
    private readonly PopupFrame _frame;
    private readonly EditorPopup _popup;
    private TextAreaColors? _colors;

    /// <summary>VS Code's delay before a hover closes once the pointer left it, so that it can reach the popup.</summary>
    private static readonly TimeSpan s_hidingDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>The narrowest the hover's most width gets: wider in wider editors.</summary>
    private const double MinMaxWidth = 500;

    /// <summary>The lowest the hover's most height gets: higher in higher editors.</summary>
    private const double MinMaxHeight = 250;

    /// <summary>The smallest the user resizes the hover to.</summary>
    private const double MinResizedWidth = 150;

    private const double MinResizedHeight = 40;

    /// <summary>Whether the user resized the hover that shows.</summary>
    private bool _resized;

    /// <summary>
    /// The text hovered now, from the pointer's arrival on a character, or the key that asked for it, until the hover
    /// ends; null for none. Until the popup opens, it waits: for its delay, or for something to show.
    /// </summary>
    private Hovered? _hovered;

    /// <summary>
    /// Where the pointer moved last over the text view, without a button down; null while it is elsewhere or a button
    /// is down.
    /// </summary>
    private Point? _pointer;

    /// <summary>The delay before the hover closes, once the pointer left it; null while none runs.</summary>
    private ITimer? _hiding;

    /// <summary>
    /// The window whose clicks and focus loss a hover that the user clicked into watches, as they end it; null while
    /// none does.
    /// </summary>
    private TopLevel? _watched;

    /// <summary>
    /// Where the last click in the editor pressed or released its button, in the text view, until the pointer moves
    /// away from there; null otherwise. Meanwhile no hover starts: AvaloniaEdit's would start again where the pointer
    /// rests after the click, and VS Code's only starts as the mouse moves.
    /// </summary>
    private Point? _clickedAt;

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
        _frame = new PopupFrame
        {
            Name = "Hover",
            Child = _content,
            Padding = new Thickness(8, 4, 4, 4),
        };
        // Within the editor where it fits, rather than over the app's toolbar.
        _popup = new EditorPopup(editor, _frame) { PrefersEditorBounds = true };
        // Resizable by its edges away from the word, for this hover only, as in VS Code.
        _frame.ResizableEdges = () =>
            PopupFrame.Edges.Right
            | (_popup.LastPlacement?.Above == true ? PopupFrame.Edges.Top : PopupFrame.Edges.Bottom);
        _frame.Resized += (size, maxHeight) =>
        {
            _resized = true;
            _frame.Width = _frame.MaxWidth = Math.Max(MinResizedWidth, size.Width);
            _frame.MaxHeight = Math.Max(MinResizedHeight, maxHeight);
        };
        _textView.PointerMoved += OnPointerMoved;
        _textView.PointerExited += OnPointerExited;
        _textView.ScrollOffsetChanged += OnClosingChange;
        _buffer.Changed += OnClosingChange;
        _document.LanguageFeaturesChanged += OnLanguageFeaturesChanged;
        _popup.Root.PointerExited += OnPointerExited;
        _popup.Root.PointerEntered += OnPointerEnteredPopup;
        // Also when its text handles the press, to select.
        _popup.Root.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressedInPopup,
            RoutingStrategies.Bubble,
            handledEventsToo: true
        );
        _popup.Escaped += OnClosingChange;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        // Also where the editor's parts handle the press, as the text does to select. The popup is not in the editor.
        _editor.AddHandler(
            InputElement.PointerPressedEvent,
            OnEditorPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        _editor.AddHandler(
            InputElement.PointerReleasedEvent,
            OnEditorPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
    }

    /// <summary>The colors of the color theme, for code in the popup, once it is loaded. New colors end the hover.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            if (value == _colors)
                return;
            _colors = value;
            Close();
        }
    }

    /// <summary>
    /// Ends the current hover: closes the popup, cancels the server's request and the reading of its answer if they are
    /// still under way, and drops what they would show.
    /// </summary>
    /// <returns>Whether a popup was open.</returns>
    public bool Close()
    {
        StayOpen();
        Unwatch();
        bool wasOpen = _popup.IsOpen;
        _hovered?.Stop();
        _hovered = null;
        if (_popup.IsFocusWithin)
            _editor.FocusText();
        _content.Focusable = false;
        _popup.Hide();
        return wasOpen;
    }

    /// <summary>
    /// Ends the hover for a key in the text, unless the user clicked into it, as VS Code does: the editor leaves out
    /// the keys that keep it, such as those that start a key sequence or show the hover.
    /// </summary>
    /// <returns>Whether a popup was open.</returns>
    public bool CloseOnKey() => _hovered is not { IsHeld: true } && Close();

    /// <summary>
    /// Shows the hover at <paramref name="offset"/> as <see cref="ShowAtCaret"/> does at the caret, at the offset as it
    /// is, also right after a word: where a diagnostic starts, for going to it.
    /// </summary>
    public void ShowAt(int offset) => Start(offset, fromKeyboard: true);

    public void Dispose()
    {
        Close();
        _textView.PointerMoved -= OnPointerMoved;
        _textView.PointerExited -= OnPointerExited;
        _textView.ScrollOffsetChanged -= OnClosingChange;
        _buffer.Changed -= OnClosingChange;
        _document.LanguageFeaturesChanged -= OnLanguageFeaturesChanged;
        _popup.Root.PointerExited -= OnPointerExited;
        _popup.Root.PointerEntered -= OnPointerEnteredPopup;
        _popup.Root.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressedInPopup);
        _popup.Escaped -= OnClosingChange;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _editor.RemoveHandler(InputElement.PointerPressedEvent, OnEditorPointerPressed);
        _editor.RemoveHandler(InputElement.PointerReleasedEvent, OnEditorPointerReleased);
    }

    /// <summary>
    /// Shows the hover at the caret, as the user asks for it by key: the diagnostics there and the server's hover. When
    /// a hover shows already, also one the pointer opened, the keyboard goes into it, as in VS Code: there the arrows,
    /// Page Up, Page Down, Home and End scroll it, Escape gives the keyboard back, and it stays wherever the pointer
    /// goes.
    /// </summary>
    public void ShowAtCaret()
    {
        if (_hovered is { } shown && _popup.IsOpen)
        {
            shown.IsHeld = true;
            StayOpen();
            _content.Focusable = true;
            _popup.Focus(_content);
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
        Start(offset, fromKeyboard: true);
    }

    /// <summary>
    /// Starts the hover at <paramref name="offset"/> at once, as the user asks for it by key: the server is asked, and
    /// the diagnostics show, with the server's hover once it answers.
    /// </summary>
    private void Start(int offset, bool fromKeyboard)
    {
        Hovered hovered = Begin(offset, fromKeyboard);
        Ask(hovered);
        Due(hovered);
    }

    /// <summary>
    /// Starts the hover of the pointer at <paramref name="offset"/>, as VS Code's hover operation starts: the server is
    /// asked halfway through <see cref="CodeEditor.HoverDelay"/>, what is known shows once the delay is over, and
    /// "Loading…" joins it when the server has not answered by three times the delay.
    /// </summary>
    private void StartDelayed(int offset)
    {
        Hovered hovered = Begin(offset, fromKeyboard: false);
        TimeSpan delay = _editor.HoverDelay;
        hovered.Timers =
        [
            After(hovered, delay / 2, Ask),
            After(hovered, delay, Due),
            After(hovered, 3 * delay, ShowLoading),
        ];
    }

    /// <summary>Ends the hover there is, and makes the hover of <paramref name="offset"/> the current one.</summary>
    private Hovered Begin(int offset, bool fromKeyboard)
    {
        Close();
        // A new hover forgets the size the user gave the last one.
        _frame.Width = double.NaN;
        _resized = false;
        var budget = new HoverBudget();
        TextRange word = WordAt(_buffer, offset);
        var hovered = new Hovered(
            offset,
            word,
            AlongWord(_buffer, word),
            _document.CurrentSnapshot,
            // Each diagnostic takes a line at least, so a hover never shows more of them than it has lines.
            HoverBlocks.Of(_squiggles.At(offset, HoverBudget.MaxLines), budget),
            budget
        )
        {
            FromKeyboard = fromKeyboard,
        };
        _hovered = hovered;
        return hovered;
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

    /// <summary>The delay is over: what is known shows.</summary>
    private void Due(Hovered hovered)
    {
        hovered.IsDue = true;
        Render(hovered);
    }

    /// <summary>The server has not answered by three times the delay: "Loading…" shows until it does.</summary>
    private void ShowLoading(Hovered hovered)
    {
        if (hovered.IsAnswered)
            return;
        hovered.IsLoading = true;
        Render(hovered);
    }

    /// <summary>
    /// Asks the server for its hover, and reads its Markdown, off the UI thread; it shows once the delay is over, unless
    /// the hover ended meanwhile. Failures of reading it go to the log.
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
            if (_hovered != hovered)
                return;
            if (hover is not null)
            {
                TextMateGrammar? grammar = _highlighter.Grammar;
                IReadOnlyList<HoverBlock> blocks = await Task.Run(
                    () => HoverBlocks.Of(hover, grammar, hovered.Budget, cancellation),
                    cancellation
                );
                if (_hovered != hovered)
                    return;
                if (hover.Range is { } range)
                    hovered.Range = Union(hovered.Range, range);
                hovered.Answer = blocks;
            }
            hovered.IsAnswered = true;
            Render(hovered);
        }
        // The hover ended while its answer was read.
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Showing the hover failed: {Exception}", e);
            if (_hovered == hovered)
            {
                hovered.IsAnswered = true;
                Render(hovered);
            }
        }
    }

    /// <summary>
    /// Shows what is known of <paramref name="hovered"/> once its delay is over: the diagnostics' messages, and below
    /// them the server's hover, or "Loading…" while it is late. With nothing to show, no popup shows.
    /// </summary>
    private void Render(Hovered hovered)
    {
        if (!hovered.IsDue)
            return;
        IReadOnlyList<HoverBlock> below =
            hovered.Answer
            ?? (
                hovered.IsLoading && !hovered.IsAnswered
                    ? [new HoverParagraph([new HoverSpan(Strings.Default.HoverLoading)])]
                    : []
            );
        IReadOnlyList<HoverBlock> blocks =
            hovered.Messages.Count == 0 ? below
            : below.Count == 0 ? hovered.Messages
            : [.. hovered.Messages, new HoverRule(), .. below];
        if (blocks.Count == 0)
        {
            StayOpen();
            _popup.Hide();
            return;
        }
        Show(hovered, blocks);
    }

    private void Show(Hovered hovered, IReadOnlyList<HoverBlock> blocks)
    {
        PopupColors colors = _colors?.Popups ?? PopupColors.Default;
        _frame.Paint(colors.HoverBackground, colors.HoverBorder, colors.HoverForeground, colors.Shadow);
        _content.SliderBrushes = (colors.Slider, colors.SliderHover, colors.SliderActive);
        // At least 500 pixels wide, up to two thirds of the editor, and scrolling beyond half its height.
        if (!_resized)
        {
            Size editor = _editor.Bounds.Size;
            _frame.MaxWidth = Math.Max(MinMaxWidth, Math.Floor(editor.Width * 2 / 3));
            _frame.MaxHeight = Math.Max(MinMaxHeight, Math.Floor(editor.Height / 2));
        }
        _content.Child = HoverView.Create(blocks, new PopupLook(_editor.FontFamily, _editor.FontSize, colors));
        // Above the hovered word, where the pointer comes from.
        _popup.Show(_textView, () => TextViewBoxes.CharacterAt(_textView, hovered.Word.Start));
    }

    /// <summary>
    /// While a hover shows, the pointer over other text closes it after <see cref="s_hidingDelay"/>, unless the pointer
    /// reaches it or comes back meanwhile: it may cross other text on its way into the popup. While none shows, the
    /// pointer on a character starts its hover, and on another character starts again, as in VS Code; not while a
    /// button is down, as when a drag selects, nor after a click until the pointer moves on.
    /// </summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point point = e.GetPosition(_textView);
        PointerPointProperties buttons = e.GetCurrentPoint(_textView).Properties;
        bool pressed = buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed;
        _pointer = pressed ? null : point;
        if (_clickedAt is { } clicked && point != clicked)
            _clickedAt = null;
        // A hover the user clicked or put the keyboard into stays, as does one asked for by key, as in VS Code.
        if (_hovered is { IsHeld: true } || (_hovered is { FromKeyboard: true } && _popup.IsOpen))
            return;
        if (_popup.IsOpen && _hovered is { } shown)
        {
            if (!pressed && OffsetAt(point) is { } offset && IsHovered(shown, offset))
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
    /// the one. A drag that selects the popup's text leaves it too, when it crosses its edge.
    /// </summary>
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender == _textView)
            _pointer = null;
        _editor.Dispatcher.Post(() =>
        {
            if (_hovered is not { FromKeyboard: false, IsHeld: false } || _popup.Root.IsPointerOver)
                return;
            if (_popup.IsOpen)
                HideSoon();
            else
                Close();
        });
    }

    private void OnPointerEnteredPopup(object? sender, PointerEventArgs e) => StayOpen();

    /// <summary>
    /// A click in the editor ends the hover, as in VS Code: on the text, also on the hovered word, its margin, its
    /// scroll bars or the search bar.
    /// </summary>
    private void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _clickedAt = e.GetPosition(_textView);
        Close();
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        _clickedAt = e.GetPosition(_textView);

    /// <summary>
    /// The user clicked into the popup, other than on an edge to resize it: it stays wherever the pointer goes, until a
    /// click outside it or the window losing the focus.
    /// </summary>
    private void OnPointerPressedInPopup(object? sender, PointerPressedEventArgs e)
    {
        if (_hovered is not { } hovered || _frame.IsResizing)
            return;
        hovered.IsHeld = true;
        StayOpen();
        if (_watched is not null || TopLevel.GetTopLevel(_editor) is not { } window)
            return;
        _watched = window;
        window.AddHandler(
            InputElement.PointerPressedEvent,
            OnPointerPressedInWindow,
            RoutingStrategies.Tunnel,
            handledEventsToo: true
        );
        if (window is WindowBase deactivates)
            deactivates.Deactivated += OnClosingChange;
    }

    /// <summary>
    /// A click outside the popup ends it, once the click is done: what it clicked takes the keyboard, or the text gets it
    /// back from the popup.
    /// </summary>
    private void OnPointerPressedInWindow(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (source == _popup.Root || _popup.Root.IsVisualAncestorOf(source)))
            return;
        Hovered? hovered = _hovered;
        _editor.Dispatcher.Post(() =>
        {
            if (_hovered == hovered)
                Close();
        });
    }

    private void Unwatch()
    {
        if (_watched is not { } window)
            return;
        _watched = null;
        window.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressedInWindow);
        if (window is WindowBase deactivates)
            deactivates.Deactivated -= OnClosingChange;
    }

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
                    if (_popup.Root.IsPointerOver || _frame.IsResizing)
                        StayOpen();
                    else
                    {
                        // As VS Code reacts to where the pointer went once the delay is over.
                        Close();
                        RestAt(_textView.IsPointerOver ? _pointer : null);
                    }
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
    /// A server started or stopped serving the document: a hover that shows its information ends, and one that waits
    /// starts again, to ask the server that serves it now.
    /// </summary>
    private void OnLanguageFeaturesChanged(object? sender, EventArgs e)
    {
        if (_hovered is not { } waiting || _popup.IsOpen)
            Close();
        else if (waiting.FromKeyboard)
            Start(waiting.Offset, fromKeyboard: true);
        else
            StartDelayed(waiting.Offset);
    }

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

    /// <summary>
    /// Whether <paramref name="offset"/> is in the hovered text: in its range, or further along a word that
    /// <see cref="WordAt"/> cut. Reads no text: how far the word goes was found when the hover started.
    /// </summary>
    private static bool IsHovered(Hovered hovered, int offset) =>
        hovered.Contains(offset) || (offset >= hovered.Along.Start && offset < hovered.Along.End);

    /// <summary>
    /// The word characters around <paramref name="word"/>, beyond where <see cref="WordAt"/> cut it, in the shown part
    /// of its line, its first 3,000 characters: the pointer may move along them without ending the hover. Just the word
    /// when it is not one, as a space or a line's end. Reads at most the shown part of one line.
    /// </summary>
    internal static TextRange AlongWord(TextDocument buffer, TextRange word)
    {
        if (word.Length == 0 || !WordCharacters.IsWordCharacter(buffer.GetCharAt(word.Start)))
            return word;
        DocumentLine line = buffer.GetLineByOffset(word.Start);
        int start = word.Start;
        while (start > line.Offset && WordCharacters.IsWordCharacter(buffer.GetCharAt(start - 1)))
            start--;
        int last = VisibleText.End(line);
        int end = word.End;
        while (end < last && WordCharacters.IsWordCharacter(buffer.GetCharAt(end)))
            end++;
        return TextRange.FromBounds(start, end);
    }

    /// <summary>
    /// How far the hovered word reaches on either side of the pointer, at most, as in VS Code: a longer word is cut
    /// there, so that a line of millions of word characters costs the pointer no more than this.
    /// </summary>
    internal const int MaxWordReach = 1_000;

    /// <summary>
    /// The word at <paramref name="offset"/>, by VS Code's word characters, reaching at most
    /// <see cref="MaxWordReach"/> characters to either side, and not into a long line's hidden tail; the character alone
    /// if it is none, and nothing at a line's end.
    /// </summary>
    internal static TextRange WordAt(TextDocument buffer, int offset)
    {
        DocumentLine line = buffer.GetLineByOffset(offset);
        if (offset == line.EndOffset)
            return new TextRange(offset, 0);
        if (!WordCharacters.IsWordCharacter(buffer.GetCharAt(offset)))
            return new TextRange(offset, 1);
        int first = Math.Max(line.Offset, offset - MaxWordReach);
        int start = offset;
        while (start > first && WordCharacters.IsWordCharacter(buffer.GetCharAt(start - 1)))
            start--;
        int last = Math.Min(VisibleText.End(line), offset + MaxWordReach);
        int end = offset;
        while (end < last && WordCharacters.IsWordCharacter(buffer.GetCharAt(end)))
            end++;
        return TextRange.FromBounds(start, end);
    }

    private static TextRange Union(TextRange a, TextRange b) =>
        TextRange.FromBounds(Math.Min(a.Start, b.Start), Math.Max(a.End, b.End));

    /// <summary>The text hovered, in the current text.</summary>
    /// <param name="offset">The character the pointer rested on.</param>
    /// <param name="word">The word there, which the popup is next to.</param>
    /// <param name="along">The word characters around it, which the pointer may move along.</param>
    /// <param name="snapshot">The text when the pointer rested: the popup closes when it changes.</param>
    /// <param name="messages">The diagnostics' messages.</param>
    /// <param name="budget">What is left of the text the hover shows, for the server's hover after the messages.</param>
    private sealed class Hovered(
        int offset,
        TextRange word,
        TextRange along,
        TextSnapshot snapshot,
        IReadOnlyList<HoverBlock> messages,
        HoverBudget budget
    )
    {
        public int Offset { get; } = offset;

        public TextRange Word { get; } = word;

        public TextRange Along { get; } = along;

        /// <summary>The text that the popup stays open over: the word, and the server's range.</summary>
        public TextRange Range { get; set; } = word;

        public TextSnapshot Snapshot { get; } = snapshot;

        public IReadOnlyList<HoverBlock> Messages { get; } = messages;

        public HoverBudget Budget { get; } = budget;

        /// <summary>Whether the user asked for it by key, at the caret, rather than by resting the pointer.</summary>
        public bool FromKeyboard { get; init; }

        /// <summary>
        /// Whether the user clicked into the popup, or put the keyboard into it: then it stays wherever the pointer
        /// goes.
        /// </summary>
        public bool IsHeld { get; set; }

        /// <summary>Cancels the server's request and the reading of its answer.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        /// <summary>The timers of the wait for the pointer's hover: for asking, showing and "Loading…".</summary>
        public ITimer[] Timers { get; set; } = [];

        /// <summary>Whether its delay is over, so that what is known shows.</summary>
        public bool IsDue { get; set; }

        /// <summary>Whether the server answered, also with nothing, or failed.</summary>
        public bool IsAnswered { get; set; }

        /// <summary>The server's hover, once it answered with one and its Markdown was read; null before.</summary>
        public IReadOnlyList<HoverBlock>? Answer { get; set; }

        /// <summary>Whether the server is late: "Loading…" shows until it answers.</summary>
        public bool IsLoading { get; set; }

        public bool Contains(int offset) => offset >= Range.Start && offset < Math.Max(Range.End, Range.Start + 1);

        /// <summary>Stops the timers, the server's request and the reading of its answer.</summary>
        public void Stop()
        {
            foreach (ITimer timer in Timers)
                timer.Dispose();
            Cancellation.Cancel();
        }
    }
}
