namespace Darp.CodeEditor;

/// <summary>
/// Where an editor was in its document: the caret, the selection and the scroll position. Taken with
/// <see cref="CodeEditor.SaveViewState"/> and given back with <see cref="CodeEditor.RestoreViewState"/>.
/// </summary>
/// <remarks>
/// It refers to the document's text at the time it was saved; restoring it follows the edits made since, as
/// <see cref="CodeDocument.MapToCurrent"/> does. It lives in memory only and cannot be stored across app starts.
/// Immutable, so it can be kept anywhere, such as in a tab's view model.
/// </remarks>
public sealed class CodeEditorViewState
{
    private CodeEditorViewState() { }

    /// <summary>The document the state belongs to.</summary>
    public CodeDocument Document => throw new NotImplementedException();
}
