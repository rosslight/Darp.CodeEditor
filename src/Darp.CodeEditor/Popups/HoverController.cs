using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The hover of one editor's view of a document: resting the pointer over text shows the messages of the diagnostics
/// there, above the server's hover, in a popup that looks like the app's tooltips.
/// </summary>
/// <remarks>
/// <para>
/// The pointer rests for AvaloniaEdit's hover time, 400 ms, as long as Avalonia's tooltips wait. The diagnostics show
/// at once, and the server's hover joins them when it answers; its Markdown is read, and its code blocks highlighted,
/// off the UI thread.
/// </para>
/// <para>
/// The hovered text is the word under the pointer, and the range that the server's hover names. The popup stays while
/// the pointer is over it or over the popup, so that its links can be clicked and it can be scrolled.
/// </para>
/// <para>
/// <see cref="Close"/> ends the hover, for every reason it ends: the pointer leaves both, also for another word under
/// the same diagnostic; typing, scrolling and Escape; the editor losing the keyboard focus; a server that starts or
/// stops serving the document, whose information the hover showed; new colors, which its code was drawn in; and the
/// editor no longer showing the document. Ending it cancels the server's request and the reading of its answer if they
/// are still under way, and drops what they would show, also before any popup opened.
/// </para>
/// </remarks>
internal sealed class HoverController : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly TextView _textView;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SquiggleLayer _squiggles;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly ScrollViewer _content = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly EditorPopup _popup;
    private TextAreaColors? _colors;

    /// <summary>The text hovered now, from the pointer's rest until the popup closes; null for none.</summary>
    private Hovered? _hovered;

    public HoverController(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        SquiggleLayer squiggles,
        BackgroundTextMateHighlighter highlighter
    )
    {
        _editor = editor;
        _textView = textArea.TextView;
        _buffer = textArea.Document;
        _document = document;
        _squiggles = squiggles;
        _highlighter = highlighter;
        _popup = new EditorPopup(editor, new HoverToolTip { Content = _content });
        _textView.PointerHover += OnPointerHover;
        _textView.PointerMoved += OnPointerMoved;
        _textView.PointerExited += OnPointerExited;
        _textView.ScrollOffsetChanged += OnClosingChange;
        _buffer.Changed += OnClosingChange;
        _document.LanguageFeaturesChanged += OnClosingChange;
        _popup.Root.PointerExited += OnPointerExited;
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
        bool wasOpen = _popup.IsOpen;
        _hovered?.Cancellation.Cancel();
        _hovered = null;
        _popup.Hide();
        return wasOpen;
    }

    public void Dispose()
    {
        Close();
        _textView.PointerHover -= OnPointerHover;
        _textView.PointerMoved -= OnPointerMoved;
        _textView.PointerExited -= OnPointerExited;
        _textView.ScrollOffsetChanged -= OnClosingChange;
        _buffer.Changed -= OnClosingChange;
        _document.LanguageFeaturesChanged -= OnClosingChange;
        _popup.Root.PointerExited -= OnPointerExited;
    }

    /// <summary>
    /// The pointer rests: over the text already hovered, nothing changes; over other text, its diagnostics show, and
    /// the server is asked for its hover.
    /// </summary>
    private void OnPointerHover(object? sender, PointerEventArgs e)
    {
        if (
            OffsetAt(e.GetPosition(_textView)) is not { } offset
            || (_hovered is { } current && IsHovered(current, offset))
        )
            return;
        Close();
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
        );
        _hovered = hovered;
        if (hovered.Messages.Count > 0)
            Show(hovered, hovered.Messages);
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
            if (_hovered != hovered)
                return;
            if (hover.Range is { } range)
                hovered.Range = Union(hovered.Range, range);
            Show(hovered, hovered.Messages.Count > 0 ? [.. hovered.Messages, new HoverRule(), .. blocks] : blocks);
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

    private void Show(Hovered hovered, IReadOnlyList<HoverBlock> blocks)
    {
        var code = new CodeLook(_editor.FontFamily, _editor.FontSize, _colors?.Background, _colors?.Foreground);
        _content.Content = HoverView.Create(blocks, code);
        // Above the hovered word, where the pointer comes from.
        _popup.Show(_textView, () => TextViewBoxes.CharacterAt(_textView, hovered.Word.Start));
    }

    /// <summary>Closes the popup once the pointer moves over other text.</summary>
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_hovered is not { } hovered)
            return;
        if (OffsetAt(e.GetPosition(_textView)) is not { } offset || !IsHovered(hovered, offset))
            Close();
    }

    /// <summary>
    /// Closes the popup once the pointer has left both the text and the popup: posted, as the pointer enters the other
    /// only after it left the one.
    /// </summary>
    private void OnPointerExited(object? sender, PointerEventArgs e) =>
        _editor.Dispatcher.Post(() =>
        {
            if (!_textView.IsPointerOver && !_popup.Root.IsPointerOver)
                Close();
        });

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

        /// <summary>Cancels the server's request and the reading of its answer.</summary>
        public CancellationTokenSource Cancellation { get; } = new();

        public bool Contains(int offset) => offset >= Range.Start && offset < Math.Max(Range.End, Range.Start + 1);
    }

    /// <summary>The app's tooltip, shown open, as the hover's look: its font, padding, corners and colors.</summary>
    private sealed class HoverToolTip : ToolTip
    {
        public HoverToolTip() => PseudoClasses.Set(":open", true);

        protected override Type StyleKeyOverride => typeof(ToolTip);
    }
}
