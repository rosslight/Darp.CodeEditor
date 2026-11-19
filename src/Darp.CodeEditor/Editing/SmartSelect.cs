using Avalonia.Logging;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Expands and shrinks the selection, as VS Code's smart select (<c>editor.action.smartSelect.expand</c> and
/// <c>.shrink</c>): through the ranges around the caret, found as expanding starts, until the selection, the caret or
/// the text changes otherwise. They are the server's <c>textDocument/selectionRange</c> where it offers them, or else the bracket
/// ranges, with the word ranges either way (<see cref="SmartSelectRanges"/>).
/// </summary>
internal sealed class SmartSelect : IEditorFeature
{
    private readonly TextArea _area;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly BackgroundTextMateHighlighter _highlighter;
    private readonly TextChanges _changes;

    /// <summary>Drops what the server answers once the view no longer shows the document.</summary>
    private readonly CancellationTokenSource _disposed = new();

    /// <summary>The ranges from the selection out, and the one selected; null until expanding starts.</summary>
    private (IReadOnlyList<TextRange> Ranges, int Index)? _ranges;

    /// <summary>
    /// Counts the selection's sessions: each change of the selection, the caret or the text but the feature's own
    /// starts another, and an answer for an earlier one neither starts nor moves along the ranges.
    /// </summary>
    private int _session;

    /// <summary>
    /// The commands since the session asked for its ranges, expanding or not, in the order of their keys; null while it
    /// asks for none. A session asks once: the commands go through the ranges in order once they come.
    /// </summary>
    private List<bool>? _waiting;

    /// <summary>Whether a range is selected, which changes the selection within the session.</summary>
    private bool _selecting;

    public SmartSelect(
        TextArea area,
        CodeDocument document,
        BackgroundTextMateHighlighter highlighter,
        TextChanges changes
    )
    {
        _area = area;
        _buffer = area.Document;
        _document = document;
        _highlighter = highlighter;
        _changes = changes;
        area.Caret.PositionChanged += OnSelectionChanged;
        area.SelectionChanged += OnSelectionChanged;
        _buffer.Changed += OnTextChanged;
    }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.SmartSelectExpand, () => Run(expand: true));
        commands.Add(CodeEditorCommands.SmartSelectShrink, () => Run(expand: false));
    }

    public void Dispose()
    {
        _area.Caret.PositionChanged -= OnSelectionChanged;
        _area.SelectionChanged -= OnSelectionChanged;
        _buffer.Changed -= OnTextChanged;
        _disposed.Cancel();
        _disposed.Dispose();
    }

    /// <summary>Selects the next range out or in, once the ranges are found.</summary>
    /// <returns>Whether it applies: not to a rectangle selection, whose keys grow it.</returns>
    private bool Run(bool expand)
    {
        if (_area.Selection is RectangleSelection)
            return false;
        if (_ranges is not null)
            Select(expand);
        else if (_waiting is not null)
            _waiting.Add(expand);
        else
        {
            _waiting = [expand];
            Start();
        }
        return true;
    }

    /// <summary>
    /// Finds the ranges around the selection, from the server where it offers them, and goes through them as the
    /// commands since say: only while the session lasts, as the text and the selection stay but for the feature's own
    /// changes. Failures go to the log.
    /// </summary>
    private async void Start()
    {
        CancellationToken cancellation = _disposed.Token;
        int session = _session;
        TextSelection selection = _changes.Selection;
        try
        {
            // A server that fails gives no ranges, and the bracket ranges stay out, as in VS Code.
            IReadOnlyList<TextRange>? server = _document.Features is { SelectionRange: true }
                ? await _document.SelectionRangesAsync(_document.CurrentSnapshot, selection.Caret, cancellation) ?? []
                : null;
            if (cancellation.IsCancellationRequested || session != _session || _waiting is not { } waiting)
                return;
            _waiting = null;
            _ranges = (Ranges(selection, server), 0);
            foreach (bool expand in waiting)
                Select(expand);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, AssetFiles.LogArea)?.Log(this, "Smart select failed: {Exception}", e);
        }
    }

    /// <summary>
    /// The ranges from <paramref name="selection"/> out: those of <paramref name="server"/>, or without them the bracket
    /// ranges, and the word ranges, that hold the selection.
    /// </summary>
    private List<TextRange> Ranges(TextSelection selection, IReadOnlyList<TextRange>? server)
    {
        int caret = selection.Caret;
        IEnumerable<TextRange> found =
            server
            ?? SmartSelectRanges.Brackets(
                _buffer,
                caret,
                LanguageConfiguration.Of(_document.Language).Brackets,
                _highlighter.TokensOf
            );
        TextRange selected = selection.Range;
        return
        [
            selected,
            .. SmartSelectRanges
                .Combine(_buffer, found.Concat(SmartSelectRanges.Words(_buffer, caret)), caret)
                .Where(range => range.Start <= selected.Start && selected.End <= range.End),
        ];
    }

    /// <summary>Selects the next range out, or in, past those equal to the one selected; at either end, none.</summary>
    private void Select(bool expand)
    {
        if (_ranges is not var (ranges, index))
            return;
        int next = index + (expand ? 1 : -1);
        while (next >= 0 && next < ranges.Count && ranges[next] == ranges[index])
            next += expand ? 1 : -1;
        if (next < 0 || next >= ranges.Count)
            return;
        _ranges = (ranges, next);
        _selecting = true;
        try
        {
            _changes.Select(new TextSelection(ranges[next].Start, ranges[next].End));
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <summary>A caret move or a selection of anyone's but the feature's starts another session.</summary>
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!_selecting)
            EndSession();
    }

    /// <summary>The ranges are of the text before the change.</summary>
    private void OnTextChanged(object? sender, DocumentChangeEventArgs e) => EndSession();

    private void EndSession()
    {
        _session++;
        _ranges = null;
        _waiting = null;
    }
}
