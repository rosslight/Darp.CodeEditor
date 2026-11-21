using System.Text;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>Ctrl+/ in one editor's view, from the language's editing rules, as VS Code toggles comments.</summary>
internal sealed class EditingHelpers : IEditorFeature
{
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;

    public EditingHelpers(TextArea textArea, CodeDocument document)
    {
        _textArea = textArea;
        _document = document;
    }

    private TextDocument Buffer => _textArea.Document;

    /// <summary>The language's editing rules; none while they load.</summary>
    private LanguageConfiguration Configuration => LanguageConfiguration.Of(_document.Language);

    public void Dispose() { }

    public void AddCommands(CommandTable commands) =>
        commands.Add(
            CodeEditorCommands.CommentLine,
            ToggleComment,
            CommandNeeds.TextFocus | CommandNeeds.Writable,
            () => Configuration is { LineComment: not null } or { BlockComment: not null }
        );

    /// <summary>
    /// Toggles line comments on the selected lines, or the caret's, with the language's line comment, or else
    /// with its block comment around them.
    /// </summary>
    private void ToggleComment()
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

    private bool CanInsert(int offset) => _textArea.ReadOnlySectionProvider.CanInsert(offset);

    /// <summary>Whether the whole range may be removed or replaced.</summary>
    private bool CanChange(int offset, int length) =>
        length == 0
            ? CanInsert(offset)
            : _textArea
                .ReadOnlySectionProvider.GetDeletableSegments(new TextSegment { StartOffset = offset, Length = length })
                .Sum(segment => segment.Length) == length;

    private static int LeadingWhitespace(string text) => IndentRules.Leading(text);
}
