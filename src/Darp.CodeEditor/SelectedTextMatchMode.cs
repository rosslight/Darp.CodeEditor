namespace Darp.CodeEditor;

/// <summary>
/// How the other occurrences of the selected text match it, as VS Code's <c>editor.selectedTextMatchMode</c>: for
/// <see cref="CodeEditor.SelectedTextMatchMode"/>.
/// </summary>
public enum SelectedTextMatchMode
{
    /// <summary>With the search bar's Match Case and Match Whole Word, also while the bar is closed.</summary>
    FindOptions,

    /// <summary>With case, and inside words too, whatever the search bar's options.</summary>
    CaseSensitive,

    /// <summary>Without case, and inside words too, whatever the search bar's options.</summary>
    CaseInsensitive,
}
