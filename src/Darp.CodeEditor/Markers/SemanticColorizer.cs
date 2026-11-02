using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Markers;

/// <summary>
/// Colors semantic classifications over the TextMate colors. Rendering reads only the cached segments; they move with
/// the text until the next result replaces them.
/// </summary>
internal sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private SegmentCache<ClassifiedSegment> _cache = SegmentCache<ClassifiedSegment>.Empty;

    public void Update(TextDocument document, IReadOnlyList<ClassifiedRange> ranges)
    {
        var segments = new List<(TextRange, ClassifiedSegment)>(ranges.Count);
        foreach (ClassifiedRange range in ranges)
        {
            if (GetBrush(range.Kind) is { } brush)
                segments.Add((range.Range, new ClassifiedSegment(brush)));
        }
        _cache.Dispose();
        _cache = SegmentCache<ClassifiedSegment>.Create(document, segments);
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
            if (start < end)
            {
                IBrush brush = segment.Brush;
                ChangeLinePart(start, end, element => element.TextRunProperties.SetForegroundBrush(brush));
            }
        }
    }

    // Colors of the Dark+ theme for the matching semantic token types.
    private static IBrush? GetBrush(ClassificationKind kind) =>
        kind switch
        {
            ClassificationKind.Class or ClassificationKind.Delegate => s_type,
            ClassificationKind.Struct => s_struct,
            ClassificationKind.Interface or ClassificationKind.Enum or ClassificationKind.TypeParameter => s_interface,
            ClassificationKind.Method or ClassificationKind.ExtensionMethod => s_method,
            ClassificationKind.Property
            or ClassificationKind.Field
            or ClassificationKind.Event
            or ClassificationKind.Local
            or ClassificationKind.Parameter => s_variable,
            ClassificationKind.Constant or ClassificationKind.EnumMember => s_constant,
            ClassificationKind.Namespace or ClassificationKind.Label => null,
            _ => null,
        };

    private static readonly IBrush s_type = new ImmutableSolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush s_struct = new ImmutableSolidColorBrush(Color.Parse("#86C691"));
    private static readonly IBrush s_interface = new ImmutableSolidColorBrush(Color.Parse("#B8D7A3"));
    private static readonly IBrush s_method = new ImmutableSolidColorBrush(Color.Parse("#DCDCAA"));
    private static readonly IBrush s_variable = new ImmutableSolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush s_constant = new ImmutableSolidColorBrush(Color.Parse("#4FC1FF"));

    private sealed class ClassifiedSegment(IBrush brush) : TextSegment
    {
        public IBrush Brush { get; } = brush;
    }
}
