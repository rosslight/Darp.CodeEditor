namespace Darp.CodeEditor;

/// <summary>
/// Which closing characters typing steps over, or Backspace deletes with their opening one, as VS Code's
/// <c>editor.autoClosingOvertype</c> and <c>editor.autoClosingDelete</c>: for
/// <see cref="CodeEditor.AutoClosingOvertype"/> and <see cref="CodeEditor.AutoClosingDelete"/>. Only the single
/// characters of the language's auto-closing pairs.
/// </summary>
public enum AutoClosingEdit
{
    /// <summary>Any of them.</summary>
    Always,

    /// <summary>Those that the editor typed, while the caret stays between them and their opening ones.</summary>
    Auto,

    /// <summary>None.</summary>
    Never,
}
