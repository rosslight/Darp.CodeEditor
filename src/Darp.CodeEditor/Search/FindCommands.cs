using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The search bar's commands in one editor's view of a document: they apply from the text and from the bar, and start
/// with the selected text within one line.
/// </summary>
internal sealed class FindCommands : IEditorFeature
{
    private readonly TextArea _textArea;

    /// <summary>The editor's search bar; null until it is first needed.</summary>
    private readonly Func<SearchBar?> _bar;

    /// <summary>The editor's search bar, made the first time it is needed; null without a place for it.</summary>
    private readonly Func<SearchBar?> _createBar;

    public FindCommands(TextArea textArea, Func<SearchBar?> bar, Func<SearchBar?> createBar)
    {
        _textArea = textArea;
        _bar = bar;
        _createBar = createBar;
    }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.Find, () => Open(replace: false), CommandNeeds.None);
        // A read-only editor opens the search bar without its replace row.
        commands.Add(CodeEditorCommands.Replace, () => Open(replace: true), CommandNeeds.None);
        commands.Add(CodeEditorCommands.FindNext, () => FindNext(previous: false), CommandNeeds.None);
        commands.Add(CodeEditorCommands.FindPrevious, () => FindNext(previous: true), CommandNeeds.None);
    }

    public void Dispose() { }

    /// <summary>Closes the search bar, as Escape does.</summary>
    /// <returns>Whether it was open.</returns>
    public bool Close()
    {
        if (_bar() is not { IsOpen: true } bar)
            return false;
        bar.Close();
        return true;
    }

    private bool Open(bool replace)
    {
        if (_createBar() is not { } bar)
            return false;
        bar.Open(replace && !_textArea.IsReadOnly, SelectedLineText());
        return true;
    }

    /// <summary>Selects the next or previous match, opening the bar for the selection if it is closed.</summary>
    private bool FindNext(bool previous)
    {
        if (_createBar() is not { } bar)
            return false;
        string? text = bar.IsOpen ? null : SelectedLineText();
        if (previous)
            bar.FindPrevious(text);
        else
            bar.FindNext(text);
        return true;
    }

    /// <summary>The selected text, if it is within one line: what a search starts with.</summary>
    private string? SelectedLineText() =>
        _textArea.Selection is { IsEmpty: false, IsMultiline: false } selection ? selection.GetText() : null;
}
