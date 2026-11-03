namespace Darp.CodeEditor.Luau;

/// <summary>Luau highlighting and editing helpers for editors.</summary>
public static class LuauLanguage
{
    /// <summary>
    /// The Luau TextMate grammar next to the app, where <c>Darp.CodeEditor.Luau.targets</c> puts it. TextMateSharp's
    /// bundled grammars have Lua, not Luau.
    /// </summary>
    public static string GrammarPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "Grammars", "Luau.tmLanguage.json");

    /// <summary>Comment tokens, brackets and auto-closing pairs of Luau, including its interpolated strings.</summary>
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

    /// <summary>
    /// Gives <paramref name="editor"/> the Luau grammar and <see cref="Configuration"/>. Without the grammar file, the
    /// editor highlights Lua.
    /// </summary>
    public static void Configure(CodeEditor editor)
    {
        editor.LanguageConfiguration = Configuration;
        if (File.Exists(GrammarPath))
            editor.SetGrammarFile(GrammarPath);
        else
            editor.LanguageId = "lua";
    }
}
