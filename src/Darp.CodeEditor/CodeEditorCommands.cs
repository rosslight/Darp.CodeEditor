namespace Darp.CodeEditor;

/// <summary>
/// The names of the editor's commands, which <see cref="CodeEditorKeymap"/> binds keys to: VS Code's command ids, so
/// that a VS Code <c>keybindings.json</c> binds them.
/// </summary>
/// <remarks>
/// A command applies only where it can act, and its key otherwise goes on to the binding before it, or to the editor
/// and the app as an ordinary key. Commands on the text apply while the text has the keyboard, and those that change
/// the text or ask a language server not in a read-only editor; the search bar's apply also while it has the keyboard,
/// and its toggles also while it is closed.
/// </remarks>
public static class CodeEditorCommands
{
    /// <summary>Asks the language server for completion at the caret.</summary>
    public const string TriggerSuggest = "editor.action.triggerSuggest";

    /// <summary>Asks the language server for the signature of the call around the caret.</summary>
    public const string TriggerParameterHints = "editor.action.triggerParameterHints";

    /// <summary>
    /// Opens the search bar and puts the keyboard in its query, which starts with the selection within one line, or the
    /// word at the caret, as <see cref="FindOptions.SeedSearchStringFromSelection"/> says.
    /// </summary>
    public const string Find = "actions.find";

    /// <summary>
    /// Opens the search bar with its replace row. A selection within one line becomes the query, and the keyboard then
    /// goes into the replacement, as it does when the query has it; otherwise into the query. Not in a read-only
    /// editor.
    /// </summary>
    public const string Replace = "editor.action.startFindReplaceAction";

    /// <summary>
    /// Selects the next match, opening the bar if it is closed, also beyond the matches it highlights. An empty query
    /// starts as for <see cref="Find"/>.
    /// </summary>
    public const string FindNext = "editor.action.nextMatchFindAction";

    /// <summary>Selects the previous match, as <see cref="FindNext"/> selects the next one.</summary>
    public const string FindPrevious = "editor.action.previousMatchFindAction";

    /// <summary>
    /// Turns the search bar's Match Case on or off, also while the bar is closed, for the next search. An open bar
    /// searches again and leaves the selection where it is.
    /// </summary>
    public const string ToggleFindCaseSensitive = "toggleFindCaseSensitive";

    /// <summary>Turns Match Whole Word on or off, as <see cref="ToggleFindCaseSensitive"/> does Match Case.</summary>
    public const string ToggleFindWholeWord = "toggleFindWholeWord";

    /// <summary>
    /// Turns Use Regular Expression on or off, as <see cref="ToggleFindCaseSensitive"/> does Match Case.
    /// </summary>
    public const string ToggleFindRegex = "toggleFindRegex";

    /// <summary>
    /// Turns Preserve Case on or off, as <see cref="ToggleFindCaseSensitive"/> does Match Case: a replacement without
    /// groups then takes the case of each match, so that replacing <c>foo</c> with <c>bar</c> turns <c>FOO</c> into
    /// <c>BAR</c>.
    /// </summary>
    public const string TogglePreserveCase = "togglePreserveCase";

    /// <summary>
    /// Turns Find in Selection on for the selected lines, or off, also while the bar is closed, and selects the first
    /// match from where the search started. Without a selection it stays off; closing the bar turns it off.
    /// </summary>
    public const string ToggleFindInSelection = "toggleFindInSelection";

    /// <summary>
    /// Comments the selected lines, or the caret's, with the language's line comment, or uncomments them when all
    /// are commented; in a language without one, toggles its block comment.
    /// </summary>
    public const string CommentLine = "editor.action.commentLine";

    /// <summary>
    /// Moves the caret to the start of the word before it, past a single separator right after a word character, such
    /// as the dot of <c>foo.</c>. At a line's start, it goes on from the end of the line before.
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
    /// Deletes the selection, or else back to the start of the word before the caret; an empty pair that the editor
    /// closed goes as a whole, two or more spaces before the caret go alone, and at a line's start, the line break.
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
    /// Toggles the language's block comment: removes the one that the selection is in, or else puts one around the
    /// selection, which stays on the commented text; without a selection, an empty comment goes in, with the caret in
    /// it.
    /// </summary>
    public const string BlockComment = "editor.action.blockComment";

    /// <summary>
    /// Moves the selected lines, or the caret's, above the line before them, with the selection. With the language's
    /// indentation rules and its <see cref="CodeLanguage.AutoIndent"/> at <see cref="AutoIndent.Full"/>, they take the
    /// indentation of their new place.
    /// </summary>
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

    /// <summary>
    /// Undoes the document's last step, as <see cref="CodeDocument.Undo"/> does, and brings the caret into view.
    /// </summary>
    public const string Undo = "undo";

    /// <summary>
    /// Redoes the last step undone, as <see cref="CodeDocument.Redo"/> does, and brings the caret into view.
    /// </summary>
    public const string Redo = "redo";

    /// <summary>
    /// Moves the caret to the bracket that matches the one at the start of the selection; elsewhere to the closing
    /// bracket of the pair around it, or else to the next bracket. Brackets in strings and comments don't count.
    /// </summary>
    public const string JumpToBracket = "editor.action.jumpToBracket";

    /// <summary>
    /// Selects the pair at the start of the selection, or else the pair around it, or else the next pair, with the text
    /// between its brackets; backward from a closing bracket. Without a pair it does not apply. No key by default.
    /// </summary>
    public const string SelectToBracket = "editor.action.selectToBracket";

    /// <summary>
    /// Expands the selection to the next range around it: the language server's selection ranges, or without them the
    /// pairs of brackets, and the words, the lines, and the whole text. In a rectangle selection it does not apply.
    /// </summary>
    public const string SmartSelectExpand = "editor.action.smartSelect.expand";

    /// <summary>Shrinks the selection back through the ranges that <see cref="SmartSelectExpand"/> went through.</summary>
    public const string SmartSelectShrink = "editor.action.smartSelect.shrink";

    /// <summary>
    /// Moves the caret to the start of the next occurrence of the symbol at the caret, after the last to the first,
    /// while they are highlighted (<see cref="CodeEditorOptions.OccurrencesHighlight"/>); otherwise it does not apply.
    /// </summary>
    public const string WordHighlightNext = "editor.action.wordHighlight.next";

    /// <summary>Moves the caret to the start of the previous occurrence, as <see cref="WordHighlightNext"/> does.</summary>
    public const string WordHighlightPrevious = "editor.action.wordHighlight.prev";
}
