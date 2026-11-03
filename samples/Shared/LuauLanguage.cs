namespace Darp.CodeEditor.Samples;

/// <summary>The demos' Luau support: its grammar comes from a file, so the editing helpers need its configuration.</summary>
internal static class LuauLanguage
{
    /// <summary>Comment tokens and brackets of Luau, whose grammar comes from a file.</summary>
    public static LanguageConfiguration Configuration { get; } =
        new()
        {
            LineComment = "--",
            BlockComment = new BlockComment("--[[", "]]"),
            Brackets = [new('(', ')'), new('[', ']'), new('{', '}')],
            AutoClosingPairs =
            [
                new('(', ')'),
                new('[', ']'),
                new('{', '}'),
                new('"', '"', NotInString: true),
                new('\'', '\'', NotInString: true),
                new('`', '`', NotInString: true),
            ],
        };
}
