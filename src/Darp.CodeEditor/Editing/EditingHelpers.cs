using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The editing helpers of one editor's view of a document, from the language's editing rules, as VS Code applies
/// them: Enter indents the new line, typing outdents a line that comes to match the decrease pattern, opening
/// characters bring their closing ones, which are typed over and deleted with them, and opening characters surround a
/// selection. Ctrl+/ toggles comments. Tab and Shift+Tab are AvaloniaEdit's, with the document's indentation.
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

    private readonly WordSelection _wordSelection;

    public EditingHelpers(TextArea textArea, CodeDocument document, BackgroundTextMateHighlighter highlighter)
    {
        _textArea = textArea;
        _document = document;
        _highlighter = highlighter;
        textArea.TextEntering += OnTextEntering;
        textArea.TextEntered += OnTextEntered;
        textArea.Caret.PositionChanged += OnCaretPositionChanged;
        _wordSelection = new WordSelection(textArea);
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
        _wordSelection.Dispose();
        _autoClosed.Clear();
    }

    /// <summary>
    /// Backspace, and deleting by words, from the editor's key handler, while the text area has the keyboard. Words go
    /// with Ctrl+Backspace and Ctrl+Delete, or Alt on macOS, the platform's key for moving by words.
    /// </summary>
    public void OnKeyDown(KeyEventArgs e)
    {
        KeyModifiers words =
            Application.Current?.PlatformSettings?.HotkeyConfiguration.WholeWordTextActionModifiers
            ?? KeyModifiers.Control;
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && TypingOffsets() is { } offsets)
            e.Handled = DeletePairs(offsets);
        else if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == words)
            e.Handled = DeleteWord(left: e.Key == Key.Back);
    }

    /// <summary>
    /// Deletes the selection, or by VS Code's <c>deleteWordLeft</c> and <c>deleteWordRight</c>
    /// (<see cref="WordRules"/>): before the caret, an empty pair that the editor closed goes as a whole first.
    /// </summary>
    /// <returns>Whether it handled the key: not in an empty rectangle selection, which AvaloniaEdit keeps.</returns>
    private bool DeleteWord(bool left)
    {
        if (_textArea.Selection is RectangleSelection { Length: 0 })
            return false;
        if (!_textArea.Selection.IsEmpty)
        {
            _textArea.Selection.ReplaceSelectionWithText(string.Empty);
            return true;
        }
        int caret = _textArea.Caret.Offset;
        if (left && DeletePairs([caret]))
            return true;
        if (
            (left ? WordRules.DeleteWordLeft(Buffer, caret) : WordRules.DeleteWordRight(Buffer, caret)) is { } range
            && CanChange(range.Start, range.Length)
        )
        {
            Buffer.Remove(range.Start, range.Length);
            _textArea.Caret.BringCaretToView();
        }
        return true;
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
            if (TypingOffsets() is not { } offsets)
                e.Handled = Surround(typed);
            else if (TypeOver(typed, offsets))
                e.Handled = true;
            else
                _closing = PairToClose(typed, offsets);
        }
    }

    /// <summary>
    /// Once a character is in: outdents its line, or closes its pair. As in VS Code, a line that outdents closes no
    /// pair. A rectangle selection outdents no line, so that it keeps its shape.
    /// </summary>
    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        AutoClosingPair? closing = _closing;
        _closing = null;
        if (e.Text is not [_] || TypingOffsets() is not { } offsets)
            return;
        if (_textArea.Selection is not RectangleSelection && Outdent())
            return;
        if (closing is { } pair)
            Close(pair, offsets);
    }

    /// <summary>
    /// Where typed text goes in: at the caret, or on every line of an empty rectangle selection, as AvaloniaEdit types
    /// there. Null for a selection with text, which typing replaces.
    /// </summary>
    private int[]? TypingOffsets() =>
        _textArea.Selection switch
        {
            RectangleSelection { Length: 0 } rectangle => [.. rectangle.Segments.Select(s => s.StartOffset)],
            { IsEmpty: true } => [_textArea.Caret.Offset],
            _ => null,
        };

    /// <summary>
    /// Moves the caret, or every line of an empty rectangle selection, by <paramref name="columns"/>, after the text
    /// changed or instead of a change.
    /// </summary>
    private void MoveTyping(int columns)
    {
        if (_textArea.Selection is RectangleSelection rectangle)
        {
            TextViewPosition start = rectangle.StartPosition;
            TextViewPosition end = rectangle.EndPosition;
            _textArea.Selection = new RectangleSelection(
                _textArea,
                new TextViewPosition(start.Line, start.Column + columns),
                new TextViewPosition(end.Line, end.Column + columns)
            );
            _textArea.Caret.Position = new TextViewPosition(end.Line, end.Column + columns);
        }
        else
            _textArea.Caret.Offset += columns;
        _textArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// Forgets the closing characters that the caret left, or every line of a rectangle selection, or that are gone.
    /// </summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        int[] positions = TypingOffsets() ?? [_textArea.Caret.Offset];
        _autoClosed.RemoveAll(pair => !Array.Exists(positions, position => pair.Encloses(position, Buffer)));
    }

    /// <summary>
    /// Typing the closing character that the editor typed, right before it, steps over it, as in VS Code: only a
    /// single character, and not a quote that a backslash escapes. In a rectangle selection, on every line or none.
    /// </summary>
    private bool TypeOver(char typed, int[] offsets)
    {
        if (!Array.TrueForAll(offsets, IsClosedBefore))
            return false;
        MoveTyping(1);
        return true;

        bool IsClosedBefore(int offset) =>
            offset < Buffer.TextLength
            && Buffer.GetCharAt(offset) == typed
            && !(IsQuote(typed) && IsEscaped(offset))
            && _autoClosed.Exists(pair => pair.Close.Offset == offset && pair.CloseText.Length == 1);
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

    /// <summary>
    /// The pair to close when <paramref name="typed"/> goes in at all of <paramref name="offsets"/>: the same one at
    /// each, as in VS Code with several cursors, or none.
    /// </summary>
    private AutoClosingPair? PairToClose(char typed, int[] offsets)
    {
        AutoClosingPair? found = PairToClose(typed, offsets[0]);
        return Array.TrueForAll(offsets, offset => PairToClose(typed, offset) == found) ? found : null;
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

    /// <summary>
    /// Types the closing characters of <paramref name="pair"/> at <paramref name="offsets"/>, right after the opening
    /// ones, in their undo step. The caret and the selection stay before them.
    /// </summary>
    private void Close(AutoClosingPair pair, int[] offsets)
    {
        Selection selection = _textArea.Selection;
        TextViewPosition caret = _textArea.Caret.Position;
        InTypedUndoStep(() =>
        {
            // From the end, so that the offsets still to close stay where they are.
            for (int i = offsets.Length - 1; i >= 0; i--)
                Buffer.Insert(offsets[i], pair.Close);
        });
        if (selection is RectangleSelection rectangle)
            _textArea.Selection = new RectangleSelection(_textArea, rectangle.StartPosition, rectangle.EndPosition);
        _textArea.Caret.Position = caret;
        for (int i = 0; i < offsets.Length; i++)
        {
            // Each line above got its closing characters too.
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
    /// Backspace between an opening character and the closing one that the editor typed deletes both, as in VS Code:
    /// for pairs of single characters. In a rectangle selection, on every line in one step, or none.
    /// </summary>
    private bool DeletePairs(int[] offsets)
    {
        if (!Array.TrueForAll(offsets, IsInEmptyPair))
            return false;
        using (Buffer.RunUpdate())
        {
            for (int i = offsets.Length - 1; i >= 0; i--)
                Buffer.Remove(offsets[i] - 1, 2);
        }
        if (_textArea.Selection is RectangleSelection)
            MoveTyping(-1);
        return true;

        bool IsInEmptyPair(int offset)
        {
            if (offset == 0 || offset >= Buffer.TextLength)
                return false;
            string open = Buffer.GetCharAt(offset - 1).ToString();
            string close = Buffer.GetCharAt(offset).ToString();
            return Configuration.AutoClosingPairs.Any(pair => pair.Open == open && pair.Close == close)
                && _autoClosed.Exists(pair => pair.Close.Offset == offset)
                && CanChange(offset - 1, 2);
        }
    }

    /// <summary>
    /// Typing the opening character of a surrounding pair puts the pair around the selection, and keeps the text
    /// inside selected, as in VS Code: not around whitespace only, and a quote typed over a single quote replaces it.
    /// In a rectangle selection, around every line's part in one step, if each has more than whitespace.
    /// </summary>
    private bool Surround(char typed)
    {
        CharacterPair? found = null;
        foreach (CharacterPair pair in Configuration.SurroundingPairs)
        {
            if (pair.Open is [char open] && open == typed)
                found = pair;
        }
        Selection selection = _textArea.Selection;
        SelectionSegment[] segments = [.. selection.Segments];
        if (found is not { } surrounding || !Array.TrueForAll(segments, CanSurround))
            return false;
        using (Buffer.RunUpdate())
        {
            for (int i = segments.Length - 1; i >= 0; i--)
            {
                Buffer.Insert(segments[i].EndOffset, surrounding.Close);
                Buffer.Insert(segments[i].StartOffset, surrounding.Open);
            }
        }
        if (selection is RectangleSelection rectangle)
        {
            TextViewPosition start = rectangle.StartPosition;
            TextViewPosition end = rectangle.EndPosition;
            _textArea.Selection = new RectangleSelection(
                _textArea,
                new TextViewPosition(start.Line, start.Column + 1),
                new TextViewPosition(end.Line, end.Column + 1)
            );
            _textArea.Caret.Position = new TextViewPosition(end.Line, end.Column + 1);
        }
        else
        {
            int start = segments[0].StartOffset;
            int end = segments[0].EndOffset;
            _textArea.Selection = Selection.Create(_textArea, start + 1, end + 1);
            _textArea.Caret.Offset = end + 1;
        }
        _textArea.Caret.BringCaretToView();
        return true;

        bool CanSurround(SelectionSegment segment)
        {
            string selected = Buffer.GetText(segment);
            return !string.IsNullOrWhiteSpace(selected)
                && !(IsQuote(typed) && selected is [char quote] && IsQuote(quote))
                && CanInsert(segment.StartOffset)
                && CanInsert(segment.EndOffset);
        }
    }

    /// <summary>Enter: the new line, indented, in one step.</summary>
    private void InsertNewLine()
    {
        using (Buffer.RunUpdate())
        {
            // Removes only what is not read-only.
            if (!_textArea.Selection.IsEmpty)
                _textArea.Selection.ReplaceSelectionWithText("");
            // Also an empty rectangle selection ends: the new line goes in at the caret.
            _textArea.ClearSelection();
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
        if (configuration.IndentationRules is { } rules && IndentByRules(rules, line, before, after) is { } byRules)
        {
            // The new line replaces the indentation of the text it takes along; on a line of whitespace only, all of
            // the rest.
            int firstText = LeadingWhitespace(text);
            int replaced = firstText == text.Length ? text.Length - column : Math.Max(0, firstText - column);
            string inserted = lineBreak + indenting.Normalize(byRules);
            return new NewLine(inserted, inserted.Length, replaced);
        }
        string kept = lineBreak + indenting.Normalize(indentation);
        return new NewLine(kept, kept.Length, 0);
    }

    /// <summary>
    /// The indentation of a new line after <paramref name="before"/>, the text before the caret, by the indentation
    /// rules: VS Code's <c>getIndentForEnter</c>. Null when matching runs out of time.
    /// </summary>
    private string? IndentByRules(IndentationRules rules, DocumentLine line, string before, string after)
    {
        var indenting = new Indenting(_document.Indentation);
        IndentationMatching matching = rules.StartMatching();
        try
        {
            // The line before the new one is the text before the caret.
            Inherited? inherited = InheritedIndentation(
                matching,
                line.LineNumber + 1,
                honorIntentional: true,
                number => number == line.LineNumber ? before : LineText(number)
            );
            if (inherited is not { } action)
                return before[..LeadingWhitespace(before)];
            string indentation = action.Increases ? indenting.Shift(action.Indentation) : action.Indentation;
            return matching.Decreases(after) ? indenting.Unshift(indentation) : indentation;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Outdents the caret's line when the character just typed made it match the language's decrease pattern, as VS
    /// Code does: to the indentation that the lines above give it, in the undo step of the character. When matching
    /// runs out of time, the line stays as typed.
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
        if (typed < 0 || OutdentedIndentation(rules, line.LineNumber, text, typed) is not { } outdented)
            return false;
        string indentation = new Indenting(_document.Indentation).Normalize(outdented);
        int current = LeadingWhitespace(text);
        if (text[..current] == indentation || !CanChange(line.Offset, current))
            return false;
        InTypedUndoStep(() => Buffer.Replace(line.Offset, current, indentation));
        _textArea.Caret.Offset = caret + indentation.Length - current;
        return true;
    }

    /// <summary>
    /// The indentation of line <paramref name="lineNumber"/>, if the character typed at <paramref name="typed"/> made
    /// its <paramref name="text"/> match the decrease pattern: VS Code's <c>getIndentActionForType</c>. Null when it
    /// didn't, when no line above has text, or when matching runs out of time.
    /// </summary>
    private string? OutdentedIndentation(IndentationRules rules, int lineNumber, string text, int typed)
    {
        IndentationMatching matching = rules.StartMatching();
        try
        {
            if (matching.Decreases(text.Remove(typed, 1)) || !matching.Decreases(text))
                return null;
            if (InheritedIndentation(matching, lineNumber, honorIntentional: false, LineText) is not { } inherited)
                return null;
            return inherited.Increases
                ? inherited.Indentation
                : new Indenting(_document.Indentation).Unshift(inherited.Indentation);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indentation that the lines above give line <paramref name="lineNumber"/>, and whether it is one level more:
    /// VS Code's <c>getInheritIndentForLine</c>. Null when no line above has text.
    /// </summary>
    /// <param name="matching">The language's indentation rules, within the time of one indentation.</param>
    /// <param name="lineNumber">The line to indent.</param>
    /// <param name="honorIntentional">
    /// Whether a line above that neither increases nor decreases gives its own indentation, as the user may have
    /// indented it on purpose. Otherwise the lines above it are searched for one that does.
    /// </param>
    /// <param name="lineText">The text of a line by its number.</param>
    internal static Inherited? InheritedIndentation(
        IndentationMatching matching,
        int lineNumber,
        bool honorIntentional,
        Func<int, string> lineText
    )
    {
        if (lineNumber <= 1)
            return new Inherited("", false);
        // Every line read counts against the time, also the empty ones that the scans skip.
        string Read(int number) => matching.Read(lineText, number);
        // The first line with text above empty lines takes no indentation.
        for (int prior = lineNumber - 1; prior > 0; prior--)
        {
            if (Read(prior).Length > 0)
                break;
            if (prior == 1)
                return new Inherited("", false);
        }
        int preceding = PrecedingLineWithText(lineNumber, Read);
        if (preceding < 1)
            return null;
        string text = Read(preceding);
        if (matching.Increases(text))
            return new Inherited(Leading(text), true);
        if (matching.Decreases(text) || preceding == 1 || honorIntentional)
            return new Inherited(Leading(text), false);
        for (int above = preceding - 1; above > 0; above--)
        {
            string aboveText = Read(above);
            if (matching.Increases(aboveText))
                return new Inherited(Leading(aboveText), true);
            if (matching.Decreases(aboveText))
                return new Inherited(Leading(aboveText), false);
        }
        return new Inherited(Leading(Read(1)), false);

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

    /// <summary>
    /// Toggles line comments on the selected lines, or the caret's, with the language's line comment, or else with its
    /// block comment around them.
    /// </summary>
    public void ToggleComment()
    {
        LanguageConfiguration configuration = Configuration;
        (int first, int last) = SelectedLines();
        if (configuration.LineComment is { } token)
            ToggleLineComment(token, first, last);
        else if (configuration.BlockComment is { } block)
            ToggleBlockComment(block, first, last);
    }

    /// <summary>
    /// Comments the lines at their smallest indentation, by its width in columns, with a space after the token, or
    /// uncomments them when every line is commented, as in VS Code. Lines of whitespace only take no part, unless all
    /// lines are.
    /// </summary>
    private void ToggleLineComment(string token, int first, int last)
    {
        var lines = new List<(DocumentLine Line, string Text, int Indent)>();
        for (int number = first; number <= last; number++)
        {
            DocumentLine line = Buffer.GetLineByNumber(number);
            string text = Buffer.GetText(line);
            lines.Add((line, text, LeadingWhitespace(text)));
        }
        List<(DocumentLine Line, string Text, int Indent)> withText = lines.FindAll(l => l.Indent < l.Text.Length);
        bool blank = withText.Count == 0;
        List<(DocumentLine Line, string Text, int Indent)> changed = blank ? lines : withText;
        bool uncomment =
            !blank && withText.TrueForAll(l => string.CompareOrdinal(l.Text, l.Indent, token, 0, token.Length) == 0);
        if (!changed.TrueForAll(l => CanChange(l.Line.Offset, l.Line.Length)))
            return;
        // The smallest indentation by its width, so that tabs and spaces line up.
        var indenting = new Indenting(_document.Indentation);
        int column = changed.Min(l => indenting.Width(l.Text.AsSpan(0, l.Indent)));
        var edits = new List<(int Offset, int Length, string Text)>(changed.Count);
        foreach ((DocumentLine line, string text, int indent) in changed)
        {
            if (!uncomment)
            {
                edits.Add((line.Offset + (blank ? indent : indenting.OffsetOf(text, column)), 0, token + " "));
                continue;
            }
            int end = indent + token.Length;
            edits.Add(
                (line.Offset + indent, end < text.Length && text[end] == ' ' ? token.Length + 1 : token.Length, "")
            );
        }
        ReplaceInOneChange(edits);
    }

    /// <summary>
    /// Makes <paramref name="edits"/>, in order and apart, as one change of the text from the first to the last, with a
    /// map of them, so that anchors such as the caret move as they would for each edit. AvaloniaEdit raises a change
    /// per edit also within one update, so the editor's work per change, such as the highlighter's and the view's,
    /// happens once instead of once per edit.
    /// </summary>
    private void ReplaceInOneChange(List<(int Offset, int Length, string Text)> edits)
    {
        int start = edits[0].Offset;
        string text = Buffer.GetText(start, edits[^1].Offset + edits[^1].Length - start);
        var replaced = new StringBuilder(text.Length);
        var map = new OffsetChangeMap();
        int copied = 0;
        int delta = 0;
        foreach ((int offset, int length, string inserted) in edits)
        {
            replaced.Append(text, copied, offset - start - copied).Append(inserted);
            map.Add(new OffsetChangeMapEntry(offset + delta, length, inserted.Length));
            delta += inserted.Length - length;
            copied = offset - start + length;
        }
        replaced.Append(text, copied, text.Length - copied);
        Buffer.Replace(start, text.Length, new StringTextSource(replaced.ToString()), map);
    }

    /// <summary>
    /// Puts the block comment around the lines, from their first text to the end of the last, with a space inside
    /// each token, or removes it when it is around them already, as VS Code does for languages without a line
    /// comment. On a line of whitespace only, the caret goes inside an empty comment.
    /// </summary>
    private void ToggleBlockComment(CharacterPair block, int first, int last)
    {
        DocumentLine firstLine = Buffer.GetLineByNumber(first);
        int start = firstLine.Offset + LeadingWhitespace(Buffer.GetText(firstLine));
        int end = Buffer.GetLineByNumber(last).EndOffset;
        if (!CanChange(start, end - start))
            return;
        string content = Buffer.GetText(start, end - start).TrimEnd();
        (string open, string close) = block;
        if (
            content.Length >= open.Length + close.Length
            && content.StartsWith(open, StringComparison.Ordinal)
            && content.EndsWith(close, StringComparison.Ordinal)
        )
        {
            int openLength =
                content.Length > open.Length && content[open.Length] == ' ' ? open.Length + 1 : open.Length;
            int closeStart = content.Length - close.Length;
            if (closeStart > openLength && content[closeStart - 1] == ' ')
                closeStart--;
            using (Buffer.RunUpdate())
            {
                Buffer.Remove(start + closeStart, content.Length - closeStart);
                Buffer.Remove(start, openLength);
            }
        }
        else if (start == end)
        {
            Buffer.Insert(start, open + "  " + close);
            _textArea.Caret.Offset = start + open.Length + 1;
        }
        else
        {
            using (Buffer.RunUpdate())
            {
                Buffer.Insert(end, " " + close);
                Buffer.Insert(start, open + " ");
            }
        }
    }

    /// <summary>The lines of the selection, without a last line it only touches at its start; or the caret's line.</summary>
    private (int First, int Last) SelectedLines()
    {
        if (_textArea.Selection.IsEmpty)
            return (_textArea.Caret.Line, _textArea.Caret.Line);
        ISegment selected = _textArea.Selection.SurroundingSegment;
        DocumentLine first = Buffer.GetLineByOffset(selected.Offset);
        DocumentLine last = Buffer.GetLineByOffset(selected.EndOffset);
        if (last != first && last.Offset == selected.EndOffset)
            last = last.PreviousLine;
        return (first.LineNumber, last.LineNumber);
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
    internal readonly record struct Inherited(string Indentation, bool Increases);

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
