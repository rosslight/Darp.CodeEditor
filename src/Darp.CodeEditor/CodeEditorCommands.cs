namespace Darp.CodeEditor;

/// <summary>
/// The ids of the editor's commands, for <see cref="CodeEditor.RunCommand"/>: VS Code's command ids.
/// </summary>
/// <remarks>
/// A command applies only where it can act. Those that change the text or ask a language server don't apply in a
/// read-only editor. By its default key, a command on the text also needs the text to have the keyboard, and a key whose
/// command does not apply goes on to the app.
/// </remarks>
public static class CodeEditorCommands
{
    /// <summary>Asks the language server for completion at the caret.</summary>
    public const string TriggerSuggest = "editor.action.triggerSuggest";

    /// <summary>Asks the language server for the signature of the call around the caret.</summary>
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";

    /// <summary>
    /// Opens the search bar and puts the keyboard in its query, which starts with the selection within one line.
    /// </summary>
    public const string Find = "actions.find";

    /// <summary>Opens the search bar with its replace row; a read-only editor opens it without.</summary>
    public const string Replace = "editor.action.startFindReplaceAction";

    /// <summary>Selects the next match, opening the bar if it is closed.</summary>
    public const string FindNext = "editor.action.nextMatchFindAction";

    /// <summary>Selects the previous match, as <see cref="FindNext"/> selects the next one.</summary>
    public const string FindPrevious = "editor.action.previousMatchFindAction";

    /// <summary>
    /// Comments the selected lines, or the caret's, with the language's line comment, or uncomments them when all are
    /// commented; in a language without one, toggles its block comment.
    /// </summary>
    public const string CommentLine = "editor.action.commentLine";

    /// <summary>Undoes the document's last step, as <see cref="CodeDocument.Undo"/> does, and shows the caret.</summary>
    public const string Undo = "undo";

    /// <summary>Redoes the last step undone, as <see cref="CodeDocument.Redo"/> does, and shows the caret.</summary>
    public const string Redo = "redo";

    /// <summary>Moves the selection, or else the caret's line, to the clipboard.</summary>
    public const string Cut = "editor.action.clipboardCutAction";

    /// <summary>Copies the selection, or else the caret's line, to the clipboard.</summary>
    public const string Copy = "editor.action.clipboardCopyAction";

    /// <summary>Replaces the selection with the clipboard's text, in the document's line breaks.</summary>
    public const string Paste = "editor.action.clipboardPasteAction";

    /// <summary>Selects the whole text.</summary>
    public const string SelectAll = "editor.action.selectAll";
}
