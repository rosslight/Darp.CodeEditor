namespace Darp.CodeEditor;

/// <summary>
/// The line break that a document gives new text, one per document, as VS Code's text model has one with
/// <c>files.eol</c> at <c>auto</c>: detected from the text, else the system's (<see cref="CodeDocument.LineBreak"/>).
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
}
