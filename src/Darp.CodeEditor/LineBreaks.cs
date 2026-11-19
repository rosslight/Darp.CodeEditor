namespace Darp.CodeEditor;

/// <summary>
/// The line breaks of new text, as VS Code picks them: each document has one, <see cref="CodeDocument.LineBreak"/>,
/// which new lines, pasted text, snippets and replacements take.
/// </summary>
internal static class LineBreaks
{
    /// <summary>
    /// The operating system's line break, <see cref="Environment.NewLine"/>: a document's whose text has none, as with
    /// VS Code's default <c>files.eol</c>, <c>auto</c>, and the clipboard's on Windows. Tests set it before they create
    /// a document, to run as on another system.
    /// </summary>
    internal static string System { get; set; } = Environment.NewLine;

    /// <summary>
    /// The line break of a document with <paramref name="text"/>, as VS Code's text model detects it: <c>\r\n</c> when
    /// more than half of the line breaks are <c>\r\n</c> or <c>\r</c>, else <c>\n</c>, and <see cref="System"/> without
    /// any.
    /// </summary>
    public static string Detect(ReadOnlySpan<char> text)
    {
        int count = 0;
        int withCarriageReturn = 0;
        while (text.IndexOfAny('\r', '\n') is var i and >= 0)
        {
            count++;
            if (text[i] == '\r')
            {
                withCarriageReturn++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
            text = text[(i + 1)..];
        }
        if (count == 0)
            return System;
        return withCarriageReturn * 2 > count ? "\r\n" : "\n";
    }

    /// <summary>
    /// The line break of the text that copy and cut put on the clipboard from a document whose line break is
    /// <paramref name="lineBreak"/>: as in VS Code, <c>\r\n</c> on Windows, whose line break it is, else the
    /// document's.
    /// </summary>
    public static string OnClipboard(string lineBreak) => System == "\r\n" ? "\r\n" : lineBreak;
}
