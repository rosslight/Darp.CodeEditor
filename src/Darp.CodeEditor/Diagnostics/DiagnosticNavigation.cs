using System.ComponentModel;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Diagnostics;

/// <summary>
/// Goes through the shown document's diagnostics one at a time, as VS Code's F8 and Shift+F8 do in a file: its errors,
/// warnings and information, not its hints, in a <see cref="DiagnosticOrder"/>, with wraparound.
/// </summary>
/// <remarks>
/// This is a port of VS Code's <c>MarkerList</c> and <c>MarkerController</c> (<c>gotoError</c>, 1.147.0, MIT license)
/// for one file. The first move starts from the caret: forward, to the first diagnostic in the order that holds the
/// caret or ends after it; backward, to the one before that, unless it holds the caret. An empty diagnostic counts as
/// the word it touches. Later moves take the next or previous one in the order, until the caret leaves the diagnostic
/// moved to, or the diagnostics change. The caret goes to where the diagnostic starts, or stays where it is inside it.
/// New diagnostics show in the problem's popup while it is open, as VS Code's widget updates: the first in the order
/// that holds where the caret went, which it is of how many now; without one, the popup closes.
/// </remarks>
internal sealed class DiagnosticNavigation : IDisposable
{
    private readonly TextArea _textArea;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;

    /// <summary>Shows the problem that a move went to.</summary>
    private readonly HoverController _hover;

    /// <summary>The diagnostics in the order of the last move.</summary>
    private DiagnosticMarker[] _markers = [];

    /// <summary>The order of <see cref="_markers"/>; null to sort them again.</summary>
    private DiagnosticOrder? _order;

    /// <summary>The index in <see cref="_markers"/> of the diagnostic moved to last; -1 to start from the caret.</summary>
    private int _index = -1;

    /// <summary>The order of the last move, which the problem that shows is in.</summary>
    private DiagnosticOrder _moved;

    public DiagnosticNavigation(TextArea textArea, CodeDocument document, HoverController hover)
    {
        _textArea = textArea;
        _buffer = textArea.Document;
        _document = document;
        _hover = hover;
        _document.PropertyChanged += OnDocumentPropertyChanged;
        _textArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    /// <summary>
    /// Moves to the next diagnostic, or the previous one, in <paramref name="order"/>: returns where the caret goes, the
    /// diagnostic, and which it is, from 1, of how many; null without diagnostics.
    /// </summary>
    public (int Caret, Diagnostic Diagnostic, int Index, int Count)? Move(bool next, DiagnosticOrder order)
    {
        _moved = order;
        if (_order != order)
        {
            _markers = Sorted(order);
            _order = order;
            _index = -1;
        }
        int count = _markers.Length;
        if (count == 0)
            return null;
        int caret = _textArea.Caret.Offset;
        _index = _index == -1 ? First(caret, next) : (_index + (next ? 1 : count - 1)) % count;
        DiagnosticMarker marker = _markers[_index];
        var range = new TextRange(marker.StartOffset, marker.Length);
        return (Holds(range, caret) ? caret : range.Start, marker.Diagnostic, _index + 1, count);
    }

    public void Dispose()
    {
        _document.PropertyChanged -= OnDocumentPropertyChanged;
        _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
    }

    /// <summary>
    /// The errors, warnings and information shown, in <paramref name="order"/>; equal ones keep their order in the text.
    /// </summary>
    private DiagnosticMarker[] Sorted(DiagnosticOrder order)
    {
        IEnumerable<DiagnosticMarker> markers = _document.ShownDiagnostics.Where(marker =>
            marker.Diagnostic.Severity != DiagnosticSeverity.Hint
        );
        return order == DiagnosticOrder.Position
            ?
            [
                .. markers
                    .OrderBy(marker => marker.StartOffset)
                    .ThenBy(marker => marker.EndOffset)
                    .ThenByDescending(marker => marker.Diagnostic.Severity),
            ]
            :
            [
                .. markers
                    .OrderByDescending(marker => marker.Diagnostic.Severity)
                    .ThenBy(marker => marker.StartOffset)
                    .ThenBy(marker => marker.EndOffset),
            ];
    }

    /// <summary>
    /// VS Code's first move from the caret: forward, the first diagnostic in the order that holds the caret or ends after
    /// it, else the first; backward, the one before it, unless it holds the caret, else the last.
    /// </summary>
    private int First(int caret, bool next)
    {
        for (int i = 0; i < _markers.Length; i++)
        {
            var range = new TextRange(_markers[i].StartOffset, _markers[i].Length);
            if (range.Length == 0 && WordTouching(range.Start) is { } word)
                range = word;
            if (caret > range.End)
                continue;
            if (next || Holds(range, caret))
                return i;
            return (i + _markers.Length - 1) % _markers.Length;
        }
        return next ? 0 : _markers.Length - 1;
    }

    /// <summary>
    /// The word that <paramref name="offset"/> is in or touches, by VS Code's word characters, reaching as far as a
    /// hovered word; null where none does.
    /// </summary>
    private TextRange? WordTouching(int offset)
    {
        DocumentLine line = _buffer.GetLineByOffset(offset);
        int first = Math.Max(line.Offset, offset - HoverController.MaxWordReach);
        int last = Math.Min(line.EndOffset, offset + HoverController.MaxWordReach);
        int start = offset;
        while (start > first && WordCharacters.IsWordCharacter(_buffer.GetCharAt(start - 1)))
            start--;
        int end = offset;
        while (end < last && WordCharacters.IsWordCharacter(_buffer.GetCharAt(end)))
            end++;
        return start == end ? null : TextRange.FromBounds(start, end);
    }

    /// <summary>Whether <paramref name="range"/> holds <paramref name="offset"/>, either edge included, as VS Code's ranges do.</summary>
    private static bool Holds(TextRange range, int offset) => offset >= range.Start && offset <= range.End;

    /// <summary>
    /// New diagnostics: the next move sorts them, and starts from the caret; the problem that shows shows as they have
    /// it, or closes.
    /// </summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CodeDocument.Diagnostics))
            return;
        _order = null;
        _index = -1;
        if (_hover.ProblemOffset is { } offset)
            ShowProblemAt(offset);
    }

    /// <summary>
    /// Shows the problem at <paramref name="offset"/> in the problem's popup, as VS Code's <c>MarkerList.find</c> finds
    /// it: the first in the order of the last move whose range holds the offset; closes the popup without one.
    /// </summary>
    private void ShowProblemAt(int offset)
    {
        _markers = Sorted(_moved);
        _order = _moved;
        for (int i = 0; i < _markers.Length; i++)
        {
            if (Holds(new TextRange(_markers[i].StartOffset, _markers[i].Length), offset))
            {
                _hover.UpdateProblem(_markers[i].Diagnostic, i + 1, _markers.Length);
                return;
            }
        }
        _hover.Close();
    }

    /// <summary>The caret left the diagnostic moved to: the next move starts from the caret.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_index == -1)
            return;
        DiagnosticMarker marker = _markers[_index];
        if (!Holds(new TextRange(marker.StartOffset, marker.Length), _textArea.Caret.Offset))
            _index = -1;
    }
}
