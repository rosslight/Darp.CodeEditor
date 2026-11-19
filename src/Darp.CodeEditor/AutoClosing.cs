namespace Darp.CodeEditor;

/// <summary>
/// Where typing the opening characters of the language's auto-closing pairs also types their closing ones, as VS
/// Code's <c>editor.autoClosingBrackets</c> and <c>editor.autoClosingQuotes</c>: for
/// <see cref="CodeEditor.AutoClosingBrackets"/> and <see cref="CodeEditor.AutoClosingQuotes"/>. Quotes are
/// <c>'</c>, <c>"</c> and <c>`</c>, and the other pairs brackets. Unless <see cref="Never"/>, a pair also closes before
/// the closing characters of another.
/// </summary>
public enum AutoClosing
{
    /// <summary>Before any character, and a quote also right after a word.</summary>
    Always,

    /// <summary>Before whitespace, or a character of the language's <c>autoCloseBefore</c>.</summary>
    LanguageDefined,

    /// <summary>Before a space or a tab only.</summary>
    BeforeWhitespace,

    /// <summary>Nowhere.</summary>
    Never,
}
