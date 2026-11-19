namespace Darp.CodeEditor;

/// <summary>
/// The editor's settings that apps set differently for different editors or users, each at the path of its VS Code
/// setting: <c>editor.autoClosingBrackets</c> is <c>AutoClosing.Brackets</c>. Behavior without a setting here is built
/// in, with VS Code's default. Immutable: set it once, or share one instance between editors, as a keymap.
/// </summary>
public sealed record CodeEditorOptions
{
    /// <summary>Every setting at its default.</summary>
    public static CodeEditorOptions Default { get; } = new();

    /// <summary>Which opening characters bring their closing ones: <c>editor.autoClosing*</c>.</summary>
    public AutoClosingOptions AutoClosing { get; init; } = new();
}

/// <summary>
/// Which typed opening brackets and quotes bring their closing ones. Typing a closing character steps over one that the
/// editor typed, and Backspace between an empty pair that it closed deletes both; with a kind set to
/// <see cref="AutoClosingMode.Never"/>, no pairs of that kind are deleted so.
/// </summary>
public sealed record AutoClosingOptions
{
    /// <summary>Brackets, such as <c>(</c>, <c>[</c> and <c>{</c>: <c>editor.autoClosingBrackets</c>.</summary>
    public AutoClosingMode Brackets { get; init; } = AutoClosingMode.LanguageDefined;

    /// <summary>Quotes, such as <c>"</c> and <c>'</c>: <c>editor.autoClosingQuotes</c>.</summary>
    public AutoClosingMode Quotes { get; init; } = AutoClosingMode.LanguageDefined;
}

/// <summary>Where a typed opening character brings its closing one, as VS Code's <c>editor.autoClosing*</c> values.</summary>
public enum AutoClosingMode
{
    /// <summary>Wherever the language's pairs allow, also before a word, and a quote right after one.</summary>
    Always,

    /// <summary>
    /// Before whitespace, the end of the line, another closing character, or a character of the language's
    /// <c>autoCloseBefore</c>, or by default one that can't start an expression; a quote not right after a word.
    /// </summary>
    LanguageDefined,

    /// <summary>Before whitespace, the end of the line or another closing character only.</summary>
    BeforeWhitespace,

    /// <summary>Never.</summary>
    Never,
}
