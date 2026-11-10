using System.Globalization;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The values of the snippet variables that need no editor: the file's, the time's and the random ones, as VS Code
/// gives them. Names and months are English, as in VS Code's English version.
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
