using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor;

/// <summary>
/// The line break that a document gives new text, one per document, as VS Code's text model has one with
/// <c>files.eol</c> at <c>auto</c>: detected from the text, else the system's (<see cref="CodeDocument.LineBreak"/>);
/// and the edits that put it in next to the document's other line breaks.
/// </summary>
internal static class LineBreaks
{
    /// <summary>
    /// The system's line break, <see cref="Environment.NewLine"/>. Tests set it before they create a document, to run as
    /// on another system.
    /// </summary>
    internal static string System { get; set; } = Environment.NewLine;

    /// <summary>
    /// <c>\r\n</c> when more than half of the line breaks of <paramref name="text"/> are <c>\r\n</c> or <c>\r</c>, else
    /// <c>\n</c>; <see cref="System"/> for a text without any.
    /// </summary>
    public static string Detect(ReadOnlySpan<char> text)
    {
        int lineBreaks = 0;
        int withCarriageReturn = 0;
        for (int i = text.IndexOfAny('\r', '\n'); i >= 0; i = text.IndexOfAny('\r', '\n'))
        {
            lineBreaks++;
            if (text[i] == '\r')
            {
                withCarriageReturn++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
            text = text[(i + 1)..];
        }
        if (lineBreaks == 0)
            return System;
        return withCarriageReturn * 2 > lineBreaks ? "\r\n" : "\n";
    }

    /// <summary>
    /// The line break of text that copy and cut take from a document whose line break is <paramref name="lineBreak"/>:
    /// <c>\r\n</c> where the system's is, as on Windows, else the document's.
    /// </summary>
    public static string OnClipboard(string lineBreak) => System == "\r\n" ? "\r\n" : lineBreak;

    /// <summary>
    /// <paramref name="edits"/> of <paramref name="text"/>, in order and apart, so that none puts an LF right after a
    /// lone CR, or a CR right before a lone LF, which would make one line break of two and lose a line: the CR goes
    /// along with the edit, as <paramref name="lineBreak"/>.
    /// </summary>
    public static IReadOnlyList<TextEdit> Apart(ITextSource text, IReadOnlyList<TextEdit> edits, string lineBreak)
    {
        TextEdit[] apart = [.. edits];
        // By runs of edits that each start where the one before ends, with no text between them.
        for (int first = 0, last = 0; first < apart.Length; first = ++last)
        {
            while (last + 1 < apart.Length && apart[last + 1].Offset == End(apart[last]))
                last++;
            (int start, int end) = (apart[first].Offset, End(apart[last]));
            string inserted = string.Concat(apart[first..(last + 1)].Select(edit => edit.Text));
            char after = inserted.Length > 0 ? inserted[0] : CharAt(end);
            if (CharAt(start - 1) == '\r' && CharAt(start) != '\n' && after == '\n')
                apart[first] = new TextEdit(start - 1, apart[first].Length + 1, lineBreak + apart[first].Text);
            if (inserted.EndsWith('\r') && CharAt(end) == '\n' && CharAt(end - 1) != '\r')
            {
                int ending = Array.FindLastIndex(apart, last, last - first + 1, edit => edit.Text.Length > 0);
                apart[ending] = apart[ending] with { Text = apart[ending].Text[..^1] + lineBreak };
            }
        }
        return apart;

        char CharAt(int offset) => offset >= 0 && offset < text.TextLength ? text.GetCharAt(offset) : '\0';
        static int End(TextEdit edit) => edit.Offset + edit.Length;
    }
}
