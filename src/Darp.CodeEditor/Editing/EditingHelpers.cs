using Avalonia.Input;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The editing helpers of one editor's view of a document, from the language's editing rules, as VS Code applies
/// them: Enter indents the new line, typing outdents a line that comes to match the decrease pattern, opening
/// characters bring their closing ones, which are typed over and deleted with them, and opening characters surround a
/// selection. Tab and Shift+Tab are AvaloniaEdit's, with the document's indentation.
/// </summary>
/// <remarks>
/// <para>
/// Each helper is one step that the user undoes: on its own, or with the character just typed. Typed characters still
/// reach AvaloniaEdit and the other handlers of typed text: a closing character goes in after the opening one, in the
/// same undo step.
/// </para>
/// <para>
/// A pair that the language does not close in strings or comments asks the highlighter for the tokens where the user
/// types, found on the UI thread within a short time limit. While they are unknown, such a pair does not close.
/// </para>
/// </remarks>
internal sealed class EditingHelpers : IDisposable
{
    /// <summary>The characters before which a pair closes, as VS Code's default <c>autoCloseBefore</c>.</summary>
    private const string CloseBefore = ";:.,=}])> \n\t";

    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;

    /// <summary>
    /// Closing characters that the editor typed: typing them steps over them, and Backspace deletes them with their
    /// opening character, while the caret stays inside the pair.
    /// </summary>
    private readonly List<AutoClosed> _autoClosed = [];

    /// <summary>The pair to close once the character being typed is in.</summary>
    private AutoClosingPair? _closing;

    public EditingHelpers(TextArea textArea, CodeDocument document, BackgroundTextMateHighlighter highlighter)
    {
        _textArea = textArea;
        _document = document;
        _highlighter = highlighter;
        textArea.TextEntering += OnTextEntering;
        textArea.TextEntered += OnTextEntered;
        textArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    private TextDocument Buffer => _textArea.Document;

    /// <summary>The language's editing rules; none while they load.</summary>
    private LanguageConfiguration Configuration =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded
            ? loaded.Result.Configuration
            : LanguageConfiguration.None;

    public void Dispose()
    {
        _textArea.TextEntering -= OnTextEntering;
        _textArea.TextEntered -= OnTextEntered;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _autoClosed.Clear();
    }

    /// <summary>Backspace, from the editor's key handler, while the text area has the keyboard.</summary>
    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None)
            e.Handled = DeletePair();
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        _closing = null;
        int caret = _textArea.Caret.Offset;
        // Read-only text: AvaloniaEdit refuses the input itself.
        if (!_textArea.ReadOnlySectionProvider.CanInsert(caret))
            return;
        if (e.Text is "\n" or "\r" or "\r\n")
        {
            InsertNewLine();
            e.Handled = true;
        }
        else if (e.Text is [char typed])
        {
            if (!_textArea.Selection.IsEmpty)
                e.Handled = Surround(typed);
            else if (TypeOver(typed, caret))
                e.Handled = true;
            else
                _closing = PairToClose(typed, caret);
        }
    }

    /// <summary>
    /// Once a character is in: outdents its line, or closes its pair. As in VS Code, a line that outdents closes no
    /// pair.
    /// </summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        AutoClosingPair? closing = _closing;
        _closing = null;
        if (e.Text is not [_] || !_textArea.Selection.IsEmpty || Outdent())
            return;
        if (closing is { } pair)
            Close(pair);
    }

    /// <summary>Forgets the closing characters that the caret left, or that are gone.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        int caret = _textArea.Caret.Offset;
        _autoClosed.RemoveAll(pair => !pair.Encloses(caret, Buffer));
    }

    /// <summary>
    /// Typing the closing character that the editor typed, right before it, steps over it, as in VS Code: only a
    /// single character, and not a quote after a backslash, which escapes it.
    /// </summary>
    private bool TypeOver(char typed, int caret)
    {
        if (caret >= Buffer.TextLength || Buffer.GetCharAt(caret) != typed)
            return false;
        if (IsQuote(typed) && caret > 0 && Buffer.GetCharAt(caret - 1) == '\\')
            return false;
        if (!_autoClosed.Exists(pair => pair.Close.Offset == caret && pair.CloseText.Length == 1))
            return false;
        _textArea.Caret.Offset = caret + 1;
        _textArea.Caret.BringCaretToView();
        return true;
    }

    /// <summary>
    /// The pair to close when <paramref name="typed"/> goes in at <paramref name="caret"/>: the one with the longest
    /// opening that the character completes. As in VS Code, it closes only before whitespace, a character of
    /// <see cref="CloseBefore"/> or another closing character; a quote not right after a word; and a pair not closed
    /// in strings or comments only where the tokens before the caret and of the typed text are neither.
    /// </summary>
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
        string after = Buffer.GetText(caret, line.EndOffset - caret);
        if (after.Length > 0 && !CloseBefore.Contains(after[0]) && !StartsWithClosingOnly(after))
            return null;
        if (
            candidate.Open is ['\'' or '"']
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

    /// <summary>
    /// Whether <paramref name="after"/> starts with the closing characters of a pair, and not with the opening ones of
    /// another, as a quote does: VS Code's rule for closing a pair right before another closing character.
    /// </summary>
    private bool StartsWithClosingOnly(string after)
    {
        IReadOnlyList<AutoClosingPair> pairs = Configuration.AutoClosingPairs;
        return pairs.Any(pair => after.StartsWith(pair.Close, StringComparison.Ordinal))
            && !pairs.Any(pair => after.StartsWith(pair.Open, StringComparison.Ordinal));
    }

    /// <summary>Types the closing characters of <paramref name="pair"/> after the caret, with the opening ones.</summary>
    private void Close(AutoClosingPair pair)
    {
        int caret = _textArea.Caret.Offset;
        InTypedUndoStep(() => Buffer.Insert(caret, pair.Close));
        _textArea.Caret.Offset = caret;
        TextAnchor open = Buffer.CreateAnchor(caret - pair.Open.Length);
        TextAnchor close = Buffer.CreateAnchor(caret);
        // Text typed right before either one goes before it.
        open.MovementType = AnchorMovementType.AfterInsertion;
        close.MovementType = AnchorMovementType.AfterInsertion;
        _autoClosed.Add(new AutoClosed(open, close, pair.Close));
    }

    /// <summary>
    /// Backspace between an opening character and the closing one that the editor typed deletes both, as in VS Code:
    /// for pairs of single characters.
    /// </summary>
    private bool DeletePair()
    {
        int caret = _textArea.Caret.Offset;
        if (!_textArea.Selection.IsEmpty || caret == 0 || caret >= Buffer.TextLength)
            return false;
        string open = Buffer.GetCharAt(caret - 1).ToString();
        string close = Buffer.GetCharAt(caret).ToString();
        if (
            !Configuration.AutoClosingPairs.Any(pair => pair.Open == open && pair.Close == close)
            || !_autoClosed.Exists(pair => pair.Close.Offset == caret)
            || !CanChange(caret - 1, 2)
        )
            return false;
        Buffer.Remove(caret - 1, 2);
        return true;
    }

    /// <summary>
    /// Typing the opening character of a surrounding pair puts the pair around the selection, and keeps the text
    /// inside selected, as in VS Code: not around whitespace only, and a quote typed over a single quote replaces it.
    /// </summary>
    private bool Surround(char typed)
    {
        if (_textArea.Selection is not SimpleSelection selection)
            return false;
        CharacterPair? found = null;
        foreach (CharacterPair pair in Configuration.SurroundingPairs)
        {
            if (pair.Open is [char open] && open == typed)
                found = pair;
        }
        ISegment segment = selection.SurroundingSegment;
        string selected = Buffer.GetText(segment);
        if (
            found is not { } surrounding
            || string.IsNullOrWhiteSpace(selected)
            || (IsQuote(typed) && selected is [char quote] && IsQuote(quote))
            || !CanInsert(segment.Offset)
            || !CanInsert(segment.EndOffset)
        )
            return false;
        int start = segment.Offset;
        int end = segment.EndOffset;
        using (Buffer.RunUpdate())
        {
            Buffer.Insert(end, surrounding.Close);
            Buffer.Insert(start, surrounding.Open);
        }
        _textArea.Selection = Selection.Create(_textArea, start + 1, end + 1);
        _textArea.Caret.Offset = end + 1;
        _textArea.Caret.BringCaretToView();
        return true;
    }

    /// <summary>Enter: the new line, indented, in one step.</summary>
    private void InsertNewLine()
    {
        using (Buffer.RunUpdate())
        {
            // Removes only what is not read-only.
            if (!_textArea.Selection.IsEmpty)
                _textArea.Selection.ReplaceSelectionWithText("");
            int caret = _textArea.Caret.Offset;
            DocumentLine line = Buffer.GetLineByOffset(caret);
            NewLine newLine = NewLineAt(
                line,
                Buffer.GetText(line),
                caret - line.Offset,
                TextUtilities.GetNewLineFromDocument(Buffer, line.LineNumber)
            );
            int replaced = CanChange(caret, newLine.Replaced) ? newLine.Replaced : 0;
            Buffer.Replace(caret, replaced, newLine.Text);
            _textArea.Caret.Offset = caret + newLine.Caret;
        }
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// What Enter inserts at <paramref name="column"/> of a line, as VS Code decides it from the brackets and the
    /// indentation rules. Between a pair of brackets, the caret goes on a line indented one level more, and the
    /// closing bracket on the next line; after an opening bracket, the new line is indented one level more. Otherwise
    /// the indentation rules decide; without them, the new line keeps the line's indentation.
    /// </summary>
    private NewLine NewLineAt(DocumentLine line, string text, int column, string lineBreak)
    {
        LanguageConfiguration configuration = Configuration;
        Indenting indenting = new(_document.Indentation);
        string before = text[..column];
        string after = text[column..];
        string indentation = before[..LeadingWhitespace(before)];
        if (
            before.Length > 0
            && after.Length > 0
            && configuration.Brackets.Any(b => EndsWithOpening(before, b.Open) && StartsWithClosing(after, b.Close))
        )
        {
            string inner = lineBreak + indenting.Shift(indentation);
            return new NewLine(inner + lineBreak + indenting.Normalize(indentation), inner.Length, 0);
        }
        if (before.Length > 0 && configuration.Brackets.Any(b => EndsWithOpening(before, b.Open)))
        {
            string inner = lineBreak + indenting.Shift(indentation);
            return new NewLine(inner, inner.Length, 0);
        }
        if (configuration.IndentationRules is { } rules)
        {
            // The line before the new one is the text before the caret.
            Inherited? inherited = InheritedIndentation(
                rules,
                line.LineNumber + 1,
                honorIntentional: true,
                number => number == line.LineNumber ? before : LineText(number)
            );
            string afterEnter = indentation;
            if (inherited is { } action)
            {
                afterEnter = action.Increases ? indenting.Shift(action.Indentation) : action.Indentation;
                if (rules.Decreases(after))
                    afterEnter = indenting.Unshift(afterEnter);
            }
            // The new line replaces the indentation of the text it takes along; on a line of whitespace only, all of
            // the rest.
            int firstText = LeadingWhitespace(text);
            int replaced = firstText == text.Length ? text.Length - column : Math.Max(0, firstText - column);
            string inserted = lineBreak + indenting.Normalize(afterEnter);
            return new NewLine(inserted, inserted.Length, replaced);
        }
        string kept = lineBreak + indenting.Normalize(indentation);
        return new NewLine(kept, kept.Length, 0);
    }

    /// <summary>
    /// Outdents the caret's line when the character just typed made it match the language's decrease pattern, as VS
    /// Code does: to the indentation that the lines above give it, in the undo step of the character.
    /// </summary>
    /// <returns>Whether the line's indentation changed.</returns>
    private bool Outdent()
    {
        if (Configuration.IndentationRules is not { } rules)
            return false;
        int caret = _textArea.Caret.Offset;
        DocumentLine line = Buffer.GetLineByOffset(caret);
        string text = Buffer.GetText(line);
        int typed = caret - 1 - line.Offset;
        if (typed < 0 || rules.Decreases(text.Remove(typed, 1)) || !rules.Decreases(text))
            return false;
        if (InheritedIndentation(rules, line.LineNumber, honorIntentional: false, LineText) is not { } inherited)
            return false;
        var indenting = new Indenting(_document.Indentation);
        string indentation = indenting.Normalize(
            inherited.Increases ? inherited.Indentation : indenting.Unshift(inherited.Indentation)
        );
        int current = LeadingWhitespace(text);
        if (text[..current] == indentation || !CanChange(line.Offset, current))
            return false;
        InTypedUndoStep(() => Buffer.Replace(line.Offset, current, indentation));
        _textArea.Caret.Offset = caret + indentation.Length - current;
        return true;
    }

    /// <summary>
    /// The indentation that the lines above give line <paramref name="lineNumber"/>, and whether it is one level more:
    /// VS Code's <c>getInheritIndentForLine</c>. Null when no line above has text.
    /// </summary>
    /// <param name="rules">The language's indentation rules.</param>
    /// <param name="lineNumber">The line to indent.</param>
    /// <param name="honorIntentional">
    /// Whether a line above that neither increases nor decreases gives its own indentation, as the user may have
    /// indented it on purpose. Otherwise the lines above it are searched for one that does.
    /// </param>
    /// <param name="lineText">The text of a line by its number.</param>
    private static Inherited? InheritedIndentation(
        IndentationRules rules,
        int lineNumber,
        bool honorIntentional,
        Func<int, string> lineText
    )
    {
        if (lineNumber <= 1)
            return new Inherited("", false);
        // The first line with text above empty lines takes no indentation.
        for (int prior = lineNumber - 1; prior > 0; prior--)
        {
            if (lineText(prior).Length > 0)
                break;
            if (prior == 1)
                return new Inherited("", false);
        }
        int preceding = PrecedingLineWithText(lineNumber, lineText);
        if (preceding < 1)
            return null;
        string text = lineText(preceding);
        if (rules.Increases(text))
            return new Inherited(Leading(text), true);
        if (rules.Decreases(text) || preceding == 1 || honorIntentional)
            return new Inherited(Leading(text), false);
        for (int above = preceding - 1; above > 0; above--)
        {
            string aboveText = lineText(above);
            if (rules.Increases(aboveText))
                return new Inherited(Leading(aboveText), true);
            if (rules.Decreases(aboveText))
                return new Inherited(Leading(aboveText), false);
        }
        return new Inherited(Leading(lineText(1)), false);

        static string Leading(string line) => line[..LeadingWhitespace(line)];
    }

    /// <summary>
    /// The closest line above <paramref name="lineNumber"/> with more than whitespace, or else the first line; -1 for
    /// none: VS Code's <c>getPrecedingValidLine</c>.
    /// </summary>
    private static int PrecedingLineWithText(int lineNumber, Func<int, string> lineText)
    {
        int found = -1;
        for (int number = lineNumber - 1; number >= 1; number--)
        {
            if (!string.IsNullOrWhiteSpace(lineText(number)))
                return number;
            found = number;
        }
        return found;
    }

    /// <summary>Changes the text as part of the undo step of the character just typed.</summary>
    private void InTypedUndoStep(Action change)
    {
        Buffer.UndoStack.StartContinuedUndoGroup();
        try
        {
            change();
        }
        finally
        {
            Buffer.UndoStack.EndUndoGroup();
        }
    }

    private string LineText(int lineNumber) => Buffer.GetText(Buffer.GetLineByNumber(lineNumber));

    private bool CanInsert(int offset) => _textArea.ReadOnlySectionProvider.CanInsert(offset);

    /// <summary>Whether the whole range may be removed or replaced.</summary>
    private bool CanChange(int offset, int length) =>
        length == 0
            ? CanInsert(offset)
            : _textArea
                .ReadOnlySectionProvider.GetDeletableSegments(new TextSegment { StartOffset = offset, Length = length })
                .Sum(segment => segment.Length) == length;

    /// <summary>
    /// Whether <paramref name="text"/> ends with <paramref name="open"/> and whitespace, as VS Code matches an
    /// opening bracket before a new line: a bracket that starts with a word character, such as <c>begin</c>, starts a
    /// word.
    /// </summary>
    private static bool EndsWithOpening(string text, string open)
    {
        string trimmed = text.TrimEnd();
        int start = trimmed.Length - open.Length;
        return trimmed.EndsWith(open, StringComparison.Ordinal)
            && (!IsAsciiWordCharacter(open[0]) || start == 0 || !IsAsciiWordCharacter(trimmed[start - 1]));
    }

    /// <summary>Whether <paramref name="text"/> starts with whitespace and <paramref name="close"/>, likewise.</summary>
    private static bool StartsWithClosing(string text, string close)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith(close, StringComparison.Ordinal)
            && (
                !IsAsciiWordCharacter(close[^1])
                || close.Length == trimmed.Length
                || !IsAsciiWordCharacter(trimmed[close.Length])
            );
    }

    /// <summary>A word character of JavaScript's regular expressions, as in VS Code's <c>\b</c>.</summary>
    private static bool IsAsciiWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>The quotes of VS Code's quote rules.</summary>
    private static bool IsQuote(char c) => c is '\'' or '"' or '`';

    private static int LeadingWhitespace(string text)
    {
        int count = 0;
        while (count < text.Length && text[count] is ' ' or '\t')
            count++;
        return count;
    }

    /// <summary>What Enter inserts: the text, where the caret goes in it, and how much after the caret it replaces.</summary>
    private readonly record struct NewLine(string Text, int Caret, int Replaced);

    /// <summary>An indentation that lines above give, and whether it is one level more.</summary>
    private readonly record struct Inherited(string Indentation, bool Increases);

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
