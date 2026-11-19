using System.Text.Json;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Loading;

/// <summary>
/// The editing rules of a language, from VS Code's <c>language-configuration.json</c>: comments, brackets,
/// auto-closing and surrounding pairs, the characters before which pairs close, indentation rules and the rules for
/// Enter. Other settings of the file are not read.
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

    /// <summary>The editing rules of <paramref name="language"/>; none while they load.</summary>
    public static LanguageConfiguration Of(CodeLanguage language) =>
        language.Loaded is { IsCompletedSuccessfully: true } loaded ? loaded.Result.Configuration : None;

    /// <summary>The rules for Enter, <c>onEnterRules</c>, in their order: the first that matches applies.</summary>
    public IReadOnlyList<OnEnterRule> OnEnterRules { get; init; } = [];

    /// <summary>The characters before which a pair closes, <c>autoCloseBefore</c>; null for the defaults.</summary>
    public string? AutoCloseBefore { get; init; }

    /// <summary>
    /// The characters before which a quote, or else a bracket, closes: <see cref="AutoCloseBefore"/>, or by default
    /// those that can't start an expression, and for brackets the quotes too.
    /// </summary>
    public string AutoCloseBeforeSet(bool forQuotes) =>
        AutoCloseBefore ?? (forQuotes ? ";:.,=}])> \n\t" : "'\"`;:.,=}])> \n\t");

    /// <summary>
    /// Finds the brackets, opening and closing, in any case, and a bracket of letters, such as <c>begin</c>, as a whole
    /// word. Null without brackets.
    /// </summary>
    public Regex? BracketPattern { get; } = BracketPatternOf(Brackets);

    private static Regex? BracketPatternOf(IReadOnlyList<CharacterPair> pairs)
    {
        string[] brackets = [.. pairs.SelectMany(p => new[] { p.Open, p.Close }).Where(b => b.Length > 0).Distinct()];
        if (brackets.Length == 0)
            return null;
        IEnumerable<string> alternatives = brackets.Select(b =>
            b.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ' ') ? $@"\b{Regex.Escape(b)}\b" : Regex.Escape(b)
        );
        return new Regex(string.Join('|', alternatives), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

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
                {
                    IndentNextLinePattern = Property(rules, "indentNextLinePattern") is { } next ? Pattern(next) : null,
                    UnIndentedLinePattern = Property(rules, "unIndentedLinePattern") is { } un ? Pattern(un) : null,
                }
                : null
        )
        {
            OnEnterRules = Property(root, "onEnterRules") is { ValueKind: JsonValueKind.Array } onEnterRules
                ? [.. onEnterRules.EnumerateArray().Select(OnEnter).OfType<OnEnterRule>()]
                : [],
            // An empty one counts as none.
            AutoCloseBefore =
                Property(root, "autoCloseBefore") is { ValueKind: JsonValueKind.String } closeBefore
                && closeBefore.GetString() is { Length: > 0 } characters
                    ? characters
                    : null,
        };
    }

    /// <summary>
    /// A rule of <c>onEnterRules</c>; null for one to skip: without an action of a known kind, or with a regular
    /// expression that is missing or that .NET can't read.
    /// </summary>
    private static OnEnterRule? OnEnter(JsonElement rule)
    {
        if (Property(rule, "action") is not { ValueKind: JsonValueKind.Object } action)
            return null;
        IndentAction? indent = Property(action, "indent") is { ValueKind: JsonValueKind.String } kind
            ? kind.GetString() switch
            {
                "none" => IndentAction.None,
                "indent" => IndentAction.Indent,
                "indentOutdent" => IndentAction.IndentOutdent,
                "outdent" => IndentAction.Outdent,
                _ => null,
            }
            : null;
        if (indent is not { } indentAction || Property(rule, "beforeText") is not { } beforeText)
            return null;
        try
        {
            return new OnEnterRule(
                Pattern(beforeText),
                Property(rule, "afterText") is { } afterText ? Pattern(afterText) : null,
                Property(rule, "previousLineText") is { } previousLineText ? Pattern(previousLineText) : null,
                new EnterAction(
                    indentAction,
                    Property(action, "appendText") is { ValueKind: JsonValueKind.String } append
                        ? append.GetString() ?? ""
                        : "",
                    Property(action, "removeText") is { ValueKind: JsonValueKind.Number } remove
                    && remove.TryGetInt32(out int removed)
                        ? Math.Max(0, removed)
                        : 0
                )
            );
        }
        catch (Exception e)
            when (e is ArgumentException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
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

/// <summary>
/// A rule for Enter: when the text before the caret matches <paramref name="BeforeText"/>, and the text after it and the
/// line above match the others if given, the new line takes <paramref name="Action"/>.
/// </summary>
internal sealed record OnEnterRule(Regex BeforeText, Regex? AfterText, Regex? PreviousLineText, EnterAction Action)
{
    /// <summary>Whether the rule applies to the texts around the caret, within the time of one indentation.</summary>
    public bool Matches(IndentationMatching matching, string previousLineText, string beforeText, string afterText) =>
        matching.Matches(BeforeText, beforeText)
        && (AfterText is null || matching.Matches(AfterText, afterText))
        && (PreviousLineText is null || matching.Matches(PreviousLineText, previousLineText));
}

/// <summary>What a rule for Enter does to the new line.</summary>
/// <param name="Indent">How the new line is indented, from the indentation of the caret's line.</param>
/// <param name="AppendText">Text that goes in after the new line's indentation, such as <c>// </c>.</param>
/// <param name="RemoveText">How many characters of indentation the new line takes off.</param>
internal readonly record struct EnterAction(IndentAction Indent, string AppendText, int RemoveText);

/// <summary>How a rule for Enter indents the new line.</summary>
internal enum IndentAction
{
    /// <summary>As the caret's line.</summary>
    None,

    /// <summary>One level more than the caret's line.</summary>
    Indent,

    /// <summary>
    /// Two new lines: the caret goes on the first, one level more, and the text after the caret on the second, at the
    /// caret's line's indentation, as between brackets.
    /// </summary>
    IndentOutdent,

    /// <summary>One level less than the caret's line.</summary>
    Outdent,
}

/// <summary>When a line indents or outdents: regular expressions on its text, with the flags the file gives.</summary>
/// <param name="IncreaseIndentPattern">The lines after a matching line are indented one level more.</param>
/// <param name="DecreaseIndentPattern">A matching line is indented one level less than the lines before.</param>
internal sealed record IndentationRules(Regex IncreaseIndentPattern, Regex DecreaseIndentPattern)
{
    /// <summary>
    /// Only the line after a matching line is indented one level more, as after <c>if (x)</c> without a brace; null
    /// for none.
    /// </summary>
    public Regex? IndentNextLinePattern { get; init; }

    /// <summary>
    /// A matching line, such as a comment that may stand at any indentation, gives the lines after it no indentation:
    /// they take theirs from the lines above it. Null for none.
    /// </summary>
    public Regex? UnIndentedLinePattern { get; init; }

    /// <summary>How long one match may take. Some patterns backtrack on long lines, as JSON's does on nested objects.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(5);

    /// <summary>
    /// How long one indentation, of a new line or of a line the user types, may read and match lines, with the rules
    /// for Enter: on the UI thread, about 10 ms, and with a match that starts just before then, at most 15 ms. A line
    /// above it all needs about a microsecond per match.
    /// </summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromMilliseconds(10);

    /// <summary>Starts matching lines for one indentation, which may take <see cref="TimeLimit"/>.</summary>
    public IndentationMatching StartMatching() => new(this, TimeProvider.System);
}

/// <summary>
/// Reads and matches lines for one indentation, against the indentation rules and the rules for Enter. A line read or a
/// match after <see cref="IndentationRules.TimeLimit"/>, or a match that takes longer than
/// <see cref="IndentationRules.MatchTimeout"/>, throws <see cref="RegexMatchTimeoutException"/>, and the indentation
/// keeps what the line has.
/// </summary>
internal readonly struct IndentationMatching
{
    private readonly IndentationRules? _rules;
    private readonly TimeProvider _time;

    /// <summary>The timestamp of <see cref="_time"/> after which no line is read and no match starts.</summary>
    private readonly long _deadline;

    /// <summary>Starts the time of one indentation, by <paramref name="time"/>; without indentation rules, none match.</summary>
    public IndentationMatching(IndentationRules? rules, TimeProvider time)
    {
        _rules = rules;
        _time = time;
        _deadline = time.GetTimestamp() + (long)(IndentationRules.TimeLimit.TotalSeconds * time.TimestampFrequency);
    }

    /// <summary>
    /// Reads line <paramref name="number"/> with <paramref name="lineText"/>, within the time limit: a scan over
    /// whitespace lines reads many without matching any.
    /// </summary>
    public string Read(Func<int, string> lineText, int number) =>
        _time.GetTimestamp() < _deadline
            ? lineText(number)
            : throw new RegexMatchTimeoutException("", "", IndentationRules.TimeLimit);

    /// <summary>Whether lines after <paramref name="line"/> are indented one level more.</summary>
    public bool Increases(string line) => Matches(_rules?.IncreaseIndentPattern, line);

    /// <summary>Whether <paramref name="line"/> is indented one level less than the lines before.</summary>
    public bool Decreases(string line) => Matches(_rules?.DecreaseIndentPattern, line);

    /// <summary>Whether only the line after <paramref name="line"/> is indented one level more.</summary>
    public bool IndentsNextLine(string line) => Matches(_rules?.IndentNextLinePattern, line);

    /// <summary>Whether <paramref name="line"/> gives the lines after it no indentation.</summary>
    public bool IsUnIndented(string line) => Matches(_rules?.UnIndentedLinePattern, line);

    /// <summary>Whether <paramref name="pattern"/> matches <paramref name="text"/>, within the time; none matches nothing.</summary>
    public bool Matches(Regex? pattern, string text) =>
        pattern is not null
        && (
            _time.GetTimestamp() < _deadline
                ? pattern.IsMatch(text)
                : throw new RegexMatchTimeoutException(text, pattern.ToString(), IndentationRules.TimeLimit)
        );
}
