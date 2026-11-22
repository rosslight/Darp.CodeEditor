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
        commands.Add(CodeEditorCommands.Undo, Undo, needs, () => _document.CanUndo);
        commands.Add(CodeEditorCommands.Redo, Redo, needs, () => _document.CanRedo);
    }

    private void Undo()
    {
        _document.Undo();
        _area.Caret.BringCaretToView();
    }

    private void Redo()
    {
        _document.Redo();
        _area.Caret.BringCaretToView();
    }
}
