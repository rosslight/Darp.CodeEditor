using Avalonia;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Editing;

/// <summary>Where a text view draws text.</summary>
internal static class TextViewBoxes
{
    /// <summary>
    /// The box of the character at <paramref name="offset"/>, as the view shows it now: from the top to the bottom of
    /// its line, a space wide. The view must have its lines.
    /// </summary>
    public static Rect CharacterAt(TextView textView, int offset)
    {
        var position = new TextViewPosition(textView.Document.GetLocation(offset));
        Point top = textView.GetVisualPosition(position, VisualYPosition.LineTop) - textView.ScrollOffset;
        Point bottom = textView.GetVisualPosition(position, VisualYPosition.LineBottom) - textView.ScrollOffset;
        return new Rect(top.X, top.Y, textView.WideSpaceWidth, bottom.Y - top.Y);
    }
}
