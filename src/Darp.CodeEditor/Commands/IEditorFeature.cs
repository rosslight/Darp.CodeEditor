namespace Darp.CodeEditor.Commands;

/// <summary>
/// A part of what an editor does for the document it shows (<see cref="EditorView"/>), which owns its commands: made
/// when the editor shows the document, and disposed when it stops.
/// </summary>
internal interface IEditorFeature : IDisposable
{
    /// <summary>Adds the feature's commands, once, after every feature of the view is made.</summary>
    void AddCommands(CommandTable commands);
}
