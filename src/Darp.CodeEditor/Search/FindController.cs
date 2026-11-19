using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The search bar's commands in one editor's view of a document, as VS Code's find controller
/// (<c>findController.ts</c>): they open the bar with its query from the text, as its <c>getSelectionSearchString</c>
/// takes it, go through the matches and turn the bar's options on and off, from the text and from the bar. The bar
/// searches the document (<see cref="DocumentSearch"/>) from the first time it is needed until the view ends.
/// </summary>
internal sealed class FindController : IEditorFeature
{
    /// <summary>The longest selection that the query takes.</summary>
    private const int MaxSeedLength = 524_288;

    private readonly EditorContext _context;
    private readonly TextChanges _changes;
    private TextAreaColors? _colors;

    /// <summary>The search of the document, for the bar, from the first time the bar is needed.</summary>
    private DocumentSearch? _search;

    public FindController(EditorContext context, TextChanges changes)
    {
        _context = context;
        _changes = changes;
        if (context.SearchBar() is { } bar)
            _search = new DocumentSearch(context, changes, bar, null);
    }

    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            _search?.Colors = value;
        }
    }

    private TextArea Area => _context.Area;

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.Find, () => Open(replace: false), CommandNeeds.None);
        commands.Add(CodeEditorCommands.Replace, () => Open(replace: true), CommandNeeds.Writable);
        commands.Add(CodeEditorCommands.FindNext, () => Start(SearchMove.Next), CommandNeeds.None);
        commands.Add(CodeEditorCommands.FindPrevious, () => Start(SearchMove.Previous), CommandNeeds.None);
        commands.Add(
            CodeEditorCommands.ToggleFindCaseSensitive,
            () => Toggle(SearchOption.MatchCase),
            CommandNeeds.None
        );
        commands.Add(CodeEditorCommands.ToggleFindWholeWord, () => Toggle(SearchOption.WholeWord), CommandNeeds.None);
        commands.Add(CodeEditorCommands.ToggleFindRegex, () => Toggle(SearchOption.Regex), CommandNeeds.None);
        commands.Add(CodeEditorCommands.TogglePreserveCase, () => Toggle(SearchOption.PreserveCase), CommandNeeds.None);
        commands.Add(CodeEditorCommands.ToggleFindInSelection, ToggleFindInSelection, CommandNeeds.None);
    }

    /// <summary>Closes the bar, as Escape does.</summary>
    /// <returns>Whether it was open.</returns>
    public bool Close()
    {
        if (_context.SearchBar() is not { IsOpen: true } bar)
            return false;
        bar.Close();
        return true;
    }

    /// <summary>
    /// Hides the replace row of an editor that became read-only, which ends replace mode: it stays hidden once the
    /// editor can be edited again.
    /// </summary>
    public void OnReadOnly()
    {
        _context.SearchBar()?.HideReplaceRow();
        _search?.UpdateStatus();
    }

    public void Dispose() => _search?.Dispose();

    /// <summary>
    /// The editor's bar and the search of the document, made the first time they are needed; null without a place for
    /// the bar.
    /// </summary>
    private (SearchBar Bar, DocumentSearch Search)? Bar()
    {
        if (_context.CreateSearchBar() is not { } bar)
            return null;
        _search ??= new DocumentSearch(_context, _changes, bar, _colors);
        return (bar, _search);
    }

    /// <summary>
    /// Opens the bar, as Ctrl+F does, or with its replace row, as Ctrl+H does. Ctrl+F takes its query from the text as
    /// <see cref="FindOptions.SeedSearchStringFromSelection"/> says, and puts the keyboard into it. Ctrl+H takes only a
    /// selection, and then puts the keyboard into the replacement, as it does when the query has it.
    /// </summary>
    private bool Open(bool replace)
    {
        if (Bar() is not (var bar, var search))
            return false;
        SeedSearchString seeds = _context.Options().Find.SeedSearchStringFromSelection;
        if (!replace)
        {
            Show(bar, search, replace, Seed(seeds), BarFocus.Query, SearchMove.None);
            return true;
        }
        bool fromQuery = bar.IsQueryFocused;
        string? seed = fromQuery || Area.Selection.IsEmpty ? null : Seed(seeds);
        BarFocus focus = fromQuery || seed is not null ? BarFocus.Replacement : BarFocus.Query;
        Show(bar, search, replace, seed, focus, SearchMove.None);
        return true;
    }

    /// <summary>
    /// Selects the next or previous match, as F3 and Shift+F3 do, opening the bar if it is closed, and leaves the
    /// keyboard where it is. The query stays: only an empty one is taken from the text.
    /// </summary>
    private bool Start(SearchMove move)
    {
        if (Bar() is not (var bar, var search))
            return false;
        string? seed = bar.Query is null ? Seed(_context.Options().Find.SeedSearchStringFromSelection) : null;
        if (bar.IsOpen && seed is null)
            search.Go(move);
        else
            Show(bar, search, replace: false, seed, BarFocus.None, move);
        return true;
    }

    /// <summary>
    /// Opens the bar with <paramref name="seed"/> as its query, escaped while it uses a regular expression: the search
    /// starts again from the caret, without moving it unless <paramref name="move"/> says so, and searches only the
    /// selection as <see cref="FindOptions.AutoFindInSelection"/> says.
    /// </summary>
    private void Show(SearchBar bar, DocumentSearch search, bool replace, string? seed, BarFocus focus, SearchMove move)
    {
        search.StartFromCaret();
        AutoFindInSelection auto = _context.Options().Find.AutoFindInSelection;
        bool scoped =
            (
                auto == AutoFindInSelection.Always
                || (auto == AutoFindInSelection.Multiline && Area.Selection.IsMultiline)
            ) && search.ScopeToSelection();
        string? query = seed is not null && bar.UsesRegex ? SearchQuery.Escape(seed) : seed;
        if (bar.Open(replace, query, focus, _context.Keymap()) || scoped)
            search.Search(move);
        else
            search.Go(move);
    }

    /// <summary>
    /// What the query takes from the text: the selection within one line, unless it is too long, or without a selection
    /// the word at the caret, as <paramref name="seeds"/> says.
    /// </summary>
    private string? Seed(SeedSearchString seeds)
    {
        Selection selection = Area.Selection;
        if (seeds == SeedSearchString.Never || selection.IsMultiline)
            return null;
        if (!selection.IsEmpty)
            return selection.Length < MaxSeedLength ? selection.GetText() : null;
        if (seeds == SeedSearchString.Selection)
            return null;
        return WordRules.WordAt(Area.Document, Area.Caret.Offset) is { } word
            ? Area.Document.GetText(word.Start, word.Length)
            : null;
    }

    /// <summary>
    /// Turns one of the bar's options on or off, as its key does, also while the bar is closed. Unlike a click on the
    /// toggle, it leaves the selection where it is.
    /// </summary>
    private bool Toggle(SearchOption option)
    {
        if (Bar() is not (var bar, var search))
            return false;
        bar.Toggle(option);
        search.Search(SearchMove.None);
        return true;
    }

    /// <summary>
    /// Turns Find in Selection on for the selection, as Alt+L does, or off; also while the bar is closed.
    /// </summary>
    private bool ToggleFindInSelection()
    {
        if (Bar() is not (_, var search))
            return false;
        search.FindInSelection(!search.IsInSelection);
        return true;
    }
}
