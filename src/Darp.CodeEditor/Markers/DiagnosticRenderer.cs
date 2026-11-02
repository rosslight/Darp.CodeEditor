using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Markers;

/// <summary>Draws squiggles under diagnostics. Rendering reads only the cached segments.</summary>
internal sealed class DiagnosticRenderer : IBackgroundRenderer
{
    private static readonly IPen s_errorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#F14C4C")));
    private static readonly IPen s_warningPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#CCA700")));
    private static readonly IPen s_infoPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3794FF")));

    private SegmentCache<DiagnosticSegment> _cache = SegmentCache<DiagnosticSegment>.Empty;

    public KnownLayer Layer => KnownLayer.Selection;

    public void Update(TextDocument document, IReadOnlyList<DiagnosticInfo> diagnostics)
    {
        var segments = new List<(TextRange, DiagnosticSegment)>(diagnostics.Count);
        foreach (DiagnosticInfo diagnostic in diagnostics)
        {
            TextRange range = diagnostic.Range;
            // Give empty ranges, such as a missing ';', one character to draw under.
            if (range.Length == 0 && range.Start > 0)
                range = new TextRange(range.Start - 1, 1);
            segments.Add((range, new DiagnosticSegment(diagnostic)));
        }
        _cache.Dispose();
        _cache = SegmentCache<DiagnosticSegment>.Create(document, segments);
    }

    public void Clear()
    {
        _cache.Dispose();
        _cache = SegmentCache<DiagnosticSegment>.Empty;
    }

    public IEnumerable<DiagnosticInfo> At(int offset) => _cache.FindContaining(offset).Select(s => s.Diagnostic);

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_cache.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;
        int start = textView.VisualLines[0].FirstDocumentLine.Offset;
        int end = textView.VisualLines[^1].LastDocumentLine.EndOffset;
        foreach (DiagnosticSegment segment in _cache.FindOverlapping(start, end - start))
        {
            IPen pen = segment.Diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => s_errorPen,
                DiagnosticSeverity.Warning => s_warningPen,
                _ => s_infoPen,
            };
            foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                drawingContext.DrawGeometry(null, pen, CreateSquiggle(rect));
        }
    }

    private static StreamGeometry CreateSquiggle(Rect rect)
    {
        const double step = 2;
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        double y = rect.Bottom - 1;
        context.BeginFigure(new Point(rect.Left, y), false);
        bool up = true;
        for (double x = rect.Left + step; x <= rect.Right + step; x += step, up = !up)
            context.LineTo(new Point(x, up ? y - step : y));
        context.EndFigure(false);
        return geometry;
    }

    private sealed class DiagnosticSegment(DiagnosticInfo diagnostic) : TextSegment
    {
        public DiagnosticInfo Diagnostic { get; } = diagnostic;
    }
}
