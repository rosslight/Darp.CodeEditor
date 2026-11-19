using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The comment commands of one editor's view, from the language's editing rules, as VS Code's
/// <c>lineCommentCommand.ts</c> and <c>blockCommentCommand.ts</c> toggle them, each in a step of its own: Ctrl+/ toggles
/// line comments on the selected lines, or else the block comment around them, and Shift+Alt+A toggles the block
/// comment around the selection.
/// </summary>
internal sealed class Comments : IEditorFeature
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly TextChanges _changes;

    public Comments(TextArea area, CodeDocument document, TextChanges changes)
    {
        _area = area;
        _document = document;
        _changes = changes;
    }

    private TextDocument Buffer => _area.Document;

    private LanguageConfiguration Configuration => LanguageConfiguration.Of(_document.Language);

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds needs = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.CommentLine, ToggleComment, needs);
        commands.Add(CodeEditorCommands.BlockComment, ToggleBlockComment, needs);
    }

    /// <summary>
    /// Ctrl+/: toggles line comments on the selected lines, or the caret's, with the language's line comment, or else
    /// with its block comment around them.
    /// </summary>
    /// <returns>Whether it toggled: the language has a comment, and the lines can change.</returns>
    private bool ToggleComment()
    {
        LanguageConfiguration configuration = Configuration;
        (int first, int last) = SelectedLines();
        bool toggled = false;
        if (configuration.LineComment is { } token)
            _changes.Run(EditStep.Own, () => toggled = ToggleLineComment(token, first, last));
        else if (configuration.BlockComment is { } block)
            _changes.Run(EditStep.Own, () => toggled = ToggleBlockCommentOnLines(block, first, last));
        return toggled;
    }

    /// <summary>
    /// Comments the lines at their smallest indentation, by its width in columns, with a space after the token, or
    /// uncomments them when every line is commented. Lines of whitespace only take no part, unless all lines are.
    /// </summary>
    private bool ToggleLineComment(string token, int first, int last)
    {
        var lines = new List<(DocumentLine Line, string Text, int Indent)>();
        for (int number = first; number <= last; number++)
        {
            DocumentLine line = Buffer.GetLineByNumber(number);
            string text = Buffer.GetText(line);
            lines.Add((line, text, IndentRules.Leading(text)));
        }
        List<(DocumentLine Line, string Text, int Indent)> withText = lines.FindAll(l => l.Indent < l.Text.Length);
        bool blank = withText.Count == 0;
        List<(DocumentLine Line, string Text, int Indent)> changed = blank ? lines : withText;
        bool uncomment =
            !blank && withText.TrueForAll(l => string.CompareOrdinal(l.Text, l.Indent, token, 0, token.Length) == 0);
        // The smallest indentation by its width, so that tabs and spaces line up.
        var indenting = new Indenting(_document.Indentation);
        int column = changed.Min(l => indenting.Width(l.Text.AsSpan(0, l.Indent)));
        var edits = new List<TextEdit>(changed.Count);
        foreach ((DocumentLine line, string text, int indent) in changed)
        {
            if (!uncomment)
            {
                edits.Add(
                    new TextEdit(line.Offset + (blank ? indent : indenting.OffsetOf(text, column)), 0, token + " ")
                );
                continue;
            }
            int end = indent + token.Length;
            edits.Add(
                new TextEdit(
                    line.Offset + indent,
                    end < text.Length && text[end] == ' ' ? token.Length + 1 : token.Length,
                    ""
                )
            );
        }
        return _changes.Apply(edits);
    }

    /// <summary>
    /// Puts the block comment around the lines, from their first text to the end of the last, with a space inside
    /// each token, or removes it when it is around them already, for languages without a line comment. On a line of
    /// whitespace only, the caret goes inside an empty comment.
    /// </summary>
    private bool ToggleBlockCommentOnLines(CharacterPair block, int first, int last)
    {
        DocumentLine firstLine = Buffer.GetLineByNumber(first);
        int start = firstLine.Offset + IndentRules.Leading(Buffer.GetText(firstLine));
        int end = Buffer.GetLineByNumber(last).EndOffset;
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
            return _changes.Apply([
                new TextEdit(start, openLength, ""),
                new TextEdit(start + closeStart, content.Length - closeStart, ""),
            ]);
        }
        if (start == end)
            return _changes.Apply(
                [new TextEdit(start, 0, open + "  " + close)],
                new TextSelection(start + open.Length + 1)
            );
        return _changes.Apply([new TextEdit(start, 0, open + " "), new TextEdit(end, 0, " " + close)]);
    }

    /// <summary>
    /// Shift+Alt+A: removes the block comment that the selection is in, from the start token before its start, by the
    /// token's length after it at most, to the end token after its end, with the spaces inside them; or, when another
    /// end token is between them, or there are none, puts the block comment around the selection, with a space inside
    /// each token, and keeps the commented text selected. Without a selection, an empty comment goes in at the caret,
    /// and the caret inside it.
    /// </summary>
    /// <returns>Whether the language has a block comment.</returns>
    private bool ToggleBlockComment()
    {
        if (Configuration.BlockComment is not (var open, var close))
            return false;
        TextRange selected = _changes.Selection.Range;
        DocumentLine startLine = Buffer.GetLineByOffset(selected.Start);
        DocumentLine endLine = Buffer.GetLineByOffset(selected.End);
        string startText = Buffer.GetText(startLine);
        string endText = Buffer.GetText(endLine);
        int openAt = LastIndexOf(startText, open, selected.Start - startLine.Offset + open.Length);
        int closeAt = endText.IndexOf(
            close,
            Math.Clamp(selected.End - endLine.Offset - close.Length, 0, endText.Length),
            StringComparison.Ordinal
        );
        if (
            openAt >= 0
            && closeAt >= 0
            && (
                startLine == endLine
                    ? Between(startText, openAt + open.Length, closeAt).Contains(close, StringComparison.Ordinal)
                    : startText[(openAt + open.Length)..].Contains(close, StringComparison.Ordinal)
                        || endText[..closeAt].Contains(close, StringComparison.Ordinal)
            )
        )
            openAt = closeAt = -1;
        List<TextEdit> edits;
        TextSelection after;
        if (openAt >= 0 && closeAt >= 0)
        {
            int openLength =
                openAt + open.Length < startText.Length && startText[openAt + open.Length] == ' '
                    ? open.Length + 1
                    : open.Length;
            int closeLength = close.Length;
            if (closeAt > 0 && endText[closeAt - 1] == ' ')
            {
                closeAt--;
                closeLength++;
            }
            int openStart = startLine.Offset + openAt;
            int closeStart = endLine.Offset + closeAt;
            // Tokens that meet or overlap, as in an empty comment whose one space both take, go as one.
            if (closeStart > openStart + openLength)
            {
                edits = [new TextEdit(openStart, openLength, ""), new TextEdit(closeStart, closeLength, "")];
                after = new TextSelection(openStart, closeStart - openLength);
            }
            else
            {
                int end = Math.Max(openStart + openLength, closeStart + closeLength);
                edits = [new TextEdit(Math.Min(openStart, closeStart), end - Math.Min(openStart, closeStart), "")];
                after = new TextSelection(Math.Min(openStart, closeStart));
            }
        }
        else if (selected.Length == 0)
        {
            edits = [new TextEdit(selected.Start, 0, open + "  " + close)];
            after = new TextSelection(selected.Start + open.Length + 1);
        }
        else
        {
            edits = [new TextEdit(selected.Start, 0, open + " "), new TextEdit(selected.End, 0, " " + close)];
            after = new TextSelection(selected.Start + open.Length + 1, selected.End + open.Length + 1);
        }
        _changes.Run(EditStep.Own, () => _changes.Apply(edits, after));
        return true;
    }

    /// <summary>The lines of the selection, without a last line it only touches at its start; or the caret's line.</summary>
    private (int First, int Last) SelectedLines()
    {
        if (_area.Selection.IsEmpty)
            return (_area.Caret.Line, _area.Caret.Line);
        ISegment selected = _area.Selection.SurroundingSegment;
        DocumentLine first = Buffer.GetLineByOffset(selected.Offset);
        DocumentLine last = Buffer.GetLineByOffset(selected.EndOffset);
        if (last != first && last.Offset == selected.EndOffset)
            last = last.PreviousLine;
        return (first.LineNumber, last.LineNumber);
    }

    /// <summary>The last <paramref name="value"/> in <paramref name="text"/> that starts at <paramref name="from"/> or before; -1 for none.</summary>
    private static int LastIndexOf(string text, string value, int from)
    {
        for (int index = Math.Min(from, text.Length - value.Length); index >= 0; index--)
        {
            if (string.CompareOrdinal(text, index, value, 0, value.Length) == 0)
                return index;
        }
        return -1;
    }

    /// <summary>The text between two indexes in either order, within <paramref name="text"/>.</summary>
    private static string Between(string text, int from, int to) =>
        text[Math.Clamp(Math.Min(from, to), 0, text.Length)..Math.Clamp(Math.Max(from, to), 0, text.Length)];
}
