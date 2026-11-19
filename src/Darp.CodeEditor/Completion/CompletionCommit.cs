using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// Commits completion's items into the text: an item's edit and its additional edits, such as an import, in one step
/// that the user undoes, with the caret after its text. Inserting, the item's text goes over its edit's range, its insert
/// range, or the word up to the caret; replacing, over its replace range, or the whole word, as far as the edits since
/// completion was asked left it. Additional edits that a resolve still under way brings come later, as in VS Code:
/// unless the text changed before where one of them goes since the commit, or the commit was undone. The edits go in
/// through <see cref="TextChanges"/>: an item whose text would touch read-only text, such as protected text or a long
/// line's hidden tail, does not go in. When one of its additional edits would, none of them does, as VS Code applies
/// them together, and the item's own text still goes in. They go in apart from a lone carriage return next to them,
/// which would join a line break of theirs (<see cref="LineBreaks.Apart"/>), unless that return is read-only. A handler of
/// any notification of the commit, of an edit, the selection, the caret or a snippet's first stop, that shows another
/// document ends the commit there: nothing of it touches that one.
/// </summary>
internal sealed class CompletionCommit : IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SnippetController _snippets;
    private readonly TextChanges _changes;

    /// <summary>Cancels the resolve of the item committed last, whose additional edits are still to come.</summary>
    private CancellationTokenSource? _lateEdits;

    public CompletionCommit(TextArea textArea, CodeDocument document, SnippetController snippets, TextChanges changes)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _snippets = snippets;
        _changes = changes;
    }

    /// <summary>
    /// Commits an item: its edit, from where it starts to where its insert range, or its replace range for
    /// <paramref name="replace"/>, ends as the edits since completion was asked moved it, or to the caret where that is
    /// further, and its additional edits, in one step that the user undoes; the caret goes after the item's text. A
    /// snippet goes in as its text, with its variables' values for the text before the commit, and its first stop is
    /// selected. Additional edits that overlap others are left out, and stay plain text. Additional edits that a resolve
    /// still under way brings come later.
    /// </summary>
    /// <param name="item">The item, as it was resolved.</param>
    /// <param name="answer">The answer that its list is.</param>
    /// <param name="replace">Whether it replaces, rather than inserts.</param>
    /// <param name="late">The resolve that brings its additional edits later; null when they are known.</param>
    /// <param name="selected">The selection that typing replaced, for a snippet's selected text; null for none.</param>
    /// <returns>Whether the editor still shows the document: a handler of the change may show another one.</returns>
    public bool Commit(
        CompletionItem item,
        CompletionAnswer answer,
        bool replace,
        ItemResolve? late,
        SelectedText? selected
    )
    {
        CompletionRequest request = answer.Request;
        int caret = _textArea.Caret.Offset;
        // Text typed at the caret goes in the range, and once the rest of the word is deleted, also by another editor or
        // code, the text after it stays out.
        TextRange since = TextRange.FromBounds(request.Offset, replace ? item.ReplaceRange.End : item.Edit.Range.End);
        int end = _document.MapToCurrent(request.Snapshot, since).End;
        var range = TextRange.FromBounds(
            item.Edit.Range.Start,
            Math.Min(_buffer.GetLineByOffset(caret).EndOffset, Math.Max(caret, end))
        );
        RenderedSnippet? snippet = item.Snippet is { } source
            ? _snippets.Render(source, range.Start, selected, answer.Clipboard)
            : null;
        var main = new CompletionEdit(range, snippet?.Text ?? InLineBreaks(item.Edit.NewText));
        // In order, the item's edit marked; an additional edit at the item's start goes before its text.
        (CompletionEdit Edit, bool IsMain)[] ordered =
        [
            .. WithoutOverlaps([main], Mapped(request.Snapshot, item.AdditionalEdits ?? []))
                .Select((edit, i) => (edit, i == 0))
                .OrderBy(e => e.Item1.Range.Start)
                .ThenBy(e => e.Item2),
        ];
        if (!CanChange(main))
        {
            // Taken out of the resolver for the edits it brings, the resolve is now nobody's: it ends here.
            late?.Cancellation.Cancel();
            return true;
        }
        TextEdit[] edits = Apart(ordered.Select(e => e.Edit));
        if (!edits.All(CanChange))
        {
            ordered = [(main, true)];
            edits = Apart([main]);
        }
        // Where the item's text goes: moved by the edits before it, and after a line break that goes along with it.
        int index = Array.FindIndex(ordered, e => e.IsMain);
        TextEdit placed = edits[index];
        int at = placed.Offset + edits.Take(index).Sum(e => e.Text.Length - e.Length);
        int start = at + (placed.Offset < main.Range.Start ? _document.LineBreak.Length : 0);
        TextSnapshot before = _document.CurrentSnapshot;
        var step = new object();
        var mark = new SnippetMark();
        var marks = new List<IUndoableOperation>();
        if (snippet is not null)
            marks.Add(mark);
        // Undoing the commit drops the additional edits still to come, as they would go in without the item.
        if (late is not null)
            marks.Add(new LateEditsMark(late.Cancellation));
        // Each step stops once a handler of the one before shows another document.
        bool shown =
            Apply(step, continued: false, edits, marks)
            && _changes.WhileShown(_textArea.ClearSelection)
            && _changes.WhileShown(() => _textArea.Caret.Offset = at + placed.Text.Length)
            && _changes.WhileShown(_textArea.Caret.BringCaretToView)
            && (snippet is null || _changes.WhileShown(() => _snippets.Start(snippet, start, mark)));
        if (!shown)
        {
            late?.Cancellation.Cancel();
            return false;
        }
        if (late is not null)
            ApplyLateEdits(late, step, request.Snapshot, before);
        // As VS Code's choices go on to the next stop.
        if (answer.IsChoice)
            _snippets.Next();
        return true;
    }

    /// <summary>
    /// Applies the additional edits that a committed item's resolve brings: in the commit's undo step while that is the
    /// last one, and otherwise, as the user typed on, in a step of their own. They are dropped when a change since the
    /// commit, its own edit included, started before where one of them goes, as by typing above an import: their
    /// places may no longer be what the server meant.
    /// </summary>
    /// <param name="late">The resolve, which the commit's undo cancels.</param>
    /// <param name="step">The commit's undo step.</param>
    /// <param name="snapshot">The text that the item's list was asked for, which the edits are in.</param>
    /// <param name="before">The text before the commit.</param>
    private async void ApplyLateEdits(ItemResolve late, object step, TextSnapshot snapshot, TextSnapshot before)
    {
        _lateEdits?.Cancel();
        _lateEdits = late.Cancellation;
        try
        {
            CompletionItem? item = await late.Task;
            if (
                late.Cancellation.IsCancellationRequested
                || item?.AdditionalEdits is not { Count: > 0 } additional
                || !_changes.IsShown
            )
            {
                return;
            }
            var asked = new SnapshotEdits(snapshot, before);
            var since = new SnapshotEdits(before, _document.CurrentSnapshot);
            if (additional.Any(edit => since.ChangeBefore(asked.Map(edit.Range).Start)))
                return;
            TextEdit[] edits = Apart(WithoutOverlaps([], Mapped(snapshot, additional)).OrderBy(e => e.Range.Start));
            bool last = _buffer.UndoStack.LastGroupDescriptor == step;
            if (edits.All(CanChange))
                Apply(last ? step : new object(), continued: last, edits, []);
        }
        catch (Exception e)
        {
            Logger
                .TryGet(LogEventLevel.Error, AssetFiles.LogArea)
                ?.Log(this, "Applying an item's additional edits failed: {Exception}", e);
        }
        finally
        {
            if (_lateEdits == late.Cancellation)
                _lateEdits = null;
        }
    }

    /// <summary>
    /// Applies edits, in order, through <see cref="TextChanges"/>, from the last to the first, each a change of its own,
    /// in the undo step <paramref name="step"/>: a new one, or the last one continued. One change from an import at the
    /// top to an item far below would replace all the text between them. The <paramref name="marks"/>, such as a
    /// snippet's, go in first, so that undoing the step reaches them last, once the text is back.
    /// </summary>
    /// <returns>Whether the editor still shows the document: the edits stop where a handler showed another one.</returns>
    private bool Apply(object step, bool continued, TextEdit[] edits, List<IUndoableOperation> marks)
    {
        UndoStack undo = _buffer.UndoStack;
        if (continued)
            undo.StartContinuedUndoGroup(step);
        else
            undo.StartUndoGroup(step);
        try
        {
            foreach (IUndoableOperation mark in marks)
                undo.Push(mark);
            // Each edit raises a change, and the update's end the text's change.
            return _changes.WhileShown(() =>
            {
                using (_buffer.RunUpdate())
                {
                    for (int i = edits.Length - 1; i >= 0; i--)
                    {
                        if (!_changes.WhileShown(() => _changes.Apply([edits[i]])))
                            break;
                    }
                }
            });
        }
        finally
        {
            undo.EndUndoGroup();
        }
    }

    /// <summary>
    /// <paramref name="edits"/>, in order, apart from a lone carriage return next to them (<see cref="LineBreaks.Apart"/>),
    /// unless taking it along would change read-only text: such an edit stays as it was.
    /// </summary>
    private TextEdit[] Apart(IEnumerable<CompletionEdit> edits)
    {
        TextEdit[] asWere = [.. edits.Select(e => new TextEdit(e.Range.Start, e.Range.Length, e.NewText))];
        TextEdit[] apart = [.. LineBreaks.Apart(_buffer, asWere, _document.LineBreak)];
        for (int i = 0; i < apart.Length; i++)
        {
            if (apart[i].Offset != asWere[i].Offset && !CanChange(apart[i]))
                apart[i] = asWere[i];
        }
        return apart;
    }

    /// <summary>
    /// Edits in the text of <paramref name="snapshot"/>, moved to the current text, with the document's line breaks.
    /// </summary>
    private IEnumerable<CompletionEdit> Mapped(TextSnapshot snapshot, IEnumerable<CompletionEdit> edits) =>
        edits.Select(edit => new CompletionEdit(
            _document.MapToCurrent(snapshot, edit.Range),
            InLineBreaks(edit.NewText)
        ));

    private string InLineBreaks(string text) => TextUtilities.NormalizeNewLines(text, _document.LineBreak);

    /// <summary><paramref name="first"/> and the edits that overlap none before them.</summary>
    private List<CompletionEdit> WithoutOverlaps(List<CompletionEdit> first, IEnumerable<CompletionEdit> more)
    {
        foreach (CompletionEdit edit in more)
        {
            if (first.TrueForAll(other => edit.Range.End <= other.Range.Start || edit.Range.Start >= other.Range.End))
                first.Add(edit);
        }
        return first;
    }

    private bool CanChange(CompletionEdit edit) => _changes.CanChange(edit.Range.Start, edit.Range.Length);

    private bool CanChange(TextEdit edit) => _changes.CanChange(edit.Offset, edit.Length);

    public void Dispose() => _lateEdits?.Cancel();

    /// <summary>
    /// The mark of a commit whose additional edits are still to come, in its undo step: undoing the step cancels their
    /// resolve. It holds no reference to the editor, so the document's undo history keeps no editor alive.
    /// </summary>
    private sealed class LateEditsMark(CancellationTokenSource lateEdits) : IUndoableOperation
    {
        private readonly CancellationTokenSource _lateEdits = lateEdits;

        public void Undo() => _lateEdits.Cancel();

        public void Redo() { }
    }
}
