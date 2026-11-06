using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Search;

/// <summary>
/// Highlights the search matches in view in the color theme's find match colors, as VS Code does: the match that is
/// selected in the current color, the others in the highlight color. Above the selection, so that the current match
/// shows its own color.
/// </summary>
internal sealed class SearchMatchLayer(TextArea textArea) : SelectionOverlay(textArea)
{
    private TextAreaColors? _colors;
    private TextSegmentCollection<SearchMatch>? _matches;

    /// <summary>The colors of the color theme, once it is loaded.</summary>
    public TextAreaColors? Colors
    {
        set
        {
            _colors = value;
            InvalidateVisual();
        }
    }

    /// <summary>The matches to highlight; null for none.</summary>
    public TextSegmentCollection<SearchMatch>? Matches
    {
        set
        {
            _matches = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        TextView textView = TextArea.TextView;
        if (_matches is not { } matches || _colors is not { } colors || !textView.VisualLinesValid)
            return;
        if (textView.VisualLines is not [VisualLine first, ..] || textView.VisualLines[^1] is not { } last)
            return;
        int start = first.FirstDocumentLine.Offset;
        Selection selection = TextArea.Selection;
        foreach (SearchMatch match in matches.FindOverlappingSegments(start, last.LastDocumentLine.EndOffset - start))
        {
            bool current =
                !selection.IsEmpty
                && selection.SurroundingSegment is { } selected
                && selected.Offset == match.StartOffset
                && selected.Length == match.Length;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
            builder.AddSegment(textView, match);
            if (builder.CreateGeometry() is { } geometry)
            {
                context.DrawGeometry(
                    current ? colors.FindMatch : colors.FindMatchHighlight,
                    current ? colors.FindMatchBorder : colors.FindMatchHighlightBorder,
                    geometry
                );
            }
        }
    }
}
