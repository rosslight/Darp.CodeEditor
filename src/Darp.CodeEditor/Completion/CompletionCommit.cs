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
/// completion was asked left it. Additional edits that a resolve still under way brings come later. The edits go in
/// through <see cref="TextChanges"/>: an item whose text would touch read-only text, such as protected text or a long
/// line's hidden tail, does not go in, and additional edits that would are left out. They go in apart from a lone
/// carriage return next to them, which would join a line break of theirs (<see cref="LineBreaks.Apart"/>).
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
    /// <param name="request">The request that its list answered.</param>
    /// <param name="replace">Whether it replaces, rather than inserts.</param>
    /// <param name="late">The resolve that brings its additional edits later; null when they are known.</param>
    public void Commit(CompletionItem item, CompletionRequest request, bool replace, ItemResolve? late)
    {
        int caret = _textArea.Caret.Offset;
        // Text typed at the caret goes in the range, and once the rest of the word is deleted, also by another editor or
        // code, the text after it stays out.
        TextRange since = TextRange.FromBounds(request.Offset, replace ? item.ReplaceRange.End : item.Edit.Range.End);
        int end = _document.MapToCurrent(request.Snapshot, since).End;
        var range = TextRange.FromBounds(
            item.Edit.Range.Start,
            Math.Min(_buffer.GetLineByOffset(caret).EndOffset, Math.Max(caret, end))
        );
        RenderedSnippet? snippet = item.Snippet is { } source ? _snippets.Render(source, range.Start) : null;
        var main = new CompletionEdit(range, snippet?.Text ?? InLineBreaks(item.Edit.NewText));
        // In order, the item's edit marked; an additional edit at the item's start goes before its text.
        (CompletionEdit Edit, bool IsMain)[] ordered =
        [
            .. WithoutOverlaps([main], Mapped(request.Snapshot, item.AdditionalEdits ?? []))
                .Select((edit, i) => (edit, i == 0))
                .OrderBy(e => e.Item1.Range.Start)
                .ThenBy(e => e.Item2),
        ];
        TextEdit[] edits = Apart(ordered.Select(e => e.Edit));
        // A lone carriage return that goes along with an edit may be read-only too.
        if (!CanChange(main) || !edits.All(e => _changes.CanChange(e.Offset, e.Length)))
        {
            // Taken out of the resolver for the edits it brings, the resolve is now nobody's: it ends here.
            late?.Cancellation.Cancel();
            return;
        }
        var step = new object();
        var mark = new SnippetMark();
        Apply(step, continued: false, edits, snippet is null ? null : mark);
        // Where the item's text went: moved by the edits before it, and after a line break that went along with it.
        int index = Array.FindIndex(ordered, e => e.IsMain);
        TextEdit placed = edits[index];
        int at = placed.Offset + edits.Take(index).Sum(e => e.Text.Length - e.Length);
        int start = at + (placed.Offset < main.Range.Start ? _document.LineBreak.Length : 0);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = at + placed.Text.Length;
        _textArea.Caret.BringCaretToView();
        if (snippet is not null)
            _snippets.Start(snippet, start, mark);
        if (late is not null)
            ApplyLateEdits(late, step, request.Snapshot);
    }

    /// <summary>
    /// Applies the additional edits that a committed item's resolve brings, in the commit's undo step, if that step is
    /// still the last one; drops them otherwise.
    /// </summary>
    private async void ApplyLateEdits(ItemResolve late, object step, TextSnapshot snapshot)
    {
        _lateEdits?.Cancel();
        _lateEdits = late.Cancellation;
        try
        {
            CompletionItem? item = await late.Task;
            if (
                late.Cancellation.IsCancellationRequested
                || item?.AdditionalEdits is not { Count: > 0 } additional
                || _buffer.UndoStack.LastGroupDescriptor != step
            )
            {
                return;
            }
            TextEdit[] edits = Apart(WithoutOverlaps([], Mapped(snapshot, additional)).OrderBy(e => e.Range.Start));
            Apply(step, continued: true, [.. edits.Where(e => _changes.CanChange(e.Offset, e.Length))], mark: null);
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
    /// top to an item far below would replace all the text between them. A snippet's <paramref name="mark"/> goes in
    /// first, so that undoing the step reaches it last, once the text is back.
    /// </summary>
    private void Apply(object step, bool continued, TextEdit[] edits, SnippetMark? mark)
    {
        UndoStack undo = _buffer.UndoStack;
        if (continued)
            undo.StartContinuedUndoGroup(step);
        else
            undo.StartUndoGroup(step);
        try
        {
            if (mark is not null)
                undo.Push(mark);
            using (_buffer.RunUpdate())
            {
                for (int i = edits.Length - 1; i >= 0; i--)
                    _changes.Apply([edits[i]]);
            }
        }
        finally
        {
            undo.EndUndoGroup();
        }
    }

    /// <summary>
    /// <paramref name="edits"/>, in order, apart from a lone carriage return next to them (<see cref="LineBreaks.Apart"/>).
    /// </summary>
    private TextEdit[] Apart(IEnumerable<CompletionEdit> edits) =>
        [
            .. LineBreaks.Apart(
                _buffer,
                [.. edits.Select(e => new TextEdit(e.Range.Start, e.Range.Length, e.NewText))],
                _document.LineBreak
            ),
        ];

    /// <summary>
    /// Edits in the text of <paramref name="snapshot"/>, moved to the current text, with the document's line breaks.
    /// </summary>
    private IEnumerable<CompletionEdit> Mapped(TextSnapshot snapshot, IEnumerable<CompletionEdit> edits) =>
        edits.Select(edit => new CompletionEdit(
            _document.MapToCurrent(snapshot, edit.Range),
            InLineBreaks(edit.NewText)
        ));

    private string InLineBreaks(string text) => TextUtilities.NormalizeNewLines(text, _document.LineBreak);

    /// <summary>
    /// <paramref name="first"/> and the edits that overlap none before them and touch no read-only text.
    /// </summary>
    private List<CompletionEdit> WithoutOverlaps(List<CompletionEdit> first, IEnumerable<CompletionEdit> more)
    {
        foreach (CompletionEdit edit in more)
        {
            if (
                CanChange(edit)
                && first.TrueForAll(other => edit.Range.End <= other.Range.Start || edit.Range.Start >= other.Range.End)
            )
                first.Add(edit);
        }
        return first;
    }

    private bool CanChange(CompletionEdit edit) => _changes.CanChange(edit.Range.Start, edit.Range.Length);

    public void Dispose() => _lateEdits?.Cancel();
}
