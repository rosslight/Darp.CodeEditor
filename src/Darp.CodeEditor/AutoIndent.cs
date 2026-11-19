namespace Darp.CodeEditor;

/// <summary>
/// How far the editor indents by itself in a language's documents (<see cref="CodeLanguage.AutoIndent"/>), as VS
/// Code's <c>editor.autoIndent</c>: each level does what the one before it does, and more.
/// </summary>
public enum AutoIndent
{
    /// <summary>A new line starts at the line's start.</summary>
    None,

    /// <summary>A new line keeps the indentation of the line before it.</summary>
    Keep,

    /// <summary>A new line is indented one level more after an opening bracket.</summary>
    Brackets,

    /// <summary>The language's rules for Enter apply too, such as continuing a doc comment.</summary>
    Advanced,

    /// <summary>
    /// The language's indentation rules apply too: on Enter, on Tab on a blank line, when typing makes a line match the
    /// pattern that outdents it, and to moved lines.
    /// </summary>
    Full,
}
