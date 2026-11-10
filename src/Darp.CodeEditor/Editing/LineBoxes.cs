using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Where a line in view draws its characters, in the view's coordinates. A row of the line is measured in one pass over
/// its glyphs the first time one of its characters is asked for, so that many boxes in a row cost what one does: a text
/// line's own hit tests walk the row from its start each time. Reused from line to line, it keeps its arrays.
/// </summary>
internal sealed class LineBoxes
{
    /// <summary>The visual column where each row starts, and after the last one, where the line ends.</summary>
    private readonly List<int> _rowStarts = [];

    /// <summary>The top of each row, and after the last one, where the line ends, in the coordinates of the whole text.</summary>
    private readonly List<double> _rowTops = [];

    /// <summary>How close boxes are that touch: a part of a pixel, as the layout adds up the widths of glyphs.</summary>
    private const double Touch = 0.5;

    private readonly List<bool> _measured = [];

    /// <summary>The stretches of the screen that a span covers on a row, kept for the next.</summary>
    private readonly List<(double Left, double Right)> _stretches = [];
    private double[] _left = [];
    private double[] _right = [];
    private TextView? _textView;
    private VisualLine? _line;
    private int _element;

    /// <summary>Starts on <paramref name="line"/>, which the view shows.</summary>
    public void Show(TextView textView, VisualLine line)
    {
        _textView = textView;
        _line = line;
        _rowStarts.Clear();
        _rowTops.Clear();
        _measured.Clear();
        int column = 0;
        double top = line.VisualTop;
        foreach (TextLine row in line.TextLines)
        {
            _rowStarts.Add(column);
            _rowTops.Add(top);
            _measured.Add(false);
            column += row.Length;
            // As the line places its rows.
            top += Math.Max(row.Height, textView.DefaultLineHeight);
        }
        _rowStarts.Add(column);
        _rowTops.Add(top);
        if (_left.Length < column + 1)
            (_left, _right) = (new double[column + 1], new double[column + 1]);
        Rewind();
    }

    /// <summary>Lets <see cref="ColumnAt"/> start again from the line's start.</summary>
    public void Rewind() => _element = 0;

    /// <summary>
    /// The visual column of the text at <paramref name="relativeOffset"/> from the line's start, as
    /// <see cref="VisualLine.GetVisualColumn(int)"/> finds it; since <see cref="Rewind"/>, the offsets may not decrease.
    /// </summary>
    public int ColumnAt(int relativeOffset)
    {
        VisualLine line = Line;
        IReadOnlyList<VisualLineElement> elements = line.Elements;
        while (
            _element < elements.Count - 1
            && elements[_element].RelativeTextOffset + elements[_element].DocumentLength < relativeOffset
        )
        {
            _element++;
        }
        if (elements.Count == 0)
            return line.VisualLength;
        VisualLineElement element = elements[_element];
        bool inside =
            element.RelativeTextOffset <= relativeOffset
            && relativeOffset <= element.RelativeTextOffset + element.DocumentLength;
        return inside ? element.GetVisualColumn(relativeOffset) : line.VisualLength;
    }

    /// <summary>
    /// The box of the character at <paramref name="column"/>: as wide as it is drawn, as high as its row. Past the line's
    /// end, it is the line's end.
    /// </summary>
    public Rect CharacterAt(int column)
    {
        column = Math.Min(column, _rowStarts[^1] - 1);
        int row = RowOf(column);
        Measure(row);
        return Box(row, _left[column], _right[column]);
    }

    /// <summary>
    /// Boxes over the characters from <paramref name="start"/> to <paramref name="end"/>: on each row, one for each stretch
    /// of the screen that they cover, as text of the other direction within them puts them apart.
    /// </summary>
    public void AddSpans(int start, int end, List<Rect> boxes)
    {
        for (int row = RowOf(start); row < _rowStarts.Count - 1 && _rowStarts[row] < end; row++)
        {
            Measure(row);
            int from = Math.Max(start, _rowStarts[row]);
            int to = Math.Min(end, _rowStarts[row + 1]);
            if (from >= to)
                continue;
            // In the order of the text, a character next to the one before, on either side, as text runs on in either
            // direction, adds to its stretch.
            _stretches.Clear();
            for (int column = from; column < to; column++)
            {
                (double left, double right) = (_left[column], _right[column]);
                if (
                    _stretches is [.., var (lastLeft, lastRight)]
                    && left <= lastRight + Touch
                    && right >= lastLeft - Touch
                )
                    _stretches[^1] = (Math.Min(lastLeft, left), Math.Max(lastRight, right));
                else
                    _stretches.Add((left, right));
            }
            // Stretches apart in the text may meet on the screen.
            _stretches.Sort();
            (double Left, double Right) stretch = _stretches[0];
            for (int i = 1; i < _stretches.Count; i++)
            {
                if (_stretches[i].Left <= stretch.Right + Touch)
                    stretch.Right = Math.Max(stretch.Right, _stretches[i].Right);
                else
                {
                    boxes.Add(Box(row, stretch.Left, stretch.Right));
                    stretch = _stretches[i];
                }
            }
            boxes.Add(Box(row, stretch.Left, stretch.Right));
        }
    }

    private VisualLine Line => _line ?? throw new InvalidOperationException("No line is shown.");

    private Rect Box(int row, double left, double right)
    {
        TextView textView = _textView ?? throw new InvalidOperationException("No line is shown.");
        Vector scroll = textView.ScrollOffset;
        return new Rect(left - scroll.X, _rowTops[row] - scroll.Y, right - left, _rowTops[row + 1] - _rowTops[row]);
    }

    /// <summary>The row that <paramref name="column"/> is on; a column past the line's end is on its last row.</summary>
    private int RowOf(int column)
    {
        int found = _rowStarts.BinarySearch(0, _rowStarts.Count - 1, column, null);
        return Math.Max(0, found >= 0 ? found : ~found - 1);
    }

    /// <summary>
    /// Finds where <paramref name="row"/> draws each character, from its text runs: the glyphs of a shaped run, each
    /// over its cluster's characters, and the whole run for others, such as a tab's. Characters that no run draws, as
    /// at the line's end, are where the character before them ends.
    /// </summary>
    private void Measure(int row)
    {
        if (_measured[row])
            return;
        _measured[row] = true;
        TextLine textLine = Line.TextLines[row];
        int start = _rowStarts[row];
        int end = _rowStarts[row + 1];
        _left.AsSpan(start, end - start).Fill(double.NaN);
        foreach (TextBounds bounds in textLine.GetTextBounds(start, end - start))
        {
            foreach (TextRunBounds run in bounds.TextRunBounds)
            {
                int first = Math.Max(start, run.TextSourceCharacterIndex);
                int last = Math.Min(end, run.TextSourceCharacterIndex + run.Length);
                if (
                    run.TextRun is ShapedTextRun { ShapedBuffer: { Length: > 0 } glyphs } shaped
                    && shaped.Length == run.Length
                )
                    MeasureGlyphs(glyphs, run.TextSourceCharacterIndex, run.Rectangle.Left, first, last);
                for (int column = first; column < last; column++)
                {
                    if (double.IsNaN(_left[column]))
                        (_left[column], _right[column]) = (run.Rectangle.Left, run.Rectangle.Right);
                }
            }
        }
        double previous = textLine.GetDistanceFromCharacterHit(new CharacterHit(start));
        for (int column = start; column < end; column++)
        {
            if (double.IsNaN(_left[column]))
                (_left[column], _right[column]) = (previous, previous);
            previous = _right[column];
        }
    }

    /// <summary>
    /// The glyphs of a shaped run, in the order they are drawn, from <paramref name="x"/>: each over the characters of
    /// its cluster, which count from the run's first.
    /// </summary>
    private void MeasureGlyphs(ShapedBuffer glyphs, int runStart, double x, int first, int last)
    {
        int firstCluster = int.MaxValue;
        for (int i = 0; i < glyphs.Length; i++)
            firstCluster = Math.Min(firstCluster, glyphs[i].GlyphCluster);
        for (int i = 0; i < glyphs.Length; i++)
        {
            GlyphInfo glyph = glyphs[i];
            int column = runStart + glyph.GlyphCluster - firstCluster;
            if (column >= first && column < last)
            {
                // A cluster of several glyphs, as a letter and its accent, spans them all.
                bool seen = !double.IsNaN(_left[column]);
                _left[column] = seen ? Math.Min(_left[column], x) : x;
                _right[column] = seen ? Math.Max(_right[column], x + glyph.GlyphAdvance) : x + glyph.GlyphAdvance;
            }
            x += glyph.GlyphAdvance;
        }
        // The other characters of a cluster, as a surrogate pair's second half, are where its first is.
        for (int column = first + 1; column < last; column++)
        {
            if (double.IsNaN(_left[column]))
                (_left[column], _right[column]) = (_left[column - 1], _right[column - 1]);
        }
    }
}
