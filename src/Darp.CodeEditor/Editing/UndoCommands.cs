using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;

namespace Darp.CodeEditor.Editing;

/// <summary>The commands of one editor's view that undo and redo the document's steps.</summary>
internal sealed class UndoCommands : IEditorFeature
{
    private readonly TextArea _area;
    private readonly CodeDocument _document;

    public UndoCommands(TextArea area, CodeDocument document)
    {
        _area = area;
        _document = document;
    }

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        const CommandNeeds needs = CommandNeeds.TextFocus | CommandNeeds.Writable;
        commands.Add(CodeEditorCommands.Undo, () => UndoOrRedo(redo: false), needs);
        commands.Add(CodeEditorCommands.Redo, () => UndoOrRedo(redo: true), needs);
    }

    /// <returns>Whether there was a step to undo or redo.</returns>
    private bool UndoOrRedo(bool redo)
    {
        if (!(redo ? _document.CanRedo : _document.CanUndo))
            return false;
        if (redo)
            _document.Redo();
        else
            _document.Undo();
        _area.Caret.BringCaretToView();
        return true;
    }
}
