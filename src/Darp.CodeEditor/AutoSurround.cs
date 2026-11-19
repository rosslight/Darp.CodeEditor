namespace Darp.CodeEditor;

/// <summary>
/// Which opening characters typed over a selection put the language's surrounding pair around it, as VS Code's
/// <c>editor.autoSurround</c>: for <see cref="CodeEditor.AutoSurround"/>.
/// </summary>
public enum AutoSurround
{
    /// <summary>Quotes and brackets.</summary>
    LanguageDefined,

    /// <summary>Quotes only.</summary>
    Quotes,

    /// <summary>Brackets only.</summary>
    Brackets,

    /// <summary>None: the character replaces the selection.</summary>
    Never,
}
