namespace Darp.CodeEditor;

/// <summary>
/// What the editing helpers need to know about a language: its comment tokens, its brackets, and the pairs that close
/// automatically. The editor reads it from VS Code's language configuration of a bundled grammar; for other grammars
/// the host sets it.
/// </summary>
public sealed record LanguageConfiguration
{
    /// <summary>Brackets <c>() [] {}</c> and quotes <c>" '</c>, no comments. Used until a language's configuration is known.</summary>
    public static LanguageConfiguration Default { get; } =
        new()
        {
            Brackets = [new('(', ')'), new('[', ']'), new('{', '}')],
            AutoClosingPairs =
            [
                new('(', ')'),
                new('[', ']'),
                new('{', '}'),
                new('"', '"', NotInString: true),
                new('\'', '\'', NotInString: true),
            ],
        };

    /// <summary>The token that starts a line comment, such as <c>//</c> or <c>--</c>.</summary>
    public string? LineComment { get; init; }

    /// <summary>The tokens around a block comment, such as <c>/*</c> and <c>*/</c>.</summary>
    public BlockComment? BlockComment { get; init; }

    /// <summary>Brackets that are matched, and indent the lines between them.</summary>
    public IReadOnlyList<BracketPair> Brackets { get; init; } = [];

    /// <summary>Pairs whose closing character is typed with the opening one, and that surround a selection.</summary>
    public IReadOnlyList<AutoClosingPair> AutoClosingPairs { get; init; } = [];

    /// <summary>Converts VS Code's language configuration, as TextMateSharp reads it. Multi-character pairs are left out.</summary>
    internal static LanguageConfiguration? FromTextMate(TextMateSharp.Grammars.LanguageConfiguration? configuration)
    {
        if (configuration is null)
            return null;
        var brackets = new List<BracketPair>();
        foreach (IList<string> pair in configuration.Brackets ?? [])
        {
            if (pair is [[char open], [char close]])
                brackets.Add(new BracketPair(open, close));
        }
        var autoClosing = new List<AutoClosingPair>();
        foreach (IList<char> pair in configuration.AutoClosingPairs?.CharPairs ?? [])
        {
            if (pair is [char open, char close])
                autoClosing.Add(new AutoClosingPair(open, close));
        }
        foreach (TextMateSharp.Grammars.AutoPair pair in configuration.AutoClosingPairs?.AutoPairs ?? [])
        {
            if (pair is { Open: [char open], Close: [char close] })
            {
                IList<string> notIn = pair.NotIn ?? [];
                autoClosing.Add(new AutoClosingPair(open, close, notIn.Contains("string"), notIn.Contains("comment")));
            }
        }
        return new LanguageConfiguration
        {
            LineComment = string.IsNullOrEmpty(configuration.Comments?.LineComment)
                ? null
                : configuration.Comments.LineComment,
            BlockComment = configuration.Comments?.BlockComment is [{ Length: > 0 } start, { Length: > 0 } end]
                ? new BlockComment(start, end)
                : null,
            Brackets = brackets,
            AutoClosingPairs = autoClosing,
        };
    }
}

public readonly record struct BlockComment(string Start, string End);

public readonly record struct BracketPair(char Open, char Close);

/// <param name="NotInString">Not closed automatically inside a string, typical for quotes.</param>
/// <param name="NotInComment">Not closed automatically inside a comment.</param>
public readonly record struct AutoClosingPair(
    char Open,
    char Close,
    bool NotInString = false,
    bool NotInComment = false
);
