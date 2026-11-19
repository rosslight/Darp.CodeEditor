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
/// line's hidden tail, does not go in, and additional edits that would are left out.
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
        if (!CanChange(main))
            return;
        var step = new object();
        var mark = new SnippetMark();
        List<CompletionEdit> edits = WithoutOverlaps([main], Mapped(request.Snapshot, item.AdditionalEdits ?? []));
        Apply(step, continued: false, edits, snippet is null ? null : mark);
        // The item's text moved by the additional edits before it.
        int start =
            main.Range.Start
            + edits.Skip(1).Where(e => e.Range.End <= main.Range.Start).Sum(e => e.NewText.Length - e.Range.Length);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = start + main.NewText.Length;
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
            Apply(step, continued: true, WithoutOverlaps([], Mapped(snapshot, additional)), mark: null);
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
    /// Applies edits through <see cref="TextChanges"/>, from the last to the first, each a change of its own, in the undo
    /// step <paramref name="step"/>: a new one, or the last one continued. One change from an import at the top to an
    /// item far below would replace all the text between them. A snippet's <paramref name="mark"/> goes in first, so
    /// that undoing the step reaches it last, once the text is back.
    /// </summary>
    private void Apply(object step, bool continued, List<CompletionEdit> edits, SnippetMark? mark)
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
                foreach (CompletionEdit edit in edits.OrderByDescending(e => e.Range.Start))
                    _changes.Apply([new TextEdit(edit.Range.Start, edit.Range.Length, edit.NewText)]);
            }
        }
        finally
        {
            undo.EndUndoGroup();
        }
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
