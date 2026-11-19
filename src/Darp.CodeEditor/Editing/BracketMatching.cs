using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The brackets at the caret, as VS Code's <c>BracketMatchingController</c>: boxes the bracket next to a caret without
/// a selection and its match, or else, as <see cref="CodeEditorOptions.MatchBrackets"/> says, the innermost pair around
/// it, in the color theme's bracket match colors; and goes to the matching bracket
/// (<c>editor.action.jumpToBracket</c>) or selects a pair (<c>editor.action.selectToBracket</c>).
/// </summary>
/// <remarks>
/// The pair is found when the text view draws, after the caret, the selection or the text changed or the lines were
/// drawn again: once per frame, however many changes came before it. A change draws the text view again without
/// measuring it again: AvaloniaEdit's <c>InvalidateLayer</c> measures it, which a caret move along a line otherwise does
/// not. When the background changes lines' tokens, also out of view, the pair is found at once.
/// </remarks>
internal sealed class BracketMatching : IBackgroundRenderer, IEditorFeature
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;
    private readonly Func<CodeEditorOptions> _options;
    private TextAreaColors? _colors;
    private (int Open, int Close)? _pair;

    /// <summary>Whether the caret, the selection, the text or the lines' tokens changed since the pair was found.</summary>
    private bool _stale = true;

    public BracketMatching(
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        TextChanges changes,
        Func<CodeEditorOptions> options
    )
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _highlighter = highlighter;
        _changes = changes;
        _options = options;
        textArea.Caret.PositionChanged += OnChanged;
        textArea.SelectionChanged += OnChanged;
        _buffer.TextChanged += OnChanged;
        textArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        highlighter.TokensChanged += OnTokensChanged;
        textArea.TextView.BackgroundRenderers.Add(this);
    }

    /// <summary>Behind the selection, in the layer that the text view draws itself.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _textArea.TextView.InvalidateVisual();
        }
    }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.JumpToBracket, JumpToBracket);
        commands.Add(CodeEditorCommands.SelectToBracket, SelectToBracket);
    }

    /// <summary>Boxes the brackets as the new option says.</summary>
    public void OnOptionsChanged(MatchBrackets old, MatchBrackets now)
    {
        if (old == now)
            return;
        _stale = true;
        _textArea.TextView.InvalidateVisual();
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_stale)
        {
            _stale = false;
            _pair = Find();
        }
        if (_pair is not { } pair || _colors is not { } colors)
            return;
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            BorderThickness = colors.BracketMatchBorder.Thickness,
        };
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.Open, Length = 1 });
        // Two boxes, also for brackets side by side.
        builder.CloseFigure();
        builder.AddSegment(textView, new TextSegment { StartOffset = pair.Close, Length = 1 });
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(colors.BracketMatchBackground, colors.BracketMatchBorder, geometry);
    }

    public void Dispose()
    {
        _textArea.Caret.PositionChanged -= OnChanged;
        _textArea.SelectionChanged -= OnChanged;
        _buffer.TextChanged -= OnChanged;
        _textArea.TextView.VisualLinesChanged -= OnVisualLinesChanged;
        _highlighter.TokensChanged -= OnTokensChanged;
        _textArea.TextView.BackgroundRenderers.Remove(this);
    }

    /// <summary>The pair to box: none for a selection, as in VS Code.</summary>
    private (int Open, int Close)? Find()
    {
        MatchBrackets match = _options().MatchBrackets;
        if (match == MatchBrackets.Never || !_textArea.Selection.IsEmpty)
            return null;
        int caret = _textArea.Caret.Offset;
        return BracketMatcher.FindPair(_buffer, caret, Brackets, _highlighter.TokensOf)
            ?? (
                match == MatchBrackets.Always
                    ? BracketMatcher.FindEnclosing(_buffer, caret, Brackets, _highlighter.TokensOf)
                    : null
            );
    }

    /// <summary>
    /// Moves the caret to the bracket that matches the one at the start of the selection, as VS Code's
    /// <c>jumpToBracket</c> does: before the closing bracket from its opening one, before the opening one from its
    /// closing one; elsewhere before the closing bracket of the pair around it, or else before the next bracket, or
    /// else to the selection's start.
    /// </summary>
    private void JumpToBracket()
    {
        int position = SelectionStart();
        int target = BracketMatcher.FindPair(_buffer, position, Brackets, _highlighter.TokensOf) is var (open, close)
            ? Touches(close, position)
                ? open
                : close
            : BracketMatcher.FindEnclosing(_buffer, position, Brackets, _highlighter.TokensOf)?.Close
                ?? BracketMatcher.FindNext(_buffer, position, Brackets, _highlighter.TokensOf)
                ?? position;
        _changes.Select(new TextSelection(target));
    }

    /// <summary>
    /// Selects the pair that VS Code's <c>selectToBracket</c> finds at the start of the selection, with the text between
    /// its brackets: the pair of the bracket there, or else the pair around it, or else the pair of the next bracket.
    /// From a closing bracket, the selection goes backward.
    /// </summary>
    /// <returns>Whether there was a pair to select.</returns>
    private bool SelectToBracket()
    {
        int position = SelectionStart();
        Func<int, int[]?> tokensOf = _highlighter.TokensOf;
        if (
            (
                BracketMatcher.FindPair(_buffer, position, Brackets, tokensOf)
                ?? BracketMatcher.FindEnclosing(_buffer, position, Brackets, tokensOf)
                ?? (
                    BracketMatcher.FindNext(_buffer, position, Brackets, tokensOf) is { } next
                        ? BracketMatcher.FindPair(_buffer, next, Brackets, tokensOf)
                        : null
                )
            )
            is not var (open, close)
        )
            return false;
        _changes.Select(
            Touches(close, position) ? new TextSelection(close + 1, open) : new TextSelection(open, close + 1)
        );
        return true;
    }

    /// <summary>Whether a bracket at <paramref name="bracket"/> touches <paramref name="position"/>: before or after it.</summary>
    private static bool Touches(int bracket, int position) => position == bracket || position == bracket + 1;

    private int SelectionStart() =>
        _textArea.Selection.IsEmpty ? _textArea.Caret.Offset : _textArea.Selection.SurroundingSegment.Offset;

    /// <summary>The lines were built again, with new tokens maybe: the frame that follows finds the pair again.</summary>
    private void OnVisualLinesChanged(object? sender, EventArgs e) => _stale = true;

    /// <summary>
    /// The background tokenized lines, also out of view, whose brackets may be in strings or comments now: finds the
    /// pair at once, and draws again only when it changed. Drawing again for each of the background's batches drew the
    /// text view twice per Page Down, which cost it 45%.
    /// </summary>
    private void OnTokensChanged(object? sender, EventArgs e)
    {
        (int Open, int Close)? pair = Find();
        _stale = false;
        if (pair == _pair)
            return;
        _pair = pair;
        _textArea.TextView.InvalidateVisual();
    }

    /// <summary>
    /// Draws again where a pair shows or may show now: around the caret, next to a bracket, or instead of the last pair.
    /// </summary>
    private void OnChanged(object? sender, EventArgs e)
    {
        _stale = true;
        int caret = _textArea.Caret.Offset;
        MatchBrackets match = _options().MatchBrackets;
        if (
            _pair is not null
            || match == MatchBrackets.Always
            || (match == MatchBrackets.Near && (IsBracketCharacter(caret) || IsBracketCharacter(caret - 1)))
        )
            _textArea.TextView.InvalidateVisual();
    }

    /// <summary>The language's brackets; none while they load.</summary>
    private IReadOnlyList<CharacterPair> Brackets => LanguageConfiguration.Of(_document.Language).Brackets;

    private bool IsBracketCharacter(int offset)
    {
        if (offset < 0 || offset >= _buffer.TextLength)
            return false;
        char c = _buffer.GetCharAt(offset);
        return Brackets.Any(pair => pair is { Open: [char open], Close: [char close] } && (c == open || c == close));
    }
}
