using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The comment command of one editor's view, from the language's editing rules, as VS Code's
/// <c>lineCommentCommand.ts</c> toggles comments, in a step of its own: Ctrl+/ toggles line comments on the selected
/// lines, or else the block comment around them.
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
}
