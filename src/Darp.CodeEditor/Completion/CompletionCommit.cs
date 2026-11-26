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
/// completion was asked left it. Additional edits that a resolve still under way brings come later, as in VS Code,
/// unless the commit was undone. The edits go in through <see cref="TextChanges"/>: an item whose text would touch
/// read-only text, such as protected text or a long line's hidden tail, does not go in. When one of its additional
/// edits would, or would go over text that changed since its list was asked for, none of them does, as VS Code applies
/// them together, and the item's own text still goes in.
/// </summary>
internal sealed class CompletionCommit : IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly SnippetController _snippets;
    private readonly TextChanges _changes;

    /// <summary>Runs a committed item's command.</summary>
    private readonly Action<string> _runCommand;

    /// <summary>Whether the view ended: a command still posted no longer runs.</summary>
    private bool _disposed;

    /// <summary>Cancel the resolves of the committed items whose additional edits are still to come, one per commit.</summary>
    private readonly HashSet<CancellationTokenSource> _lateEdits = [];

    /// <param name="textArea">The text area of the view.</param>
    /// <param name="document">The view's document.</param>
    /// <param name="snippets">The view's snippets.</param>
    /// <param name="changes">How the view edits its document.</param>
    /// <param name="runCommand">Runs a committed item's command, once the key that committed is handled.</param>
    public CompletionCommit(
        TextArea textArea,
        CodeDocument document,
        SnippetController snippets,
        TextChanges changes,
        Action<string> runCommand
    )
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _snippets = snippets;
        _changes = changes;
        _runCommand = runCommand;
    }

    /// <summary>
    /// Commits an item: its edit, from where it starts to where its insert range, or its replace range for
    /// <paramref name="replace"/>, ends as the edits since completion was asked moved it, or to the caret where that is
    /// further, and its additional edits, in one step that the user undoes; the caret goes after the item's text. A
    /// snippet goes in as its text, and its first stop is selected. Additional edits that overlap others are left out,
    /// and stay plain text. Additional edits that a resolve still under way brings come later. Once the item went in,
    /// and the key that committed it is handled, its command runs.
    /// </summary>
    /// <param name="item">The item, as it was resolved.</param>
    /// <param name="answer">The answer that its list is.</param>
    /// <param name="replace">Whether it replaces, rather than inserts.</param>
    /// <param name="late">The resolve that brings its additional edits later; null when they are known.</param>
    public void Commit(CompletionItem item, CompletionAnswer answer, bool replace, ItemResolve? late)
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
        RenderedSnippet? snippet = item.Snippet is { } source ? _snippets.Render(source, range.Start) : null;
        var main = new CompletionEdit(range, snippet?.Text ?? InLineBreaks(item.Edit.NewText));
        if (!CanChange(main))
        {
            // Taken out of the resolver for the edits it brings, the resolve is now nobody's: it ends here.
            late?.Cancellation.Cancel();
            return;
        }
        List<CompletionEdit> edits = Mapped(request.Snapshot, item.AdditionalEdits ?? []) is { } additional
            ? WithoutOverlaps([main], additional)
            : [main];
        if (!edits.TrueForAll(CanChange))
            edits = [main];
        // The item's text moved by the additional edits before it.
        int start =
            main.Range.Start
            + edits.Skip(1).Where(e => e.Range.End <= main.Range.Start).Sum(e => e.NewText.Length - e.Range.Length);
        var step = new object();
        var mark = new SnippetMark();
        var marks = new List<IUndoableOperation>();
        if (snippet is not null)
            marks.Add(mark);
        // Undoing the commit drops the additional edits still to come, as they would go in without the item.
        if (late is not null)
        {
            _lateEdits.Add(late.Cancellation);
            marks.Add(new LateEditsMark(late.Cancellation));
        }
        Apply(step, continued: false, edits, marks);
        _textArea.ClearSelection();
        _textArea.Caret.Offset = start + main.NewText.Length;
        _textArea.Caret.BringCaretToView();
        if (snippet is not null)
            _snippets.Start(snippet, start, mark);
        if (late is not null)
            ApplyLateEdits(late, step, request.Snapshot);
        if (item.Command is { } command)
        {
            _textArea.Dispatcher.Post(() =>
            {
                if (!_disposed)
                    _runCommand(command);
            });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (CancellationTokenSource lateEdits in _lateEdits)
            lateEdits.Cancel();
    }

    /// <summary>
    /// Applies the additional edits that a committed item's resolve brings: in the commit's undo step while that is the
    /// last one, and otherwise, as the user typed on, in a step of their own.
    /// </summary>
    private async void ApplyLateEdits(ItemResolve late, object step, TextSnapshot snapshot)
    {
        try
        {
            CompletionItem? item = await late.Task;
            if (
                late.Cancellation.IsCancellationRequested
                || item?.AdditionalEdits is not { Count: > 0 } additional
                || Mapped(snapshot, additional) is not { } mapped
            )
                return;
            List<CompletionEdit> edits = WithoutOverlaps([], mapped);
            bool last = _buffer.UndoStack.LastGroupDescriptor == step;
            if (edits.TrueForAll(CanChange))
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
            _lateEdits.Remove(late.Cancellation);
        }
    }

    /// <summary>
    /// Applies edits through <see cref="TextChanges"/>, from the last to the first, each a change of its own, in the undo
    /// step <paramref name="step"/>: a new one, or the last one continued. One change from an import at the top to an
    /// item far below would replace all the text between them. The <paramref name="marks"/>, such as a snippet's, go in
    /// first, so that undoing the step reaches them last, once the text is back.
    /// </summary>
    private void Apply(object step, bool continued, List<CompletionEdit> edits, List<IUndoableOperation> marks)
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
    /// Edits in the text of <paramref name="snapshot"/>, each over its whole range moved to the current text, with the
    /// document's line breaks. Null when the text that one goes over changed since, by the user or by another commit's
    /// edits: going over it would undo that change.
    /// </summary>
    private List<CompletionEdit>? Mapped(TextSnapshot snapshot, IEnumerable<CompletionEdit> edits)
    {
        var since = new SnapshotEdits(snapshot, _document.CurrentSnapshot);
        var mapped = new List<CompletionEdit>();
        foreach (CompletionEdit edit in edits)
        {
            TextRange range = since.Map(edit.Range);
            if (_document.CurrentSnapshot.GetText(range) != snapshot.GetText(edit.Range))
                return null;
            mapped.Add(new CompletionEdit(range, InLineBreaks(edit.NewText)));
        }
        return mapped;
    }

    private string InLineBreaks(string text) => TextUtilities.NormalizeNewLines(text, _document.LineBreak);

    /// <summary><paramref name="first"/> and the edits that overlap none before them.</summary>
    private static List<CompletionEdit> WithoutOverlaps(List<CompletionEdit> first, IEnumerable<CompletionEdit> more)
    {
        foreach (CompletionEdit edit in more)
        {
            if (first.TrueForAll(other => edit.Range.End <= other.Range.Start || edit.Range.Start >= other.Range.End))
                first.Add(edit);
        }
        return first;
    }

    private bool CanChange(CompletionEdit edit) => _changes.CanChange(edit.Range.Start, edit.Range.Length);

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
