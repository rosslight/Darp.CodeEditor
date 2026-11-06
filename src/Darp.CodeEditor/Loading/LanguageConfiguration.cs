using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Loading;

/// <summary>
/// The editing rules of a language, from VS Code's <c>language-configuration.json</c>: comments, brackets,
/// auto-closing and surrounding pairs, and indentation rules. Other settings of the file are not read.
/// </summary>
internal sealed record LanguageConfiguration(
    string? LineComment,
    CharacterPair? BlockComment,
    IReadOnlyList<CharacterPair> Brackets,
    IReadOnlyList<AutoClosingPair> AutoClosingPairs,
    IReadOnlyList<CharacterPair> SurroundingPairs,
    IndentationRules? IndentationRules
)
{
    /// <summary>No editing rules.</summary>
    public static LanguageConfiguration None { get; } = new(null, null, [], [], [], null);

    /// <summary>Reads a <c>language-configuration.json</c> file.</summary>
    public static LanguageConfiguration Read(Uri file)
    {
        using Stream stream = AssetFiles.Open(file);
        using JsonDocument json = JsonDocument.Parse(stream, AssetFiles.Jsonc);
        JsonElement root = json.RootElement;
        JsonElement? comments = Property(root, "comments");
        // As in VS Code, auto-closing pairs default to the brackets, and surrounding pairs to the auto-closing pairs.
        List<CharacterPair> brackets = List(root, "brackets", Pair);
        List<AutoClosingPair> autoClosingPairs = Property(root, "autoClosingPairs") is not null
            ? List(root, "autoClosingPairs", AutoClosing)
            : [.. brackets.Select(b => new AutoClosingPair(b.Open, b.Close, NotInString: false, NotInComment: false))];
        List<CharacterPair> surroundingPairs = Property(root, "surroundingPairs") is not null
            ? List(root, "surroundingPairs", Pair)
            : [.. autoClosingPairs.Select(p => new CharacterPair(p.Open, p.Close))];
        return new LanguageConfiguration(
            LineComment: Property(comments, "lineComment") switch
            {
                // Newer files give the line comment as an object with its token in "comment".
                { ValueKind: JsonValueKind.Object } lineComment => Text(lineComment.GetProperty("comment")),
                { } lineComment => Text(lineComment),
                null => null,
            },
            BlockComment: Property(comments, "blockComment") is { } blockComment ? Pair(blockComment) : null,
            Brackets: brackets,
            AutoClosingPairs: autoClosingPairs,
            SurroundingPairs: surroundingPairs,
            IndentationRules: Property(root, "indentationRules") is { } rules
                ? new IndentationRules(
                    Pattern(rules.GetProperty("increaseIndentPattern")),
                    Pattern(rules.GetProperty("decreaseIndentPattern"))
                )
                : null
        );
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out JsonElement property)
            ? property
            : null;

    private static List<T> List<T>(JsonElement root, string name, Func<JsonElement, T> read) =>
        Property(root, name) is { } array ? [.. array.EnumerateArray().Select(read)] : [];

    /// <summary>A pair as <c>["{", "}"]</c> or <c>{ "open": "{", "close": "}" }</c>.</summary>
    private static CharacterPair Pair(JsonElement pair) =>
        pair.ValueKind == JsonValueKind.Array
            ? new CharacterPair(Text(pair[0]), Text(pair[1]))
            : new CharacterPair(Text(pair.GetProperty("open")), Text(pair.GetProperty("close")));

    private static AutoClosingPair AutoClosing(JsonElement pair)
    {
        CharacterPair characters = Pair(pair);
        // notIn is a scope or a list of scopes, of which VS Code knows "string" and "comment".
        string[] notIn = Property(pair, "notIn") switch
        {
            { ValueKind: JsonValueKind.Array } scopes => [.. scopes.EnumerateArray().Select(Text)],
            { } scope => [Text(scope)],
            null => [],
        };
        return new AutoClosingPair(
            characters.Open,
            characters.Close,
            NotInString: notIn.Contains("string"),
            NotInComment: notIn.Contains("comment")
        );
    }

    /// <summary>
    /// A regular expression as a string, or as an object with its <c>pattern</c> and JavaScript's <c>flags</c>, of
    /// which <c>i</c>, <c>m</c> and <c>s</c> change how .NET matches.
    /// </summary>
    private static Regex Pattern(JsonElement pattern)
    {
        (string source, string flags) =
            pattern.ValueKind == JsonValueKind.Object
                ? (Text(pattern.GetProperty("pattern")), Property(pattern, "flags") is { } value ? Text(value) : "")
                : (Text(pattern), "");
        RegexOptions options = RegexOptions.CultureInvariant;
        if (flags.Contains('i'))
            options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m'))
            options |= RegexOptions.Multiline;
        if (flags.Contains('s'))
            options |= RegexOptions.Singleline;
        return new Regex(source, options, IndentationRules.MatchTimeout);
    }

    /// <summary>A string value; anything else makes the file invalid.</summary>
    private static string Text(JsonElement value) =>
        value.GetString() ?? throw new InvalidDataException("Expected a string, found null.");
}

/// <summary>An opening and a closing token, such as <c>{</c> and <c>}</c> or <c>&lt;!--</c> and <c>--&gt;</c>.</summary>
internal readonly record struct CharacterPair(string Open, string Close);

/// <summary>A pair whose closing token is typed with the opening one.</summary>
/// <param name="Open">The token the user types.</param>
/// <param name="Close">The token typed with it.</param>
/// <param name="NotInString">Not closed automatically inside a string.</param>
/// <param name="NotInComment">Not closed automatically inside a comment.</param>
internal readonly record struct AutoClosingPair(string Open, string Close, bool NotInString, bool NotInComment);

/// <summary>When a line indents or outdents: regular expressions on its text, with the flags the file gives.</summary>
/// <param name="IncreaseIndentPattern">The lines after a matching line are indented one level more.</param>
/// <param name="DecreaseIndentPattern">A matching line is indented one level less than the lines before.</param>
internal sealed record IndentationRules(Regex IncreaseIndentPattern, Regex DecreaseIndentPattern)
{
    /// <summary>How long one match may take. Some patterns backtrack on long lines, as JSON's does on nested objects.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// How long one indentation, of a new line or of a line the user types, may match lines: on the UI thread, about
    /// 10 ms, and with a match that starts just before then, at most 15 ms. A line above it all needs about a
    /// microsecond per match.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMilliseconds(10);

    /// <summary>Starts matching lines for one indentation, which may take <see cref="TimeLimit"/>.</summary>
    public IndentationMatching StartMatching() =>
        new(this, Stopwatch.GetTimestamp() + (long)(TimeLimit.TotalSeconds * Stopwatch.Frequency));
}

/// <summary>
/// Matches lines against the indentation rules for one indentation. A match after its time limit, or one that takes
/// longer than <see cref="IndentationRules.MatchTimeout"/>, throws <see cref="RegexMatchTimeoutException"/>, and the
/// indentation keeps what the line has.
/// </summary>
internal readonly struct IndentationMatching(IndentationRules rules, long deadline)
{
    private readonly IndentationRules _rules = rules;

    /// <summary>The <see cref="Stopwatch"/> timestamp after which no match starts.</summary>
    private readonly long _deadline = deadline;

    /// <summary>Whether lines after <paramref name="line"/> are indented one level more.</summary>
    public bool Increases(string line) => Matches(_rules.IncreaseIndentPattern, line);

    /// <summary>Whether <paramref name="line"/> is indented one level less than the lines before.</summary>
    public bool Decreases(string line) => Matches(_rules.DecreaseIndentPattern, line);

    private bool Matches(Regex pattern, string line) =>
        Stopwatch.GetTimestamp() < _deadline
            ? pattern.IsMatch(line)
            : throw new RegexMatchTimeoutException(line, pattern.ToString(), IndentationRules.TimeLimit);
}
