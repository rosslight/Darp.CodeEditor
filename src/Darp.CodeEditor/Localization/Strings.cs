using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Darp.CodeEditor.Localization;

/// <summary>
/// The editor's own text, from <c>Strings.resx</c> and its translations, in the UI culture as it is read: an app may
/// switch its language while it runs. English and German.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager s_resources = new(typeof(Strings));

    public static string Cut => Get();
    public static string Copy => Get();
    public static string Paste => Get();
    public static string SelectAll => Get();
    public static string CommentLine => Get();
    public static string Find => Get();
    public static string Replace => Get();
    public static string ReplaceAll => Get();
    public static string MatchCase => Get();
    public static string MatchWholeWord => Get();
    public static string UseRegularExpression => Get();
    public static string PreserveCase => Get();
    public static string FindInSelection => Get();
    public static string PreviousMatch => Get();
    public static string NextMatch => Get();
    public static string CloseSearch => Get();
    public static string NoResults => Get();
    public static string MatchCount => Get();
    public static string TooManyMatches => Get();
    public static string SearchTimedOut => Get();
    public static string NoSuggestions => Get();

    private static string Get([CallerMemberName] string name = "") =>
        s_resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
