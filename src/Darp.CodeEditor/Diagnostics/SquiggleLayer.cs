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
/// A diagnostic's range is about its snapshot. The layer maps it to the current text once, when the diagnostics arrive,
/// and from then on it moves with the text as the user types, until the next diagnostics replace it. Text typed at
/// either edge stays outside it, as <see cref="CodeDocument.MapToCurrent"/> keeps it. An empty range is drawn one
/// character wide.
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
    private readonly TextDocument _buffer;

    /// <summary>The diagnostics in the current text: they follow every change of it.</summary>
    private readonly TextSegmentCollection<Marker> _markers;

    private TextAreaColors? _colors;

    public SquiggleLayer(TextArea textArea, CodeDocument document)
        : base(textArea)
    {
        _document = document;
        _buffer = textArea.Document;
        _markers = new TextSegmentCollection<Marker>(_buffer);
        document.PropertyChanged += OnDocumentPropertyChanged;
        ReadDiagnostics();
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
    /// when it is empty, in the server's order.
    /// </summary>
    public IReadOnlyList<Diagnostic> At(int offset) =>
        [
            .. _markers
                .FindSegmentsContaining(offset)
                .Where(marker => marker.Length == 0 ? marker.StartOffset == offset : offset < marker.EndOffset)
                .OrderBy(marker => marker.Index)
                .Select(marker => marker.Diagnostic),
        ];

    public override void Render(DrawingContext context)
    {
        TextView textView = TextArea.TextView;
        if (_colors is not { } colors || _markers.Count == 0 || !textView.VisualLinesValid)
            return;
        if (textView.VisualLines is not [VisualLine first, ..] || textView.VisualLines[^1] is not { } last)
            return;
        int start = first.FirstDocumentLine.Offset;
        IReadOnlyList<Marker> markers = _markers.FindOverlappingSegments(
            start,
            last.LastDocumentLine.EndOffset - start
        );
        // One shape per severity, with errors drawn last, over the others.
        var shapes = new StreamGeometry?[4];
        var figures = new StreamGeometryContext?[4];
        foreach (Marker marker in markers)
        {
            int severity = (int)marker.Diagnostic.Severity;
            if (figures[severity] is not { } figure)
            {
                var shape = new StreamGeometry();
                shapes[severity] = shape;
                figure = figures[severity] = shape.Open();
            }
            if (marker.Diagnostic.Severity == DiagnosticSeverity.Hint)
            {
                // Dots under the start of a hint that starts above the view would be out of it too.
                if (marker.StartOffset >= start)
                    AddDots(figure, textView, marker.StartOffset);
            }
            else if (marker.Length == 0)
                AddWave(figure, TextViewBoxes.CharacterAt(textView, marker.StartOffset));
            else
            {
                foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, marker))
                    AddWave(figure, rect);
            }
        }
        for (int severity = 0; severity < shapes.Length; severity++)
        {
            figures[severity]?.Dispose();
            if (shapes[severity] is not { } shape)
                continue;
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
        _markers.Disconnect(_buffer);
    }

    /// <summary>The diagnostics changed: raised on the UI thread.</summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.Diagnostics))
            ReadDiagnostics();
    }

    /// <summary>Replaces the markers with the document's diagnostics, mapped to the current text.</summary>
    private void ReadDiagnostics()
    {
        _markers.Clear();
        IReadOnlyList<Diagnostic> diagnostics = _document.Diagnostics;
        for (int i = 0; i < diagnostics.Count; i++)
        {
            Diagnostic diagnostic = diagnostics[i];
            TextRange range = _document.MapToCurrent(diagnostic.Snapshot, diagnostic.Range);
            _markers.Add(new Marker(diagnostic, i) { StartOffset = range.Start, Length = range.Length });
        }
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

    /// <summary>A diagnostic in the current text.</summary>
    private sealed class Marker(Diagnostic diagnostic, int index) : TextSegment
    {
        public Diagnostic Diagnostic { get; } = diagnostic;

        /// <summary>Its place in the server's list.</summary>
        public int Index { get; } = index;
    }
}
