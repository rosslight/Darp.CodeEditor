namespace Darp.CodeEditor;

/// <summary>
/// When the search bar turns Find in Selection on by itself as it opens, as VS Code's
/// <c>editor.find.autoFindInSelection</c>: for <see cref="CodeEditor.AutoFindInSelection"/>.
/// </summary>
public enum AutoFindInSelection
{
    /// <summary>Never: only its toggle and its key turn it on.</summary>
    Never,

    /// <summary>Whenever something is selected.</summary>
    Always,

    /// <summary>When the selection spans several lines.</summary>
    Multiline,
}
