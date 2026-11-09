using System.ComponentModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
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

    /// <summary>Where the line being drawn draws its characters.</summary>
    private readonly LineBoxes _boxes = new();

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
    /// Each line in view is read only where the view shows it: in the shown part of a long line, and in the rows of a
    /// wrapped line between the view's top and bottom edges, so that what lies in a hidden tail or in rows above or
    /// below the view costs nothing. Diagnostics that overlap draw one squiggle, so their ranges are merged first, by severity: the
    /// waves and dots drawn grow with the text on screen, not with the diagnostics. Where they go is measured once per
    /// row of text, not once per squiggle: see <see cref="LineBoxes"/>.
    /// </remarks>
    public override void Render(DrawingContext context)
    {
        TextView textView = TextArea.TextView;
        TextSegmentCollection<DiagnosticMarker> markers = _document.ShownDiagnostics;
        if (_colors is not { } colors || markers.Count == 0 || !textView.VisualLinesValid)
            return;
        // The lines on screen, each with the stretch of its text that the view shows, in the order of the text.
        var lines = new List<(VisualLine Line, int Start, int End)>(textView.VisualLines.Count);
        foreach (VisualLine visualLine in textView.VisualLines)
        {
            if (OnScreen(textView, visualLine) is var (start, end))
                lines.Add((visualLine, start, end));
        }
        if (lines.Count == 0)
            return;
        // The frame's, by severity, in the order of the text: the ranges merged, and the empty ones and the hints by
        // their starts, each start once. Read once each, also those over many lines.
        var allRanges = new List<(int Start, int End)>[4];
        var allStarts = new List<int>[4];
        for (int severity = 0; severity < 4; severity++)
            (allRanges[severity], allStarts[severity]) = ([], []);
        int first = lines[0].Start;
        foreach (DiagnosticMarker marker in markers.FindOverlappingSegments(first, lines[^1].End - first))
        {
            int severity = (int)marker.Diagnostic.Severity;
            // Reading the offset walks the segments' tree: once each.
            int markerStart = marker.StartOffset;
            int markerEnd = markerStart + marker.Length;
            if (marker.Diagnostic.Severity == DiagnosticSeverity.Hint || marker.Length == 0)
            {
                if (allStarts[severity] is not [.., var last] || last != markerStart)
                    allStarts[severity].Add(markerStart);
            }
            else if (allRanges[severity] is [.., var (lastStart, lastEnd)] && markerStart <= lastEnd)
                allRanges[severity][^1] = (lastStart, Math.Max(lastEnd, markerEnd));
            else
                allRanges[severity].Add((markerStart, markerEnd));
        }
        // A line's, by severity: its part of them, and where in them the next line's part starts.
        var ranges = new List<(int Start, int End)>[4];
        var starts = new List<int>[4];
        var nextRange = new int[4];
        var nextStart = new int[4];
        // The frame's, by severity: the boxes to draw a wave under, and those of the hints' first characters.
        var waves = new List<Rect>[4];
        var dots = new List<Rect>();
        for (int severity = 0; severity < 4; severity++)
            (ranges[severity], starts[severity], waves[severity]) = ([], [], []);
        foreach ((VisualLine visualLine, int lineStart, int shownEnd) in lines)
        {
            DocumentLine line = visualLine.FirstDocumentLine;
            // Where a start is in a hidden tail: the "..." shows none of the text.
            int hidden = VisibleText.IsCollapsed(line) ? VisibleText.End(line) : -1;
            bool any = false;
            for (int severity = 0; severity < 4; severity++)
            {
                List<(int Start, int End)> merged = allRanges[severity];
                while (nextRange[severity] < merged.Count && merged[nextRange[severity]].End <= lineStart)
                    nextRange[severity]++;
                // A range over lines stays for the next line too.
                for (int k = nextRange[severity]; k < merged.Count && merged[k].Start < shownEnd; k++)
                {
                    (int from, int to) = (Math.Max(merged[k].Start, lineStart), Math.Min(merged[k].End, shownEnd));
                    if (to > from)
                        ranges[severity].Add((from, to));
                }
                List<int> points = allStarts[severity];
                while (nextStart[severity] < points.Count && points[nextStart[severity]] < lineStart)
                    nextStart[severity]++;
                for (
                    ;
                    nextStart[severity] < points.Count && points[nextStart[severity]] <= shownEnd;
                    nextStart[severity]++
                )
                {
                    if (points[nextStart[severity]] != hidden)
                        starts[severity].Add(points[nextStart[severity]]);
                }
                any |= ranges[severity].Count > 0 || starts[severity].Count > 0;
            }
            if (any)
                Measure(textView, visualLine, ranges, starts, waves, dots);
        }
        // Errors last, over the others.
        for (int severity = 0; severity < 4; severity++)
        {
            if (waves[severity].Count == 0 && (severity != (int)DiagnosticSeverity.Hint || dots.Count == 0))
                continue;
            var shape = new StreamGeometry();
            using (StreamGeometryContext figure = shape.Open())
            {
                // Where shapes of one severity overlap, as an empty range's inside another's, both show.
                figure.SetFillRule(FillRule.NonZero);
                if (severity == (int)DiagnosticSeverity.Hint)
                {
                    foreach (Rect character in dots)
                        AddDots(figure, character);
                }
                foreach (Rect box in waves[severity])
                    AddWave(figure, box);
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

    /// <summary>
    /// The boxes that a line's ranges and starts are drawn under, by severity; empties the line's lists for the next.
    /// </summary>
    private void Measure(
        TextView textView,
        VisualLine visualLine,
        List<(int Start, int End)>[] ranges,
        List<int>[] starts,
        List<Rect>[] waves,
        List<Rect> dots
    )
    {
        int lineOffset = visualLine.FirstDocumentLine.Offset;
        _boxes.Show(textView, visualLine);
        for (int severity = 0; severity < 4; severity++)
        {
            _boxes.Rewind();
            foreach (int point in starts[severity])
            {
                Rect character = _boxes.CharacterAt(_boxes.ColumnAt(point - lineOffset));
                if (severity == (int)DiagnosticSeverity.Hint)
                    dots.Add(character);
                else
                    waves[severity].Add(character.WithWidth(textView.WideSpaceWidth));
            }
            _boxes.Rewind();
            foreach ((int start, int end) in ranges[severity])
            {
                int startColumn = _boxes.ColumnAt(start - lineOffset);
                _boxes.AddSpans(startColumn, _boxes.ColumnAt(end - lineOffset), waves[severity]);
            }
            starts[severity].Clear();
            ranges[severity].Clear();
        }
    }

    /// <summary>
    /// The offsets of <paramref name="visualLine"/> that the view shows: those of its rows between the view's top and
    /// bottom edges, within the shown part of the line; null when none is. Whole rows: right-to-left text puts a row's
    /// offsets in another order across the screen than along the text, while a row holds a stretch of the text.
    /// </summary>
    private static (int Start, int End)? OnScreen(TextView textView, VisualLine visualLine)
    {
        // In the view's coordinates of the whole text, as the line places its rows.
        double top = textView.ScrollOffset.Y;
        double bottom = top + textView.Bounds.Height;
        double rowTop = visualLine.VisualTop;
        int column = 0;
        (int First, int End)? columns = null;
        foreach (TextLine row in visualLine.TextLines)
        {
            double rowBottom = rowTop + Math.Max(row.Height, textView.DefaultLineHeight);
            if (rowBottom > top && rowTop < bottom)
                columns = (columns?.First ?? column, column + row.Length);
            column += row.Length;
            rowTop = rowBottom;
        }
        if (columns is not var (first, end))
            return null;
        DocumentLine line = visualLine.FirstDocumentLine;
        int start = line.Offset + visualLine.GetRelativeOffset(first);
        return (
            start,
            Math.Max(start, Math.Min(VisibleText.End(line), line.Offset + visualLine.GetRelativeOffset(end)))
        );
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

    /// <summary>Three dots under the first characters from <paramref name="character"/> on, as VS Code marks a hint.</summary>
    private static void AddDots(StreamGeometryContext figure, Rect character)
    {
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
