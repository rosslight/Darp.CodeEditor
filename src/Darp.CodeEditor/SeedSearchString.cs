namespace Darp.CodeEditor;

/// <summary>
/// What the search takes from the text as its query, as VS Code's <c>editor.find.seedSearchStringFromSelection</c>:
/// for <see cref="CodeEditor.SeedSearchStringFromSelection"/>.
/// </summary>
public enum SeedSearchString
{
    /// <summary>Nothing: the query stays as it is.</summary>
    Never,

    /// <summary>The selection within one line, or without a selection the word at the caret.</summary>
    Always,

    /// <summary>The selection within one line only.</summary>
    Selection,
}
