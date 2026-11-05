using System.Text.Json;

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
        return new LanguageConfiguration(
            LineComment: Property(comments, "lineComment") switch
            {
                // Newer files give the line comment as an object with its token in "comment".
                { ValueKind: JsonValueKind.Object } lineComment => Text(lineComment.GetProperty("comment")),
                { } lineComment => Text(lineComment),
                null => null,
            },
            BlockComment: Property(comments, "blockComment") is { } blockComment ? Pair(blockComment) : null,
            Brackets: List(root, "brackets", Pair),
            AutoClosingPairs: List(root, "autoClosingPairs", AutoClosing),
            SurroundingPairs: List(root, "surroundingPairs", Pair),
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

    /// <summary>A regular expression as a string, or as an object with its <c>pattern</c>.</summary>
    private static string Pattern(JsonElement pattern) =>
        pattern.ValueKind == JsonValueKind.Object ? Text(pattern.GetProperty("pattern")) : Text(pattern);

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

/// <summary>When a new line indents or outdents: regular expressions on the line, in VS Code's syntax.</summary>
internal sealed record IndentationRules(string IncreaseIndentPattern, string DecreaseIndentPattern);
