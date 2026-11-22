using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Diagnostics;

/// <summary>
/// F8 and Shift+F8: goes through the shown document's errors, warnings and information by position, wrapping around,
/// moves the caret to each and shows it in the hover. Hints are not visited.
/// </summary>
/// <remarks>
/// Where the caret is on the problem moved to last, the move takes the one after or before it, so that problems that
/// start at one place are all visited; from anywhere else, it starts from the caret.
/// </remarks>
internal sealed class DiagnosticNavigation : IEditorFeature
{
    private readonly CodeEditor _editor;
    private readonly TextArea _area;
    private readonly CodeDocument _document;
    private readonly HoverController _hover;

    /// <summary>The problem moved to last, and where the caret went for it.</summary>
    private (DiagnosticMarker Marker, int Caret)? _last;

    public DiagnosticNavigation(CodeEditor editor, TextArea area, CodeDocument document, HoverController hover)
    {
        _editor = editor;
        _area = area;
        _document = document;
        _hover = hover;
    }

    public void AddCommands(CommandTable commands)
    {
        commands.Add(CodeEditorCommands.NextMarker, () => Move(next: true), applies: HasProblem);
        commands.Add(CodeEditorCommands.PreviousMarker, () => Move(next: false), applies: HasProblem);
    }

    public void Dispose() { }

    private bool HasProblem() => Problems().Any();

    /// <summary>The problems in the order of the text, which is the order of the collection.</summary>
    private IEnumerable<DiagnosticMarker> Problems() =>
        _document.ShownDiagnostics.Where(marker => marker.Diagnostic.Severity != DiagnosticSeverity.Hint);

    private void Move(bool next)
    {
        DiagnosticMarker[] problems = [.. Problems()];
        int caret = _area.Caret.Offset;
        int index = _last is var (marker, at) && at == caret ? Array.IndexOf(problems, marker) : -1;
        index =
            index >= 0 ? (index + (next ? 1 : problems.Length - 1)) % problems.Length : First(problems, caret, next);
        DiagnosticMarker problem = problems[index];
        int to = problem.StartOffset;
        _area.ClearSelection();
        _area.Caret.Offset = to;
        _last = (problem, to);
        _editor.ScrollTo(new TextRange(to, 0));
        // The scroll lands in the layout, and a hover that shows before it would close with it.
        _editor.UpdateLayout();
        _hover.ShowProblem(to, problem.Diagnostic);
    }

    /// <summary>
    /// The first move from the caret: forward, the first problem that holds the caret or ends after it, else the first;
    /// backward, the one before it, unless it holds the caret, else the last.
    /// </summary>
    private static int First(DiagnosticMarker[] problems, int caret, bool next)
    {
        for (int i = 0; i < problems.Length; i++)
        {
            if (caret > problems[i].EndOffset)
                continue;
            return next || caret >= problems[i].StartOffset ? i : (i + problems.Length - 1) % problems.Length;
        }
        return next ? 0 : problems.Length - 1;
    }
}
