namespace Darp.CodeEditor;

/// <summary>
/// Whether the editor highlights the occurrences of the symbol at the caret, as VS Code's
/// <c>editor.occurrencesHighlight</c>: for <see cref="CodeEditor.OccurrencesHighlight"/>. VS Code's experimental
/// <c>multiFile</c>, across the files open in other editors, has no counterpart: an editor knows its own document only.
/// </summary>
public enum OccurrencesHighlight
{
    /// <summary>No occurrences highlighted.</summary>
    Off,

    /// <summary>The occurrences in the editor's document.</summary>
    SingleFile,
}
