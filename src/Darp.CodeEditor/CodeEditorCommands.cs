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

    /// <summary>Shows or hides the selected item's details beside completion's list, while the list shows.</summary>
    public const string ToggleSuggestionDetails = "toggleSuggestionDetails";

    /// <summary>
    /// Commits the selected item of completion's list, while it shows, inserting: its text goes over the word up to the
    /// caret, and the rest of the word stays after it.
    /// </summary>
    public const string AcceptSelectedSuggestion = "acceptSelectedSuggestion";

    /// <summary>
    /// Commits the selected item of completion's list, while it shows, replacing: its text goes over the rest of the
    /// word too, as far as the item's replace range reaches.
    /// </summary>
    public const string AcceptAlternativeSelectedSuggestion = "acceptAlternativeSelectedSuggestion";

    /// <summary>Asks the language server for the signature of the call around the caret.</summary>
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";

    /// <summary>
    /// Opens the search bar and puts the keyboard in its query, which starts with the selection within one line or else
    /// the word at the caret.
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

    /// <summary>Moves the selected lines, or the caret's, above the line before them, with the selection.</summary>
    public const string MoveLinesUp = "editor.action.moveLinesUpAction";

    /// <summary>Moves the selected lines, or the caret's, below the line after them, as <see cref="MoveLinesUp"/>.</summary>
    public const string MoveLinesDown = "editor.action.moveLinesDownAction";

    /// <summary>Copies the selected lines, or the caret's, above them; the selection stays on the lines above.</summary>
    public const string CopyLinesUp = "editor.action.copyLinesUpAction";

    /// <summary>Copies the selected lines, or the caret's, below them; the selection goes to the lines below.</summary>
    public const string CopyLinesDown = "editor.action.copyLinesDownAction";

    /// <summary>
    /// Deletes the selected lines, or the caret's; the caret keeps its column on the line that comes up in their place.
    /// </summary>
    public const string DeleteLines = "editor.action.deleteLines";

    /// <summary>
    /// Inserts a line below the caret's, indented as Enter at the end of the caret's line indents, and puts the caret
    /// on it.
    /// </summary>
    public const string InsertLineAfter = "editor.action.insertLineAfter";

    /// <summary>Inserts a line above the caret's, as <see cref="InsertLineAfter"/> inserts one below.</summary>
    public const string InsertLineBefore = "editor.action.insertLineBefore";

    /// <summary>Indents the selected lines, or the caret's, to their next tab stop.</summary>
    public const string IndentLines = "editor.action.indentLines";

    /// <summary>Outdents the selected lines, or the caret's, to their previous tab stop.</summary>
    public const string OutdentLines = "editor.action.outdentLines";

    /// <summary>
    /// Selects the selected lines, or the caret's, whole, up to the start of the next line; again, it takes the next
    /// line too.
    /// </summary>
    public const string ExpandLineSelection = "expandLineSelection";

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
