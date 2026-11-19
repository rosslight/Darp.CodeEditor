using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The auto-closing and surrounding pairs of one editor's view, from the language's <c>autoClosingPairs</c> and
/// <c>surroundingPairs</c>, as VS Code types them: an opening character brings its closing one, which typing steps over
/// and Backspace deletes with it while the caret stays inside the pair; an opening character typed over a selection
/// surrounds it. In a rectangle selection, each acts on every line or on none. <see cref="CodeEditorOptions.AutoClosing"/>
/// says where brackets and quotes close; typing over and deleting act only on pairs that the editor closed, and
/// surrounding on every pair, as VS Code's defaults for <c>editor.autoClosingOvertype</c>, <c>autoClosingDelete</c> and
/// <c>autoSurround</c>. Surrounding and deleting a pair are each a step of their own.
/// </summary>
/// <remarks>
/// A pair that the language does not close in strings or comments asks the highlighter for the tokens where the user
/// types, found on the UI thread within a short time limit. While they are unknown, such a pair does not close.
/// </remarks>
internal sealed class AutoClosingPairs : IDisposable
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;
    private readonly Func<CodeEditorOptions> _options;

    /// <summary>The closing texts that the editor typed, while the caret stays inside their pair.</summary>
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

    /// <summary>The language's editing rules; none while they load.</summary>
    private LanguageConfiguration Configuration =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded
            ? loaded.Result.Configuration
            : LanguageConfiguration.None;

    public void Dispose()
    {
        _area.Caret.PositionChanged -= OnCaretPositionChanged;
        _autoClosed.Clear();
    }

    /// <summary>
    /// Typing <paramref name="typed"/> over a selection with text: the pair it opens goes around the selection, which
    /// keeps the text inside selected. Not around whitespace only, and a quote typed over a single quote replaces it.
    /// </summary>
    /// <returns>Whether it surrounded the selection.</returns>
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
    /// Typing <paramref name="typed"/> right before the same closing character that the editor typed steps over it:
    /// only a single character, and not a quote that a backslash escapes.
    /// </summary>
    /// <returns>Whether it stepped over.</returns>
    public bool TypeOver(char typed)
    {
        if (TypingOffsets() is not { } offsets || !Array.TrueForAll(offsets, IsClosedBefore))
            return false;
        _changes.Run(_changes.Typing(typed.ToString()), () => _changes.Apply([], moved: 1));
        return true;

        bool IsClosedBefore(int offset) =>
            offset < Buffer.TextLength
            && Buffer.GetCharAt(offset) == typed
            && !(IsQuote(typed) && IsEscaped(offset))
            && _autoClosed.Exists(pair => pair.Close.Offset == offset && pair.CloseText.Length == 1);
    }

    /// <summary>
    /// The pair that <paramref name="typed"/> closes where it goes in, the same one at each place, or none: the one with
    /// the longest opening that the character completes. It closes where the options let it, or before another closing
    /// character; a quote not right after a word, unless quotes always close; and a pair not closed in strings or
    /// comments only where the tokens before the caret and of the typed text are neither.
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
        if (TypingOffsets() is not { } offsets)
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
            _autoClosed.Add(new AutoClosed(open, close, pair.Close));
        }
    }

    /// <summary>
    /// Backspace between an opening character and the closing one that the editor typed deletes both: for pairs of
    /// single characters.
    /// </summary>
    /// <returns>Whether it deleted them.</returns>
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
            AutoClosingOptions options = _options().AutoClosing;
            return (IsQuote(open) ? options.Quotes : options.Brackets) != AutoClosingMode.Never
                && Configuration.AutoClosingPairs.Any(pair =>
                    pair is { Open: [char o], Close: [char c] } && o == open && c == close
                )
                && _autoClosed.Exists(pair => pair.Close.Offset == offset);
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
        LanguageConfiguration configuration = Configuration;
        AutoClosingOptions options = _options().AutoClosing;
        bool quote = IsQuote(typed);
        AutoClosingMode mode =
            quote ? options.Quotes
            // Pairs that open a block comment close by the language's rules alone: comments have no option.
            : configuration.BlockComment is { } block && candidate.Open.Contains(block.Open, StringComparison.Ordinal)
                ? AutoClosingMode.LanguageDefined
            : options.Brackets;
        if (mode == AutoClosingMode.Never)
            return null;
        string after = Buffer.GetText(caret, line.EndOffset - caret);
        if (after.Length > 0 && !ClosesBefore(mode, quote, after[0]) && !StartsWithClosingOnly(after))
            return null;
        if (
            candidate.Open is ['\'' or '"']
            && mode != AutoClosingMode.Always
            && caret > line.Offset
            && WordCharacters.IsWordCharacter(Buffer.GetCharAt(caret - 1))
        )
            return null;
        if (!candidate.NotInString && !candidate.NotInComment)
            return candidate;
        // Without a grammar, there are no strings or comments.
        if (_document.Language.Loaded.Result.Grammar is null)
            return candidate;
        if (_highlighter.ContextAt(caret) is not { } context)
            return null;
        return Excludes(candidate, context.Before) || Excludes(candidate, context.At) ? null : candidate;

        static bool Excludes(AutoClosingPair pair, TokenType type) =>
            (pair.NotInString && type == TokenType.String) || (pair.NotInComment && type == TokenType.Comment);
    }

    /// <summary>Whether a quote, or else a bracket, closes before <paramref name="next"/> by <paramref name="mode"/>.</summary>
    private bool ClosesBefore(AutoClosingMode mode, bool quote, char next) =>
        mode switch
        {
            AutoClosingMode.Always => true,
            AutoClosingMode.LanguageDefined => Configuration.AutoCloseBeforeSet(forQuotes: quote).Contains(next),
            AutoClosingMode.BeforeWhitespace => next is ' ' or '\t',
            _ => false,
        };

    /// <summary>
    /// Whether <paramref name="after"/> starts with the closing text of a pair, and not with the opening text of
    /// another, as a quote does.
    /// </summary>
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

    /// <summary>
    /// Whether text typed at <paramref name="offset"/> is escaped: after an odd number of backslashes, as two of them
    /// are an escaped backslash.
    /// </summary>
    private bool IsEscaped(int offset)
    {
        int backslashes = 0;
        while (offset - backslashes > 0 && Buffer.GetCharAt(offset - backslashes - 1) == '\\')
            backslashes++;
        return backslashes % 2 == 1;
    }

    private static bool IsQuote(char c) => c is '\'' or '"' or '`';

    /// <summary>A closing text that the editor typed, and where its pair starts.</summary>
    private sealed record AutoClosed(TextAnchor Open, TextAnchor Close, string CloseText)
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
