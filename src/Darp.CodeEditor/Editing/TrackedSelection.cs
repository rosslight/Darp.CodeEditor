namespace Darp.CodeEditor.Editing;

/// <summary>
/// Moves a selection through the edits of one command, as VS Code tracks the selection of a command's
/// <c>trackSelection</c>: a selection never grows by text that goes in at its ends, and a caret stays before text that
/// goes in where it is only at its line's end. A port of VS Code's <c>nodeAcceptEdit</c>.
/// </summary>
internal static class TrackedSelection
{
    /// <summary>
    /// Where the selection from <paramref name="anchor"/> to <paramref name="caret"/> goes through
    /// <paramref name="edits"/>, which are apart, in order, and in offsets of the text before all of them.
    /// </summary>
    /// <param name="anchor">Where the selection starts.</param>
    /// <param name="caret">Where it ends, the caret.</param>
    /// <param name="atLineEnd">Whether the caret of an empty selection is at its line's end.</param>
    /// <param name="edits">The edits: what each replaces, and its new text.</param>
    public static (int Anchor, int Caret) Map(
        int anchor,
        int caret,
        bool atLineEnd,
        IReadOnlyList<(int Offset, int Length, string Text)> edits
    )
    {
        int start = Math.Min(anchor, caret);
        int end = Math.Max(anchor, caret);
        // VS Code's stickiness: a selection never grows at its edges, and a caret grows only before at its line's end.
        bool empty = start == end;
        int newStart = MapOffset(start, sticksToPrevious: empty && atLineEnd, edits);
        int newEnd = Math.Max(newStart, MapOffset(end, sticksToPrevious: !empty || atLineEnd, edits));
        return anchor <= caret ? (newStart, newEnd) : (newEnd, newStart);
    }

    /// <summary>
    /// Where <paramref name="offset"/> goes through <paramref name="edits"/>: VS Code applies a command's edits from the
    /// last to the first, so each one's offsets hold when it applies.
    /// </summary>
    public static int MapOffset(
        int offset,
        bool sticksToPrevious,
        IReadOnlyList<(int Offset, int Length, string Text)> edits
    )
    {
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            (int start, int length, string text) = edits[i];
            offset = Accept(offset, sticksToPrevious, start, start + length, text.Length);
        }
        return offset;
    }

    /// <summary>VS Code's <c>nodeAcceptEdit</c> for one end of a range, without forced moves.</summary>
    private static int Accept(int marker, bool sticksToPrevious, int start, int end, int inserted)
    {
        int deleted = end - start;
        int common = Math.Min(deleted, inserted);
        if (StaysBefore(marker, sticksToPrevious, start, deleted > 0 ? Move.Stay : Move.ByStickiness))
            return marker;
        if (
            common > 0
            && StaysBefore(marker, sticksToPrevious, start + common, deleted > inserted ? Move.Stay : Move.ByStickiness)
        )
            return marker;
        if (StaysBefore(marker, sticksToPrevious, end, Move.ByStickiness))
            return start + inserted;
        return Math.Max(0, marker + inserted - deleted);
    }

    /// <summary>VS Code's <c>adjustMarkerBeforeColumn</c>: whether the marker stays before what changes at the offset.</summary>
    private static bool StaysBefore(int marker, bool sticksToPrevious, int offset, Move move) =>
        marker < offset || (marker == offset && (move == Move.Stay || sticksToPrevious));

    private enum Move
    {
        ByStickiness,
        Stay,
    }
}
