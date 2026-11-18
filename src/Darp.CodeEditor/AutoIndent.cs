namespace Darp.CodeEditor;

/// <summary>
/// How far the editor indents by itself, as VS Code's <c>editor.autoIndent</c>: the indentation of a new line on Enter,
/// of a blank line on Tab, and of a line that a typed character makes match the language's decrease pattern. Each level
/// adds to the one before. Set it per language, as <see cref="CodeLanguage.AutoIndent"/>.
/// </summary>
public enum AutoIndent
{
    /// <summary>A new line starts at the line's start, without indentation.</summary>
    None,

    /// <summary>A new line keeps the indentation of the line before.</summary>
    Keep,

    /// <summary>After an opening bracket of the language, a new line is indented one level more.</summary>
    Brackets,

    /// <summary>The language's rules for Enter apply, its <c>onEnterRules</c>, such as one that continues a comment.</summary>
    Advanced,

    /// <summary>
    /// The language's indentation rules apply, its <c>indentationRules</c>: to a new line, to a blank line on Tab, and
    /// to a line that a typed character makes match the decrease pattern. VS Code's default.
    /// </summary>
    Full,
}
