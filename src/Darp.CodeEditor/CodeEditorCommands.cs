namespace Darp.CodeEditor;

/// <summary>
/// The names of the editor's commands, which <see cref="CodeEditorKeymap"/> binds keys to: VS Code's command ids, so
/// that a VS Code <c>keybindings.json</c> binds them.
/// </summary>
/// <remarks>
/// A command applies only where it can act, and its key otherwise goes on to the binding before it, or to the editor
/// and the app as an ordinary key. Commands on the text apply while the text has the keyboard, and those that change
/// the text or ask a language server not in a read-only editor; the search bar's apply also while it has the keyboard.
/// </remarks>
public static class CodeEditorCommands
{
    /// <summary>Asks the language server for completion at the caret.</summary>
    public const string TriggerSuggest = "editor.action.triggerSuggest";

    /// <summary>Asks the language server for the signature of the call around the caret.</summary>
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";

    /// <summary>Opens the search bar, with the selection within one line as its query.</summary>
    public const string Find = "actions.find";

    /// <summary>Opens the search bar with its replace row; a read-only editor opens the search bar only.</summary>
    public const string Replace = "editor.action.startFindReplaceAction";

    /// <summary>Selects the next match, opening the bar for the selection if it is closed.</summary>
    public const string FindNext = "editor.action.nextMatchFindAction";

    /// <summary>Selects the previous match, opening the bar for the selection if it is closed.</summary>
    public const string FindPrevious = "editor.action.previousMatchFindAction";

    /// <summary>
    /// Comments the selected lines, or the caret's, with the language's line comment, or uncomments them when all
    /// are commented; in a language without one, toggles its block comment.
    /// </summary>
    public const string CommentLine = "editor.action.commentLine";

    /// <summary>
    /// Moves the caret to the start of the word before it; a single separator right after a word, such as the dot of
    /// <c>foo.bar</c>, goes with the word. At a line's start, it goes on from the end of the line before.
    /// </summary>
    public const string CursorWordLeft = "cursorWordLeft";

    /// <summary>Selects from the caret to where <see cref="CursorWordLeft"/> moves it.</summary>
    public const string CursorWordLeftSelect = "cursorWordLeftSelect";

    /// <summary>
    /// Moves the caret to the end of the word after it; a single separator right before a word goes with the word. At a
    /// line's end, it goes on from the start of the line after.
    /// </summary>
    public const string CursorWordEndRight = "cursorWordEndRight";

    /// <summary>Selects from the caret to where <see cref="CursorWordEndRight"/> moves it.</summary>
    public const string CursorWordEndRightSelect = "cursorWordEndRightSelect";

    /// <summary>
    /// Deletes the selection, or else back to the start of the word before the caret; two or more spaces before it go
    /// alone, and at a line's start, the line break.
    /// </summary>
    public const string DeleteWordLeft = "deleteWordLeft";

    /// <summary>
    /// Deletes the selection, or else up to the end of the word after the caret; spaces after it go alone, and at a
    /// line's end, the line break and the next line's indentation.
    /// </summary>
    public const string DeleteWordRight = "deleteWordRight";

    /// <summary>
    /// Without a selection, selects the word at the caret: the command's first step. Its next steps add a caret at each
    /// next match; the editor has one caret, so with a selection it does not apply.
    /// </summary>
    public const string AddSelectionToNextFindMatch = "editor.action.addSelectionToNextFindMatch";

    /// <summary>
    /// Undoes the document's last step, as <see cref="CodeDocument.Undo"/> does, and brings the caret into view.
    /// </summary>
    public const string Undo = "undo";

    /// <summary>
    /// Redoes the last step undone, as <see cref="CodeDocument.Redo"/> does, and brings the caret into view.
    /// </summary>
    public const string Redo = "redo";
}
