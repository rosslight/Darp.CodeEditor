using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Markers;

/// <summary>
/// Segments from one language-service result. The segments follow edits of the document, so results stay in place
/// while the user types until the next result replaces them.
/// </summary>
internal sealed class SegmentCache<T> : IDisposable
    where T : TextSegment
{
    private readonly TextDocument? _document;
    private readonly TextSegmentCollection<T> _segments;

    private SegmentCache(TextDocument? document, TextSegmentCollection<T> segments)
    {
        _document = document;
        _segments = segments;
    }

    public static SegmentCache<T> Empty { get; } = new(null, []);

    public int Count => _segments.Count;

    public static SegmentCache<T> Create(TextDocument document, IEnumerable<(TextRange Range, T Segment)> items)
    {
        var segments = new TextSegmentCollection<T>(document);
        foreach ((TextRange range, T segment) in items)
        {
            // Results come from outside the editor: skip ranges that do not fit the text.
            if (range.Start < 0 || range.Length < 0 || range.End > document.TextLength)
                continue;
            segment.StartOffset = range.Start;
            segment.Length = range.Length;
            segments.Add(segment);
        }
        return new SegmentCache<T>(document, segments);
    }

    public IReadOnlyList<T> FindOverlapping(int offset, int length) =>
        _segments.FindOverlappingSegments(offset, length);

    public IReadOnlyList<T> FindContaining(int offset) => _segments.FindSegmentsContaining(offset);

    public void Dispose()
    {
        if (_document is not null)
            _segments.Disconnect(_document);
    }
}
