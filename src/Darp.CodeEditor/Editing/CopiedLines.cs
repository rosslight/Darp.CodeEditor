namespace Darp.CodeEditor.Editing;

/// <summary>
/// What the app's editors last put on the clipboard, and whether it was a whole line that copy or cut took without a
/// selection, as VS Code's <c>InMemoryClipboardMetadataManager</c> remembers it: a paste of the same text pastes it as
/// a line. Used on the app's UI thread only.
/// </summary>
internal static class CopiedLines
{
    /// <summary>The text last copied as a whole line, while it was the last text copied; null otherwise.</summary>
    private static string? s_line;

    /// <summary>Notes that the editor put <paramref name="text"/> on the clipboard.</summary>
    public static void Copied(string text, bool isLine) => s_line = isLine ? text : null;

    /// <summary>
    /// Whether <paramref name="text"/>, read from the clipboard, is the line that an editor copied last. Any other
    /// text there means the clipboard changed since, and the line is forgotten.
    /// </summary>
    public static bool IsLine(string text)
    {
        if (s_line == text)
            return true;
        s_line = null;
        return false;
    }
}
