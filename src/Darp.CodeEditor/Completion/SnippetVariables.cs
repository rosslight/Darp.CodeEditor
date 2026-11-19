using System.Globalization;
using System.Text;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The values of the snippet variables that need no editor: the file's, the time's, the clipboard's and the random ones,
/// as VS Code gives them, and the indentation of selected text. Names and months are English, as in VS Code's English
/// version.
/// </summary>
internal static class SnippetVariables
{
    /// <summary>
    /// The variable of the document's file, from its path: null for another variable, and without a path. Without a
    /// workspace, the relative path is the whole path, as in VS Code.
    /// </summary>
    public static string? OfFile(string name, string? path)
    {
        if (path is null)
            return null;
        string file = Path.GetFileName(path);
        string directory = Path.GetDirectoryName(path) ?? "";
        int extension = file.LastIndexOf('.');
        return name switch
        {
            "TM_FILENAME" => file,
            // As in VS Code: a name that starts with its only dot, such as ".gitignore", is its own base.
            "TM_FILENAME_BASE" => extension > 0 ? file[..extension] : file,
            "TM_DIRECTORY" => directory,
            "TM_DIRECTORY_BASE" => Path.GetFileName(directory),
            "TM_FILEPATH" or "RELATIVE_FILEPATH" => path,
            _ => null,
        };
    }

    /// <summary>The variable of the time <paramref name="now"/> in <paramref name="zone"/>; null for another variable.</summary>
    public static string? OfTime(string name, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTimeFormatInfo english = CultureInfo.InvariantCulture.DateTimeFormat;
        return name switch
        {
            "CURRENT_YEAR" => Invariant(now.Year),
            "CURRENT_YEAR_SHORT" => Invariant(now.Year)[^2..],
            "CURRENT_MONTH" => Invariant(now.Month, "D2"),
            "CURRENT_DATE" => Invariant(now.Day, "D2"),
            "CURRENT_HOUR" => Invariant(now.Hour, "D2"),
            "CURRENT_MINUTE" => Invariant(now.Minute, "D2"),
            "CURRENT_SECOND" => Invariant(now.Second, "D2"),
            "CURRENT_MILLISECOND" => Invariant(now.Millisecond, "D3"),
            "CURRENT_DAY_NAME" => english.GetDayName(now.DayOfWeek),
            "CURRENT_DAY_NAME_SHORT" => english.GetAbbreviatedDayName(now.DayOfWeek),
            "CURRENT_MONTH_NAME" => english.GetMonthName(now.Month),
            "CURRENT_MONTH_NAME_SHORT" => english.GetAbbreviatedMonthName(now.Month),
            "CURRENT_SECONDS_UNIX" => Invariant(now.ToUnixTimeSeconds()),
            "CURRENT_MILLISECONDS_UNIX" => Invariant(now.ToUnixTimeMilliseconds()),
            "CURRENT_TIMEZONE_OFFSET" => (now.Offset < TimeSpan.Zero ? "-" : "+") + now.Offset.ToString(@"hh\:mm"),
            "CURRENT_TIMEZONE_NAME" => zone.HasIanaId ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out string? iana) ? iana
            : zone.Id,
            _ => null,
        };
    }

    /// <summary>
    /// <c>CLIPBOARD</c>'s value from the clipboard's <paramref name="text"/>, as VS Code gives it to a snippet that one
    /// caret inserts: the text's only line that is not blank, when it has one, as VS Code's default
    /// <c>editor.multiCursorPaste</c> spreads it over the carets; otherwise the whole text. Null for no text.
    /// </summary>
    public static string? Clipboard(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        string? only = null;
        foreach (string line in text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (only is not null)
                return text;
            only = line;
        }
        return only ?? text;
    }

    /// <summary>
    /// Selected text that spans lines, as VS Code puts it in for <c>SELECTION</c> and <c>TM_SELECTED_TEXT</c>: each of
    /// its lines after the first gets the indentation that the snippet adds where the variable is, the part of
    /// <paramref name="variableIndentation"/> past what it shares with <paramref name="lineIndentation"/>.
    /// </summary>
    /// <param name="value">The selected text.</param>
    /// <param name="variableIndentation">
    /// The whitespace at the start of the last line of what went in before the variable.
    /// </param>
    /// <param name="lineIndentation">The whitespace at the start of the selection's line, up to where it starts.</param>
    public static string IndentSelection(string value, string variableIndentation, string lineIndentation)
    {
        string extra = variableIndentation[variableIndentation.AsSpan().CommonPrefixLength(lineIndentation)..];
        if (extra.Length == 0)
            return value;
        var text = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            text.Append(value[i]);
            if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                text.Append(value[++i]);
            if (value[i] is '\r' or '\n')
                text.Append(extra);
        }
        return text.ToString();
    }

    /// <summary>A random variable: six decimal or hexadecimal digits, or a UUID; null for another variable.</summary>
    public static string? Random(string name) =>
        name switch
        {
            "RANDOM" => Invariant(System.Random.Shared.Next(1_000_000), "D6"),
            "RANDOM_HEX" => Invariant(System.Random.Shared.Next(0x100_0000), "x6"),
            "UUID" => Guid.NewGuid().ToString(),
            _ => null,
        };

    private static string Invariant(long value, string? format = null) =>
        value.ToString(format, CultureInfo.InvariantCulture);
}
