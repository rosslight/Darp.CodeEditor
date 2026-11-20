using System.ComponentModel;
using System.Globalization;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Localization;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Diagnostics;

/// <summary>
/// Goes through the shown document's diagnostics one at a time, as VS Code's F8 and Shift+F8 do in a file: its errors,
/// warnings and information, not its hints, the most severe first, then by position, with wraparound. Each shows in the
/// hover at the caret with which problem it is of how many, as VS Code's widget for it shows it.
/// </summary>
/// <remarks>
/// This is a port of VS Code's <c>MarkerList</c> and <c>MarkerController</c> (<c>editor.action.marker.next</c>,
/// <c>gotoError</c>, MIT license) for one file. The first move starts from the caret (<see cref="First"/>); later moves
/// take the next or previous one in the order, until the popup closes, the caret leaves the diagnostic moved to, or the
/// diagnostics change. The caret goes to where the diagnostic starts, or stays where it is inside it.
/// </remarks>
internal sealed class DiagnosticNavigation : IEditorFeature
{
    private readonly TextArea _area;
    private readonly TextDocument _buffer;
    private readonly CodeDocument _document;
    private readonly HoverController _hover;
    private readonly Action<TextRange> _reveal;
    private readonly TextChanges _changes;

    /// <summary>The diagnostics in the order of the moves; null to sort them again.</summary>
    private DiagnosticMarker[]? _markers;

    /// <summary>The index in <see cref="_markers"/> of the diagnostic moved to last; -1 to start from the caret.</summary>
    private int _index = -1;

    /// <summary>Whether a move moves the caret, which keeps where the moves are.</summary>
    private bool _moving;

    /// <summary>Whether the view is disposed: a move under way stops at its next step.</summary>
    private bool _disposed;

    /// <param name="area">The text area.</param>
    /// <param name="document">The document shown.</param>
    /// <param name="hover">The hover, which shows the problem moved to.</param>
    /// <param name="reveal">Brings a range into view, centered unless visible.</param>
    /// <param name="changes">Tells whether the text area still shows the view's document.</param>
    public DiagnosticNavigation(
        TextArea area,
        CodeDocument document,
        HoverController hover,
        Action<TextRange> reveal,
        TextChanges changes
    )
    {
        _changes = changes;
        _area = area;
        _buffer = area.Document;
        _document = document;
        _hover = hover;
        _reveal = reveal;
        _document.PropertyChanged += OnDocumentPropertyChanged;
        _area.Caret.PositionChanged += OnCaretPositionChanged;
    }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.NextMarker, () => Move(next: true));
        commands.Add(CodeEditorCommands.PreviousMarker, () => Move(next: false));
        // An editor knows its own document only.
        commands.Add(CodeEditorCommands.NextMarkerInFiles, () => Move(next: true));
        commands.Add(CodeEditorCommands.PreviousMarkerInFiles, () => Move(next: false));
    }

    public void Dispose()
    {
        _disposed = true;
        _document.PropertyChanged -= OnDocumentPropertyChanged;
        _area.Caret.PositionChanged -= OnCaretPositionChanged;
    }

    /// <returns>Whether there was a diagnostic to move to.</returns>
    private bool Move(bool next)
    {
        // The moves end with the problem's popup, as by Escape, as VS Code's session ends with its widget.
        if (_hover.ProblemOffset is null)
            _index = -1;
        DiagnosticMarker[] markers = _markers ??= Sorted();
        int count = markers.Length;
        if (count == 0)
            return false;
        int caret = _area.Caret.Offset;
        _index = _index == -1 ? First(markers, caret, next) : (_index + (next ? 1 : count - 1)) % count;
        DiagnosticMarker marker = markers[_index];
        var range = new TextRange(marker.StartOffset, marker.Length);
        int to = Holds(range, caret) ? caret : range.Start;
        TextSnapshot snapshot = _document.CurrentSnapshot;
        bool current;
        _moving = true;
        try
        {
            current = Step(_area.ClearSelection) && Step(() => _area.Caret.Offset = to);
        }
        finally
        {
            _moving = false;
        }
        // Where the popup goes, as VS Code reveals it: not the start of a diagnostic taller than the view.
        if (current && Step(() => _reveal(new TextRange(to, 0))))
            _hover.ShowProblem(to, Problem(marker.Diagnostic, _index + 1, count));
        return true;

        // The app's handlers of each step may show another document or change the text, which the rest is not for.
        bool Step(Action step) => !_disposed && _changes.WhileShown(step) && _document.CurrentSnapshot == snapshot;
    }

    /// <summary>The errors, warnings and information shown, the most severe first, then in the order of the text.</summary>
    private DiagnosticMarker[] Sorted() =>
        [
            .. _document
                .ShownDiagnostics.Where(marker => marker.Diagnostic.Severity != DiagnosticSeverity.Hint)
                .Select(marker => (Marker: marker, marker.StartOffset, marker.EndOffset))
                .OrderByDescending(found => found.Marker.Diagnostic.Severity)
                .ThenBy(found => found.StartOffset)
                .ThenBy(found => found.EndOffset)
                .Select(found => found.Marker),
        ];

    /// <summary>A heading with the severity's icon, the document's name and which problem it is, then the diagnostic.</summary>
    private IReadOnlyCollection<HoverBlock> Problem(Diagnostic diagnostic, int index, int count)
    {
        string of = string.Format(
            CultureInfo.CurrentCulture,
            count > 1 ? Strings.ProblemOfProblems : Strings.ProblemOfProblem,
            index,
            count
        );
        string? name =
            _document.FilePath is { } path ? Path.GetFileName(path)
            : _document is ServerDocument { Uri: var uri } && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
                ? HoverBlocks.Name(parsed)
            : null;
        HoverBlock[] heading = [new HoverHeading(diagnostic.Severity, name, of), new HoverRule()];
        return new DiagnosticBlocks(
            1,
            heading.Concat(HoverBlocks.Of([diagnostic], _hover.OpenRelated, HoverIcon.None))
        );
    }

    /// <summary>
    /// VS Code's first move from the caret: forward, the first diagnostic in the order that holds the caret or ends after
    /// it, an empty one counting as the word it touches, else the first; backward, the one before it, unless it holds the
    /// caret, else the last.
    /// </summary>
    private int First(DiagnosticMarker[] markers, int caret, bool next)
    {
        for (int i = 0; i < markers.Length; i++)
        {
            var range = new TextRange(markers[i].StartOffset, markers[i].Length);
            if (range.Length == 0 && WordRules.WordAt(_buffer, range.Start) is { } word)
                range = word;
            if (caret > range.End)
                continue;
            if (next || Holds(range, caret))
                return i;
            return (i + markers.Length - 1) % markers.Length;
        }
        return next ? 0 : markers.Length - 1;
    }

    /// <summary>Whether <paramref name="range"/> holds <paramref name="offset"/>, either edge included, as VS Code's ranges do.</summary>
    private static bool Holds(TextRange range, int offset) => offset >= range.Start && offset <= range.End;

    /// <summary>
    /// New diagnostics: the next move starts from the caret, and the problem that shows becomes the first in the order
    /// that holds where the caret went, as VS Code's widget updates, or closes without one.
    /// </summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CodeDocument.Diagnostics))
            return;
        _markers = null;
        _index = -1;
        if (_hover.ProblemOffset is not { } offset)
            return;
        DiagnosticMarker[] markers = _markers = Sorted();
        for (int i = 0; i < markers.Length; i++)
        {
            if (Holds(new TextRange(markers[i].StartOffset, markers[i].Length), offset))
            {
                _hover.UpdateProblem(Problem(markers[i].Diagnostic, i + 1, markers.Length));
                return;
            }
        }
        _hover.Close();
    }

    /// <summary>The caret left the diagnostic moved to: the next move starts from the caret.</summary>
    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_moving || _index == -1 || _markers is not { } markers)
            return;
        DiagnosticMarker marker = markers[_index];
        if (!Holds(new TextRange(marker.StartOffset, marker.Length), _area.Caret.Offset))
            _index = -1;
    }
}
