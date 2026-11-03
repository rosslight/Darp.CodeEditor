using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Theming;

namespace Darp.CodeEditor.Markers;

/// <summary>
/// Colors semantic classifications over the TextMate colors, with the theme's colors for the matching TextMate scopes.
/// Rendering reads only the cached segments; they move with the text until the next result replaces them.
/// </summary>
internal sealed class SemanticColorizer : DocumentColorizingTransformer, IThemedRenderer
{
    private SegmentCache<ClassifiedSegment> _cache = SegmentCache<ClassifiedSegment>.Empty;

    public EditorTheme EditorTheme { private get; set; } = EditorTheme.Default;

    public void Update(TextDocument document, IReadOnlyList<ClassifiedRange> ranges)
    {
        _cache.Dispose();
        _cache = SegmentCache<ClassifiedSegment>.Create(
            document,
            ranges.Select(range => (range.Range, new ClassifiedSegment(range.Kind)))
        );
    }

    public void Clear()
    {
        _cache.Dispose();
        _cache = SegmentCache<ClassifiedSegment>.Empty;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (ClassifiedSegment segment in _cache.FindOverlapping(line.Offset, line.Length))
        {
            int start = Math.Max(segment.StartOffset, line.Offset);
            int end = Math.Min(segment.EndOffset, line.EndOffset);
            if (start < end && EditorTheme.GetSemanticBrush(segment.Kind) is { } brush)
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private sealed class ClassifiedSegment(ClassificationKind kind) : TextSegment
    {
        public ClassificationKind Kind { get; } = kind;
    }
}
