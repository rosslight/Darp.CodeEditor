using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Language-agnostic editing helpers, driven by the editor's <see cref="LanguageConfiguration"/>: auto-indent on
/// Enter, outdent of a typed closing bracket, auto-closing pairs, typing over and deleting the closers the editor
/// inserted, surrounding a selection with a pair, and toggling comments with Ctrl+/. Tab and Shift+Tab indentation of
/// selected lines is AvaloniaEdit's.
/// </summary>
/// <remarks>
/// Typed characters still reach AvaloniaEdit and the other text input handlers, such as completion and signature
/// help; a closing character is added after the opening one went in, in the same undo step.
/// </remarks>
internal sealed class EditingHelpers
{
    /// <summary>Characters an auto-closing pair may be typed before, as VS Code's default "autoCloseBefore".</summary>
    private const string CloseBefore = ";:.,=}])>";

    private readonly CodeEditor _editor;

    /// <summary>Closers the editor inserted: typing the same character steps over them, Backspace removes the pair.</summary>
    private readonly List<TextAnchor> _autoClosed = [];

    public EditingHelpers(CodeEditor editor)
    {
        _editor = editor;
        TextArea textArea = editor.TextArea;
        textArea.TextEntering += OnTextEntering;
        textArea.TextEntered += OnTextEntered;
        textArea.Caret.PositionChanged += (_, _) => ForgetClosersOffCaretLine();
        editor.DocumentChanged += (_, _) => _autoClosed.Clear();
        editor.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private TextArea TextArea => _editor.TextArea;

    private TextDocument Document => _editor.Document;

    private LanguageConfiguration Configuration => _editor.LanguageConfiguration;

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // Read-only text: AvaloniaEdit refuses the input itself.
        if (!CanInsert(TextArea.Caret.Offset))
            return;
        if (e.Text is "\n" or "\r" or "\r\n")
        {
            InsertNewLine();
            e.Handled = true;
            return;
        }
        if (e.Text is not [char c])
            return;
        if (!TextArea.Selection.IsEmpty)
        {
            if (TextArea.Selection is SimpleSelection && FindPair(c) is { } pair && Surround(pair))
                e.Handled = true;
            return;
        }
        int caret = TextArea.Caret.Offset;
        if (caret < Document.TextLength && Document.GetCharAt(caret) == c && TakeCloserAt(caret))
        {
            TextArea.Caret.Offset = caret + 1;
            e.Handled = true;
            return;
        }
    }

    private void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not [char c] || !TextArea.Selection.IsEmpty)
            return;
        int caret = TextArea.Caret.Offset;
        if (!CanInsert(caret))
            return;
        foreach (BracketPair bracket in Configuration.Brackets)
        {
            if (bracket.Close == c)
                OutdentForClose(bracket, caret - 1);
        }
        if (FindPair(c) is not { } pair || !ShouldClose(pair, caret))
            return;
        InTypedUndoStep(() => Document.Insert(caret, pair.Close.ToString()));
        TextArea.Caret.Offset = caret;
        TextAnchor closer = Document.CreateAnchor(caret);
        closer.MovementType = AnchorMovementType.AfterInsertion;
        _autoClosed.Add(closer);
    }

    /// <summary>Changes the text as part of the undo step of the character just typed.</summary>
    private void InTypedUndoStep(Action change)
    {
        Document.UndoStack.StartContinuedUndoGroup();
        try
        {
            change();
        }
        finally
        {
            Document.UndoStack.EndUndoGroup();
        }
    }

    private bool CanInsert(int offset) => TextArea.ReadOnlySectionProvider.CanInsert(offset);

    /// <summary>Whether the whole range may be removed or replaced.</summary>
    private bool CanChange(int offset, int length) =>
        length == 0
            ? CanInsert(offset)
            : TextArea.ReadOnlySectionProvider.GetDeletableSegments(
                new TextSegment { StartOffset = offset, Length = length }
            )
                is var segments
                && segments.Sum(segment => segment.Length) == length;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && DeletePair())
            e.Handled = true;
        else if (IsToggleComment(e))
        {
            if (!_editor.IsReadOnly)
                ToggleComment();
            e.Handled = true;
        }
    }

    private static bool IsToggleComment(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(KeyModifiers.Control)
        && !e.KeyModifiers.HasFlag(KeyModifiers.Alt)
        && (e.KeySymbol == "/" || (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.Oem2 or Key.Divide));

    private AutoClosingPair? FindPair(char open)
    {
        foreach (AutoClosingPair pair in Configuration.AutoClosingPairs)
        {
            if (pair.Open == open)
                return pair;
        }
        return null;
    }

    /// <summary>
    /// Whether to add the closer after <paramref name="pair"/>'s opening character, just typed before
    /// <paramref name="caret"/>: before whitespace or a closing character only, not in excluded strings or comments,
    /// and a quote not right after a word, as in <c>don't</c>.
    /// </summary>
    private bool ShouldClose(AutoClosingPair pair, int caret)
    {
        if (caret < Document.TextLength)
        {
            char next = Document.GetCharAt(caret);
            if (!char.IsWhiteSpace(next) && !CloseBefore.Contains(next))
                return false;
        }
        int typed = caret - 1;
        if (pair.Open == pair.Close && typed > 0)
        {
            char previous = Document.GetCharAt(typed - 1);
            if (char.IsLetterOrDigit(previous) || previous == '_' || previous == pair.Open)
                return false;
        }
        // The context where the character went in, within its line and from the state at the line's start. Without
        // one, a closer could land inside a comment or string: add none.
        TokenKind kind = _editor.TokenKindBefore(typed);
        return kind != TokenKind.Unknown
            && !(pair.NotInString && kind == TokenKind.String)
            && !(pair.NotInComment && kind == TokenKind.Comment);
    }

    /// <summary>True, and forgets it, when the editor inserted the closer at <paramref name="offset"/>.</summary>
    private bool TakeCloserAt(int offset)
    {
        for (int i = 0; i < _autoClosed.Count; i++)
        {
            if (!_autoClosed[i].IsDeleted && _autoClosed[i].Offset == offset)
            {
                _autoClosed.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private void ForgetClosersOffCaretLine()
    {
        int line = TextArea.Caret.Line;
        _autoClosed.RemoveAll(closer => closer.IsDeleted || closer.Line != line);
    }

    /// <summary>Backspace right after an opening character whose auto-inserted closer follows: removes both.</summary>
    private bool DeletePair()
    {
        int caret = TextArea.Caret.Offset;
        if (!TextArea.Selection.IsEmpty || caret == 0 || caret >= Document.TextLength)
            return false;
        if (FindPair(Document.GetCharAt(caret - 1)) is not { } pair || Document.GetCharAt(caret) != pair.Close)
            return false;
        if (!CanChange(caret - 1, 2) || !TakeCloserAt(caret))
            return false;
        Document.Remove(caret - 1, 2);
        return true;
    }

    private bool Surround(AutoClosingPair pair)
    {
        ISegment selected = TextArea.Selection.SurroundingSegment;
        int start = selected.Offset;
        int end = selected.EndOffset;
        if (!CanInsert(start) || !CanInsert(end))
            return false;
        using (Document.RunUpdate())
        {
            Document.Insert(end, pair.Close.ToString());
            Document.Insert(start, pair.Open.ToString());
        }
        // The text inside the pair stays selected.
        TextArea.Selection = Selection.Create(TextArea, start + 1, end + 1);
        TextArea.Caret.Offset = end + 1;
        return true;
    }

    /// <summary>
    /// Enter: the new line keeps the indentation of the current one, one level more after an opening bracket. Between
    /// a bracket pair, the closing bracket moves to its own line below.
    /// </summary>
    private void InsertNewLine()
    {
        using (Document.RunUpdate())
        {
            // Removes only what is not read-only.
            if (!TextArea.Selection.IsEmpty)
                TextArea.Selection.ReplaceSelectionWithText("");
            int caret = TextArea.Caret.Offset;
            DocumentLine line = Document.GetLineByOffset(caret);
            string before = Document.GetText(line.Offset, caret - line.Offset);
            string after = Document.GetText(caret, line.EndOffset - caret);
            string indentation = before[..LeadingWhitespace(before)];
            string newLine = TextUtilities.GetNewLineFromDocument(Document, line.LineNumber);
            string unit = TextArea.Options.IndentationString;
            string insert = newLine + indentation;
            int caretInInsert = insert.Length;
            string trimmedBefore = before.TrimEnd();
            foreach (BracketPair bracket in Configuration.Brackets)
            {
                if (trimmedBefore.Length == 0 || trimmedBefore[^1] != bracket.Open)
                    continue;
                insert += unit;
                caretInInsert = insert.Length;
                if (after.TrimStart() is [char next, ..] && next == bracket.Close)
                    insert += newLine + indentation;
                break;
            }
            // The text moved to the new line loses its leading whitespace.
            int moved = CanChange(caret, LeadingWhitespace(after)) ? LeadingWhitespace(after) : 0;
            Document.Replace(caret, moved, insert);
            TextArea.Caret.Offset = caret + caretInInsert;
        }
        TextArea.Caret.BringCaretToView();
    }

    /// <summary>
    /// A closing bracket just typed at <paramref name="typed"/> on a line of only whitespace takes the indentation of
    /// its opening bracket's line.
    /// </summary>
    private void OutdentForClose(BracketPair bracket, int typed)
    {
        DocumentLine line = Document.GetLineByOffset(typed);
        string before = Document.GetText(line.Offset, typed - line.Offset);
        if (before.Length == 0 || LeadingWhitespace(before) != before.Length)
            return;
        if (BracketMatcher.FindOpen(Document, typed, bracket, _editor.TokenKindAt) is not { } open)
            return;
        DocumentLine openLine = Document.GetLineByOffset(open);
        string openText = Document.GetText(openLine.Offset, openLine.Length);
        string indentation = openText[..LeadingWhitespace(openText)];
        if (indentation == before || !CanChange(line.Offset, before.Length))
            return;
        InTypedUndoStep(() => Document.Replace(line.Offset, before.Length, indentation));
        TextArea.Caret.Offset = line.Offset + indentation.Length + 1;
    }

    /// <summary>
    /// Ctrl+/: comments the selected lines with the language's line comment at their smallest indentation, or removes
    /// it when all of them have one. Without a line comment, the block comment wraps the selection or the line.
    /// </summary>
    private void ToggleComment()
    {
        LanguageConfiguration configuration = Configuration;
        if (configuration.LineComment is { } token)
            ToggleLineComment(token);
        else if (configuration.BlockComment is { } block)
            ToggleBlockComment(block);
    }

    private void ToggleLineComment(string token)
    {
        (int first, int last) = SelectedLines();
        var lines = new List<(DocumentLine Line, string Text, int Indent)>();
        for (int number = first; number <= last; number++)
        {
            DocumentLine line = Document.GetLineByNumber(number);
            string text = Document.GetText(line.Offset, line.Length);
            if (!string.IsNullOrWhiteSpace(text))
                lines.Add((line, text, LeadingWhitespace(text)));
        }
        if (lines.Count == 0)
            return;
        bool commented = lines.TrueForAll(l => string.CompareOrdinal(l.Text, l.Indent, token, 0, token.Length) == 0);
        int column = lines.Min(l => l.Indent);
        if (!lines.TrueForAll(l => CanChange(l.Line.Offset, l.Line.Length)))
            return;
        using (Document.RunUpdate())
        {
            // Bottom up, so the offsets of the lines still to change stay valid.
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                (DocumentLine line, string text, int indent) = lines[i];
                if (!commented)
                {
                    Document.Insert(line.Offset + column, token + " ");
                    continue;
                }
                int length = token.Length;
                if (indent + length < text.Length && text[indent + length] == ' ')
                    length++;
                Document.Remove(line.Offset + indent, length);
            }
        }
    }

    private void ToggleBlockComment(BlockComment block)
    {
        int start;
        int end;
        if (TextArea.Selection.IsEmpty)
        {
            DocumentLine line = Document.GetLineByOffset(TextArea.Caret.Offset);
            string text = Document.GetText(line.Offset, line.Length);
            start = line.Offset + LeadingWhitespace(text);
            end = Math.Max(start, line.Offset + text.TrimEnd().Length);
        }
        else
        {
            ISegment selected = TextArea.Selection.SurroundingSegment;
            (start, end) = (selected.Offset, selected.EndOffset);
        }
        string content = Document.GetText(start, end - start);
        if (!CanChange(start, end - start))
            return;
        using (Document.RunUpdate())
        {
            if (
                content.Length >= block.Start.Length + block.End.Length
                && content.StartsWith(block.Start, StringComparison.Ordinal)
                && content.EndsWith(block.End, StringComparison.Ordinal)
            )
            {
                Document.Remove(end - block.End.Length, block.End.Length);
                Document.Remove(start, block.Start.Length);
            }
            else
            {
                Document.Insert(end, block.End);
                Document.Insert(start, block.Start);
            }
        }
    }

    /// <summary>The lines of the selection, without a last line it only touches at its start; or the caret's line.</summary>
    private (int First, int Last) SelectedLines()
    {
        if (TextArea.Selection.IsEmpty)
            return (TextArea.Caret.Line, TextArea.Caret.Line);
        ISegment selected = TextArea.Selection.SurroundingSegment;
        DocumentLine first = Document.GetLineByOffset(selected.Offset);
        DocumentLine last = Document.GetLineByOffset(selected.EndOffset);
        if (last != first && last.Offset == selected.EndOffset)
            last = last.PreviousLine;
        return (first.LineNumber, last.LineNumber);
    }

    private static int LeadingWhitespace(string text)
    {
        int count = 0;
        while (count < text.Length && text[count] is ' ' or '\t')
            count++;
        return count;
    }
}
