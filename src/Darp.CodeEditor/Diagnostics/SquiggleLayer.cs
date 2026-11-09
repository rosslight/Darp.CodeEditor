using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Diagnostics;

/// <summary>
/// Draws the shown document's diagnostics as VS Code does: a squiggle under each error, warning and information in the
/// color theme's colors, and three dots under the start of a hint. It also finds the diagnostics under the pointer, for
/// the hover.
/// </summary>
/// <remarks>
/// <para>
/// A diagnostic's range is about its snapshot. The document maps it to the current text once, when the diagnostics
/// arrive, for all of its editors, and from then on it moves with the text as the user types, until the next
/// diagnostics replace it: see <see cref="CodeDocument.ShownDiagnostics"/>. An empty range is drawn one character wide.
/// </para>
/// <para>
/// Drawing reads only the diagnostics in view, so its cost grows with what the view shows, not with the document.
/// </para>
/// </remarks>
internal sealed class SquiggleLayer : SelectionOverlay
{
    /// <summary>A squiggle's wave: half of it, across and up, in pixels, as VS Code's six by three.</summary>
    private const double HalfWave = 3;

    private readonly CodeDocument _document;

    private TextAreaColors? _colors;

    public SquiggleLayer(TextArea textArea, CodeDocument document)
        : base(textArea)
    {
        _document = document;
        document.PropertyChanged += OnDocumentPropertyChanged;
    }

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            InvalidateVisual();
        }
    }

    /// <summary>
    /// The diagnostics whose range in the current text holds the character at <paramref name="offset"/>, or starts there
    /// when it is empty: at most <paramref name="count"/> of them, the most severe first, then in the server's order.
    /// </summary>
    /// <remarks>
    /// A server may put thousands on one character, and a hover shows a few: only the diagnostics that could be kept
    /// are looked at further, and only those kept are put in order. AvaloniaEdit's search for them grows with their
    /// number, but cheaply, 0.65 ms for 100,000; reading a diagnostic's offsets walks its tree, so it is left to the
    /// few that could be kept.
    /// </remarks>
    public IReadOnlyList<Diagnostic> At(int offset, int count)
    {
        if (count <= 0)
            return [];
        // By their rank negated: the worst kept is the first to give way.
        var kept = new PriorityQueue<DiagnosticMarker, int>(count + 1);
        foreach (DiagnosticMarker marker in _document.ShownDiagnostics.FindSegmentsContaining(offset))
        {
            if (kept.Count == count && kept.TryPeek(out _, out int worst) && -marker.Rank <= worst)
                continue;
            // The search finds the ranges that end at the offset too, which do not hold its character.
            if (marker.Length == 0 ? marker.StartOffset != offset : offset >= marker.EndOffset)
                continue;
            if (kept.Count < count)
                kept.Enqueue(marker, -marker.Rank);
            else
                kept.DequeueEnqueue(marker, -marker.Rank);
        }
        var diagnostics = new Diagnostic[kept.Count];
        for (int i = diagnostics.Length - 1; i >= 0; i--)
            diagnostics[i] = kept.Dequeue().Diagnostic;
        return diagnostics;
    }

    /// <remarks>
    /// Diagnostics that overlap draw one squiggle, so their ranges are merged first, by severity: the waves and dots
    /// drawn grow with the text in view, not with the diagnostics, of which a server may put thousands on one line.
    /// </remarks>
    public override void Render(DrawingContext context)
    {
        TextView textView = TextArea.TextView;
        TextSegmentCollection<DiagnosticMarker> markers = _document.ShownDiagnostics;
        if (_colors is not { } colors || markers.Count == 0 || !textView.VisualLinesValid)
            return;
        if (textView.VisualLines is not [VisualLine first, ..] || textView.VisualLines[^1] is not { } last)
            return;
        int start = first.FirstDocumentLine.Offset;
        // In the order of their starts: by severity, the ranges in view merged, and the empty ones and the hints by
        // their starts, each start once.
        var ranges = new List<(int Start, int End)>[4];
        var starts = new List<int>[4];
        for (int severity = 0; severity < 4; severity++)
            (ranges[severity], starts[severity]) = ([], []);
        foreach (
            DiagnosticMarker marker in markers.FindOverlappingSegments(start, last.LastDocumentLine.EndOffset - start)
        )
        {
            int severity = (int)marker.Diagnostic.Severity;
            // Reading the offset walks the segments' tree: once each.
            int markerStart = marker.StartOffset;
            int markerEnd = markerStart + marker.Length;
            if (marker.Diagnostic.Severity == DiagnosticSeverity.Hint || marker.Length == 0)
            {
                // An empty range is in view where it starts; dots under the start of a hint that starts above the view
                // would be out of it too.
                List<int> points = starts[severity];
                if (markerStart >= start && (points.Count == 0 || points[^1] != markerStart))
                    points.Add(markerStart);
            }
            else if (ranges[severity] is [.., var (lastStart, lastEnd)] && markerStart <= lastEnd)
                ranges[severity][^1] = (lastStart, Math.Max(lastEnd, markerEnd));
            else
                ranges[severity].Add((markerStart, markerEnd));
        }
        // Errors last, over the others.
        for (int severity = 0; severity < 4; severity++)
        {
            if (ranges[severity].Count == 0 && starts[severity].Count == 0)
                continue;
            var shape = new StreamGeometry();
            using (StreamGeometryContext figure = shape.Open())
            {
                // Where shapes of one severity overlap, as an empty range's inside another's, both show.
                figure.SetFillRule(FillRule.NonZero);
                foreach (int point in starts[severity])
                {
                    if (severity == (int)DiagnosticSeverity.Hint)
                        AddDots(figure, textView, point);
                    else
                        AddWave(figure, TextViewBoxes.CharacterAt(textView, point));
                }
                foreach ((int rangeStart, int rangeEnd) in ranges[severity])
                {
                    var segment = new SimpleSegment(rangeStart, rangeEnd - rangeStart);
                    foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        AddWave(figure, rect);
                }
            }
            IBrush brush = (DiagnosticSeverity)severity switch
            {
                DiagnosticSeverity.Error => colors.Error,
                DiagnosticSeverity.Warning => colors.Warning,
                DiagnosticSeverity.Information => colors.Information,
                _ => colors.Hint,
            };
            context.DrawGeometry(brush, null, shape);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _document.PropertyChanged -= OnDocumentPropertyChanged;
    }

    /// <summary>The diagnostics changed: raised on the UI thread.</summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.Diagnostics))
            InvalidateVisual();
    }

    /// <summary>
    /// A wave along the bottom of <paramref name="rect"/>, as a filled band a pixel and a half thick, as VS Code draws
    /// it, so that it shows solid on any background.
    /// </summary>
    private static void AddWave(StreamGeometryContext figure, Rect rect)
    {
        const double thickness = 1.5;
        int steps = (int)Math.Ceiling(rect.Width / HalfWave);
        figure.BeginFigure(Upper(0), isFilled: true);
        for (int i = 1; i <= steps; i++)
            figure.LineTo(Upper(i));
        for (int i = steps; i >= 0; i--)
            figure.LineTo(Upper(i) + new Point(0, thickness));
        figure.EndFigure(isClosed: true);

        // The upper edge of the band: from the top of the wave down to its bottom and back, every half wave.
        Point Upper(int step) =>
            new(
                Math.Min(rect.Left + step * HalfWave, rect.Right),
                step % 2 == 0 ? rect.Bottom - HalfWave : rect.Bottom - thickness
            );
    }

    /// <summary>Three dots under the first characters at <paramref name="offset"/>, as VS Code marks a hint.</summary>
    private static void AddDots(StreamGeometryContext figure, TextView textView, int offset)
    {
        Rect character = TextViewBoxes.CharacterAt(textView, offset);
        for (int i = 0; i < 3; i++)
        {
            double x = character.Left + 1 + 4 * i;
            double y = character.Bottom - 2;
            figure.BeginFigure(new Point(x - 1, y), isFilled: true);
            figure.ArcTo(new Point(x + 1, y), new Size(1, 1), 0, false, SweepDirection.Clockwise);
            figure.ArcTo(new Point(x - 1, y), new Size(1, 1), 0, false, SweepDirection.Clockwise);
            figure.EndFigure(isClosed: true);
        }
    }
}
