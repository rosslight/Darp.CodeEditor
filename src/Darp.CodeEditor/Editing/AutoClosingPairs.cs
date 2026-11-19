using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The language's <c>autoClosingPairs</c> and <c>surroundingPairs</c> in one editor's view, as VS Code types them, on
/// every line of a rectangle selection or none: an opening character brings its closing one where
/// <see cref="CodeEditorOptions.AutoClosing"/> lets it; typing steps over, and Backspace deletes, only what the editor
/// closed, while the caret stays inside, as VS Code's default <c>editor.autoClosingOvertype</c> and
/// <c>autoClosingDelete</c>; an opening character typed over a selection surrounds it. A pair kept out of strings or
/// comments closes only where the tokens, found within a short time on the UI thread, say neither.
/// </summary>
internal sealed class AutoClosingPairs : IDisposable
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;
    private readonly Func<CodeEditorOptions> _options;

    /// <summary>The closing texts that the editor typed, kept while the caret stays inside their pair.</summary>
    private readonly List<AutoClosed> _autoClosed = [];

    public AutoClosingPairs(
        TextArea area,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        TextChanges changes,
        Func<CodeEditorOptions> options
    )
    {
        _area = area;
        _document = document;
        _highlighter = highlighter;
        _changes = changes;
        _options = options;
        area.Caret.PositionChanged += OnCaretPositionChanged;
    }

    private TextDocument Buffer => _area.Document;

    private LanguageConfiguration Configuration => LanguageConfiguration.Of(_document.Language);

    public void Dispose() => _area.Caret.PositionChanged -= OnCaretPositionChanged;

    /// <summary>
    /// Typing <paramref name="typed"/> over a selection puts the pair it opens around it, in a step of its own, the text
    /// inside still selected: not around whitespace only, and a quote typed over a single quote replaces it.
    /// </summary>
    public bool Surround(char typed)
    {
        if (TypingOffsets() is not null || SurroundingPair(typed) is not { } pair)
            return false;
        var edits = new List<TextEdit>();
        foreach (SelectionSegment segment in _area.Selection.Segments)
        {
            string selected = Buffer.GetText(segment);
            if (string.IsNullOrWhiteSpace(selected) || (IsQuote(typed) && selected is [char quote] && IsQuote(quote)))
                return false;
            edits.Add(new TextEdit(segment.StartOffset, 0, pair.Open));
            edits.Add(new TextEdit(segment.EndOffset, 0, pair.Close));
        }
        bool surrounded = false;
        _changes.Run(EditStep.Own, () => surrounded = _changes.Apply(edits, moved: pair.Open.Length));
        return surrounded;
    }

    /// <summary>
    /// Forgets the closing texts of the kinds of pairs whose option changed: a change, also off and on again, ends what
    /// the editor closed before it.
    /// </summary>
    public void OnOptionsChanged(AutoClosingOptions old, AutoClosingOptions now) =>
        _autoClosed.RemoveAll(closed => Mode(closed.Pair, old) != Mode(closed.Pair, now));

    /// <summary>
    /// Typing <paramref name="typed"/> right before the same closing character that the editor typed steps over it:
    /// only a single character, and not a quote that a backslash escapes.
    /// </summary>
    public bool TypeOver(char typed)
    {
        if (TypingOffsets() is not { } offsets || !Array.TrueForAll(offsets, IsClosedBefore))
            return false;
        _changes.Run(_changes.Typing(typed.ToString()), () => _changes.Apply([], moved: 1));
        return true;

        bool IsClosedBefore(int offset) =>
            offset < Buffer.TextLength
            && Buffer.GetCharAt(offset) == typed
            && Configuration.AutoClosingPairs.Any(pair =>
                pair is { Open.Length: 1, Close: [char close] } && close == typed
            )
            && !(IsQuote(typed) && IsEscaped(offset))
            && _autoClosed.Exists(pair => pair.Close.Offset == offset && pair.CloseText.Length == 1);
    }

    /// <summary>
    /// The pair that <paramref name="typed"/> closes where it goes in, the same one at each place, or none: the one with
    /// the longest opening that the character completes. It closes where the options let it, or before another closing
    /// character; a quote not right after a word, unless quotes always close; a bracket not before a closing one that
    /// no opening one matches, unless brackets always close; and a pair not closed in strings or comments only where the
    /// tokens before the caret and of the typed text are neither. A longer pair that contains a shorter one, as
    /// <c>(*</c> contains <c>(</c>, closes with the shorter one's closing text that is already after the caret: its
    /// <see cref="AutoClosingPair.Close"/> is what goes in.
    /// </summary>
    public AutoClosingPair? PairToClose(char typed)
    {
        if (TypingOffsets() is not { } offsets)
            return null;
        AutoClosingPair? found = PairToClose(typed, offsets[0]);
        for (int i = 1; i < offsets.Length && found is not null; i++)
        {
            if (PairToClose(typed, offsets[i]) != found)
                return null;
        }
        return found;
    }

    /// <summary>
    /// Types the closing text of <paramref name="pair"/> right after its opening text, which just went in, at the caret
    /// or on every line of an empty rectangle selection. The caret and the selection stay before it.
    /// </summary>
    public void Close(AutoClosingPair pair)
    {
        // Text can be refused after it was typed, and the opening not go in.
        if (TypingOffsets() is not { } offsets || !Array.TrueForAll(offsets, HasOpening))
            return;
        if (!_changes.Apply([.. offsets.Select(offset => new TextEdit(offset, 0, pair.Close))], moved: 0))
            return;
        for (int i = 0; i < offsets.Length; i++)
        {
            // Each line above got its closing text too.
            int offset = offsets[i] + i * pair.Close.Length;
            TextAnchor open = Buffer.CreateAnchor(offset - pair.Open.Length);
            TextAnchor close = Buffer.CreateAnchor(offset);
            // Text typed right before either one goes before it.
            open.MovementType = AnchorMovementType.AfterInsertion;
            close.MovementType = AnchorMovementType.AfterInsertion;
            _autoClosed.Add(new AutoClosed(open, close, pair.Close, pair));
        }

        bool HasOpening(int offset) =>
            offset >= pair.Open.Length && Buffer.GetText(offset - pair.Open.Length, pair.Open.Length) == pair.Open;
    }

    /// <summary>
    /// Backspace between an opening character and the closing one that the editor typed deletes both, in a step of its
    /// own: for pairs of single characters.
    /// </summary>
    public bool DeletePair()
    {
        if (TypingOffsets() is not { } offsets || !Array.TrueForAll(offsets, IsInEmptyPair))
            return false;
        bool deleted = false;
        _changes.Run(
            new EditStep(EditKind.DeletingLeft, StartsStep: true),
            () => deleted = _changes.Apply([.. offsets.Select(offset => new TextEdit(offset - 1, 2, ""))], moved: -1)
        );
        return deleted;

        bool IsInEmptyPair(int offset)
        {
            if (offset == 0 || offset >= Buffer.TextLength)
                return false;
            char open = Buffer.GetCharAt(offset - 1);
            char close = Buffer.GetCharAt(offset);
            return Configuration.AutoClosingPairs.Any(pair =>
                    pair is { Open: [char o], Close: [char c] } && o == open && c == close
                ) && _autoClosed.Exists(pair => pair.Close.Offset == offset);
        }
    }

    /// <summary>
    /// Where typed text goes in: at the caret, or on every line of an empty rectangle selection, as AvaloniaEdit types
    /// there. Null for a selection with text, which typing replaces.
    /// </summary>
    private int[]? TypingOffsets() =>
        _area.Selection switch
        {
            RectangleSelection { Length: 0 } rectangle => [.. rectangle.Segments.Select(s => s.StartOffset)],
            { IsEmpty: true } => [_area.Caret.Offset],
            _ => null,
        };

    /// <summary>Forgets the closing texts that are gone, or that the caret left, or every line of a rectangle selection.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        int[] positions = TypingOffsets() ?? [_area.Caret.Offset];
        _autoClosed.RemoveAll(pair => !Array.Exists(positions, position => pair.Encloses(position, Buffer)));
    }

    private AutoClosingPair? PairToClose(char typed, int caret)
    {
        DocumentLine line = Buffer.GetLineByOffset(caret);
        AutoClosingPair? found = null;
        foreach (AutoClosingPair pair in Configuration.AutoClosingPairs)
        {
            string open = pair.Open;
            int typedBefore = open.Length - 1;
            if (
                open[^1] == typed
                && open.Length > (found?.Open.Length ?? 0)
                && caret - line.Offset >= typedBefore
                && Buffer.GetText(caret - typedBefore, typedBefore) == open[..^1]
            )
                found = pair;
        }
        if (found is not { } candidate)
            return null;
        AutoClosingMode mode = Mode(candidate, _options().AutoClosing);
        if (mode == AutoClosingMode.Never)
            return null;
        // As far as the longest text of a pair, which the checks after the caret need, also on a long line.
        int needed = Configuration.AutoClosingPairs.Max(pair => Math.Max(pair.Open.Length, pair.Close.Length));
        string after = Buffer.GetText(caret, Math.Min(line.EndOffset - caret, needed));
        if (after.Length > 0 && !ClosesBefore(mode, IsQuote(typed), after[0]) && !StartsWithClosingOnly(after))
            return null;
        if (
            candidate.Open is ['\'' or '"']
            && mode != AutoClosingMode.Always
            && caret > line.Offset
            && WordCharacters.IsWordCharacter(Buffer.GetCharAt(caret - 1))
        )
            return null;
        if (
            (mode != AutoClosingMode.Always && UnmatchedClosingAhead(candidate, caret)) || !ClosesHere(candidate, caret)
        )
            return null;
        return Contained(candidate) is { } contained && after.StartsWith(contained.Close, StringComparison.Ordinal)
            ? candidate with
            {
                Close = candidate.Close[..^contained.Close.Length],
            }
            : candidate;
    }

    /// <summary>
    /// Whether a closing bracket of <paramref name="pair"/> after <paramref name="caret"/> closes nothing, among the
    /// language's brackets: the bracket typed there takes it.
    /// </summary>
    private bool UnmatchedClosingAhead(AutoClosingPair pair, int caret)
    {
        CharacterPair[] brackets = [.. Configuration.Brackets.Where(b => b is { Open.Length: 1, Close.Length: 1 })];
        return pair is { Open: [char open], Close: [char close] }
            && open != close
            && brackets.Contains(new CharacterPair(pair.Open, pair.Close))
            && BracketMatcher.UnmatchedClosing(
                Buffer,
                caret,
                new(pair.Open, pair.Close),
                brackets,
                _highlighter.TokensOf
            );
    }

    /// <summary>
    /// Whether <paramref name="pair"/> closes at <paramref name="caret"/> by its tokens: anywhere, unless the language
    /// keeps it out of strings or comments and the tokens before the caret or of the typed text are one, or unknown.
    /// </summary>
    private bool ClosesHere(AutoClosingPair pair, int caret)
    {
        // Without a grammar, there are no strings or comments.
        if ((!pair.NotInString && !pair.NotInComment) || _document.Language.Loaded.Result.Grammar is null)
            return true;
        return _highlighter.ContextAt(caret) is { } context && !Excludes(context.Before) && !Excludes(context.At);

        bool Excludes(TokenType type) =>
            (pair.NotInString && type == TokenType.String) || (pair.NotInComment && type == TokenType.Comment);
    }

    /// <summary>The longest shorter pair inside <paramref name="pair"/> that ends its closing text; null for none.</summary>
    private AutoClosingPair? Contained(AutoClosingPair pair)
    {
        AutoClosingPair? found = null;
        foreach (AutoClosingPair other in Configuration.AutoClosingPairs)
        {
            if (
                other.Open.Length < pair.Open.Length
                && other.Open.Length > (found?.Open.Length ?? 0)
                && pair.Open.Contains(other.Open, StringComparison.Ordinal)
                && pair.Close.EndsWith(other.Close, StringComparison.Ordinal)
            )
                found = other;
        }
        return found;
    }

    /// <summary>
    /// Where pairs of the kind of <paramref name="pair"/> close: quotes and brackets as the options say, and pairs that
    /// open a block comment by the language's rules alone, as comments have no option.
    /// </summary>
    private AutoClosingMode Mode(AutoClosingPair pair, AutoClosingOptions options) =>
        IsQuote(pair.Open[^1]) ? options.Quotes
        : Configuration.BlockComment is { } block && pair.Open.Contains(block.Open, StringComparison.Ordinal)
            ? AutoClosingMode.LanguageDefined
        : options.Brackets;

    /// <summary>Whether a quote, or else a bracket, closes before <paramref name="next"/> by <paramref name="mode"/>.</summary>
    private bool ClosesBefore(AutoClosingMode mode, bool quote, char next) =>
        mode switch
        {
            AutoClosingMode.Always => true,
            AutoClosingMode.LanguageDefined => Configuration.AutoCloseBeforeSet(forQuotes: quote).Contains(next),
            AutoClosingMode.BeforeWhitespace => next is ' ' or '\t',
            _ => false,
        };

    /// <summary>Whether <paramref name="after"/> starts with a closing text, and not an opening one, as a quote is both.</summary>
    private bool StartsWithClosingOnly(string after)
    {
        IReadOnlyList<AutoClosingPair> pairs = Configuration.AutoClosingPairs;
        return pairs.Any(pair => after.StartsWith(pair.Close, StringComparison.Ordinal))
            && !pairs.Any(pair => after.StartsWith(pair.Open, StringComparison.Ordinal));
    }

    /// <summary>The last surrounding pair that opens with <paramref name="typed"/>; null for none.</summary>
    private CharacterPair? SurroundingPair(char typed)
    {
        CharacterPair? found = null;
        foreach (CharacterPair pair in Configuration.SurroundingPairs)
        {
            if (pair.Open is [char open] && open == typed)
                found = pair;
        }
        return found;
    }

    /// <summary>Whether text typed at <paramref name="offset"/> follows an odd number of backslashes.</summary>
    private bool IsEscaped(int offset)
    {
        int backslashes = 0;
        while (offset - backslashes > 0 && Buffer.GetCharAt(offset - backslashes - 1) == '\\')
            backslashes++;
        return backslashes % 2 == 1;
    }

    private static bool IsQuote(char c) => c is '\'' or '"' or '`';

    /// <summary>A closing text that the editor typed, where its pair starts, and the pair.</summary>
    private sealed record AutoClosed(TextAnchor Open, TextAnchor Close, string CloseText, AutoClosingPair Pair)
    {
        /// <summary>
        /// Whether the closing text is still there, and <paramref name="caret"/> inside the pair: from before its
        /// opening text to after its closing text.
        /// </summary>
        public bool Encloses(int caret, TextDocument buffer) =>
            !Open.IsDeleted
            && !Close.IsDeleted
            && Open.Offset <= caret
            && caret <= Close.Offset + CloseText.Length
            && Close.Offset + CloseText.Length <= buffer.TextLength
            && buffer.GetText(Close.Offset, CloseText.Length) == CloseText;
    }
}
