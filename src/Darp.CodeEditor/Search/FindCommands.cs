using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Search;

/// <summary>
/// The search bar's commands in one editor's view of a document. They need no text focus, so that their keys work from
/// the bar too, and they apply wherever the editor has a place for the bar.
/// </summary>
internal sealed class FindCommands : IEditorFeature
{
    private readonly TextArea _area;
    private readonly Func<SearchBar?> _bar;

    /// <param name="area">The text area searched.</param>
    /// <param name="bar">The editor's bar, made the first time it is needed; null without a place for it.</param>
    public FindCommands(TextArea area, Func<SearchBar?> bar)
    {
        _area = area;
        _bar = bar;
    }

    public void Dispose() { }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.Find, () => Open(replace: false), CommandNeeds.None, HasBar);
        commands.Add(CodeEditorCommands.Replace, () => Open(replace: true), CommandNeeds.None, HasBar);
        commands.Add(CodeEditorCommands.FindNext, () => Go(previous: false), CommandNeeds.None, HasBar);
        commands.Add(CodeEditorCommands.FindPrevious, () => Go(previous: true), CommandNeeds.None, HasBar);
    }

    private bool HasBar() => _bar() is not null;

    private void Open(bool replace)
    {
        if (_bar() is { } bar)
            bar.Open(replace && !_area.IsReadOnly, Query(bar));
    }

    /// <summary>Selects the next or previous match, opening the bar with its query if it is closed.</summary>
    private void Go(bool previous)
    {
        if (_bar() is not { } bar)
            return;
        string? text = bar.IsOpen ? null : Query(bar);
        if (previous)
            bar.FindPrevious(text);
        else
            bar.FindNext(text);
    }

    /// <summary>
    /// What a search starts with: the selected text, if it is within one line; with nothing selected and the bar
    /// closed, the word at the caret. An open bar keeps what the user typed.
    /// </summary>
    private string? Query(SearchBar bar)
    {
        if (_area.Selection is { IsEmpty: false } selection)
            return selection.IsMultiline ? null : selection.GetText();
        TextDocument text = _area.Document;
        return !bar.IsOpen && WordRules.WordAt(text, _area.Caret.Offset) is { } word
            ? text.GetText(word.Start, word.Length)
            : null;
    }
}
