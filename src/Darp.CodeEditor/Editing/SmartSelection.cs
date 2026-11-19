using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Expands and shrinks the selection, as VS Code's smart select does: through the ranges around the caret, found once
/// when expanding starts, until the caret moves otherwise. The ranges are the server's, where it offers them, with the
/// word ranges; otherwise the bracket ranges with the word ranges (<see cref="SelectionRangeProviders"/>).
/// </summary>
internal sealed class SmartSelection : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly TextArea _textArea;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>The ranges, the selection's first, and which one is selected; null until expanding starts.</summary>
    private (IReadOnlyList<TextRange> Ranges, int Index)? _state;

    /// <summary>Whether the selection is set from <see cref="_state"/>: that is no move of its own.</summary>
    private bool _selecting;

    public SmartSelection(
        CodeEditor editor,
        TextArea textArea,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter
    )
    {
        _editor = editor;
        _textArea = textArea;
        _document = document;
        _highlighter = highlighter;
        textArea.Caret.PositionChanged += OnCaretMoved;
    }

    public void Dispose()
    {
        _textArea.Caret.PositionChanged -= OnCaretMoved;
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>
    /// Selects the next range out, or with <paramref name="expand"/> false the next one in, as VS Code's
    /// <c>smartSelect.expand</c> and <c>smartSelect.shrink</c> do. The first time, it waits for the server's ranges.
    /// Failures go to the log.
    /// </summary>
    public async void Run(bool expand)
    {
        try
        {
            if (_state is null && !await StartAsync())
                return;
            Select(expand);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested) { }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Smart select failed: {Exception}", e);
        }
    }

    /// <summary>
    /// Finds the ranges around the selection, as expanding starts; only while the text and the selection stay as they
    /// were meanwhile, though an expansion meanwhile still counts.
    /// </summary>
    /// <returns>Whether there are ranges to go through.</returns>
    private async Task<bool> StartAsync()
    {
        TextSnapshot snapshot = _document.CurrentSnapshot;
        Selection selection = _textArea.Selection;
        int caret = _textArea.Caret.Offset;
        List<TextRange>? ranges = await RangesAsync(snapshot, caret, _disposed.Token);
        if (_state is not null)
            return true;
        if (
            ranges is not { Count: > 0 }
            || _disposed.IsCancellationRequested
            || _document.CurrentSnapshot != snapshot
            || !_textArea.Selection.Equals(selection)
            || _textArea.Caret.Offset != caret
        )
            return false;
        (int start, int end) = SelectionBounds();
        List<TextRange> around = [TextRange.FromBounds(start, end)];
        around.AddRange(ranges.Where(range => range.Start <= start && end <= range.End));
        _state = (around, 0);
        return true;
    }

    /// <summary>Selects the next range out or in.</summary>
    private void Select(bool expand)
    {
        if (_state is not var (all, index))
            return;
        index = Move(all, index, expand);
        _state = (all, index);
        _selecting = true;
        try
        {
            _textArea.Caret.Offset = all[index].End;
            if (all[index].Length == 0)
                _textArea.ClearSelection();
            else
                _textArea.Selection = Selection.Create(_textArea, all[index].Start, all[index].End);
            _textArea.Caret.BringCaretToView();
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>
    /// The ranges around <paramref name="caret"/>: the server's, when it offers them, or else the bracket ranges, with
    /// the word ranges either way, combined as VS Code combines them. Null when the text changed while the server
    /// answered: its ranges, and the caret, are then of text that is gone.
    /// </summary>
    private async Task<List<TextRange>?> RangesAsync(TextSnapshot snapshot, int caret, CancellationToken cancellation)
    {
        SmartSelectOptions options = _editor.SmartSelect;
        var ranges = new List<TextRange>();
        if (_document.Features is { SelectionRange: true })
        {
            // A server that fails gives no ranges, and the bracket ranges stay out, as in VS Code.
            IReadOnlyList<TextRange>? server = await _document.SelectionRangesAsync(snapshot, caret, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (_document.CurrentSnapshot != snapshot)
                return null;
            if (server is not null)
                ranges.AddRange(server);
        }
        else
        {
            ranges.AddRange(
                SelectionRangeProviders.Brackets(_textArea.Document, caret, Brackets, _highlighter.TokensOf)
            );
        }
        TextDocument buffer = _textArea.Document;
        ranges.AddRange(SelectionRangeProviders.Words(buffer, caret, options.SelectSubwords));
        return SelectionRangeProviders.Combine(buffer, ranges, caret, options.SelectLeadingAndTrailingWhitespace);
    }

    /// <summary>
    /// The index of the next range out or in from <paramref name="index"/>, past ranges equal to the current one; the
    /// same at either end.
    /// </summary>
    private static int Move(IReadOnlyList<TextRange> ranges, int index, bool expand)
    {
        int next = index + (expand ? 1 : -1);
        if (next < 0 || next >= ranges.Count)
            return index;
        return ranges[next] == ranges[index] ? Move(ranges, next, expand) : next;
    }

    private (int Start, int End) SelectionBounds()
    {
        int caret = _textArea.Caret.Offset;
        Selection selection = _textArea.Selection;
        if (selection.IsEmpty)
            return (caret, caret);
        ISegment segment = selection.SurroundingSegment;
        return (segment.Offset, segment.EndOffset);
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (!_selecting)
            _state = null;
    }

    /// <summary>The language's brackets; none while they load.</summary>
    private IReadOnlyList<CharacterPair> Brackets =>
        _document.Language.Loaded is { IsCompletedSuccessfully: true } loaded
            ? loaded.Result.Configuration.Brackets
            : [];
}
