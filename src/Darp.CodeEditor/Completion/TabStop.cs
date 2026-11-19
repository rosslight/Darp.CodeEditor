using AvaloniaEdit.Document;

namespace Darp.CodeEditor.Completion;

/// <summary>A stop in the text: a placeholder, a mirror, or the final stop.</summary>
/// <param name="start">Where it starts.</param>
/// <param name="end">Where it ends; before <paramref name="start"/> once text went in at an empty stop that does not grow.</param>
/// <param name="parent">The placeholder it is inside; null for none.</param>
/// <param name="hadText">Whether it had text when it went in: once it is empty, its text was deleted.</param>
/// <param name="isFinal">Whether it is the session's final stop.</param>
internal sealed class TabStop(TextAnchor start, TextAnchor end, TabStop? parent, bool hadText, bool isFinal)
{
    private readonly TextAnchor _start = start;
    private readonly TextAnchor _end = end;
    private readonly bool _hadText = hadText;

    public TabStop? Parent { get; } = parent;

    /// <summary>How many placeholders it is inside.</summary>
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;

    public bool IsFinal { get; } = isFinal;

    /// <summary>Its place among the session's stops, in the order of the text.</summary>
    public int Order { get; set; }

    /// <summary>The stops of its number, itself included: its mirrors.</summary>
    public List<TabStop> Group { get; set; } = [];

    /// <summary>Whether it is selected, or around the selected stop: it grows as the user types at its edges.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Whether the text of a placeholder around it was replaced: it no longer counts, as in VS Code.</summary>
    public bool IsGone { get; set; }

    public int StartOffset => _start.Offset;

    public int EndOffset => Math.Max(_start.Offset, _end.Offset);

    /// <summary>Whether it is gone, or its text, or that of a placeholder around it, was deleted, as VS Code tells.</summary>
    public bool IsCollapsed
    {
        get
        {
            for (TabStop? stop = this; stop is not null; stop = stop.Parent)
            {
                if (stop.IsGone || (stop._hadText && stop.StartOffset == stop.EndOffset))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Makes the stop grow as the user types at its edges while it is selected. Otherwise text typed at its edges
    /// goes outside it, and at an empty one after it, as at the final stop after a placeholder.
    /// </summary>
    public void FollowEdits()
    {
        if (IsSelected)
            Grow();
        else if (StartOffset == EndOffset)
            Stay(after: true);
        else
            Keep();
    }

    /// <summary>Text that goes in at its edges goes inside it.</summary>
    public void Grow()
    {
        _start.MovementType = AnchorMovementType.BeforeInsertion;
        _end.MovementType = AnchorMovementType.AfterInsertion;
    }

    /// <summary>Text that goes in at its edges goes outside it.</summary>
    public void Keep()
    {
        _start.MovementType = AnchorMovementType.AfterInsertion;
        _end.MovementType = AnchorMovementType.BeforeInsertion;
    }

    /// <summary>An empty stop stays before text that goes in where it is, or comes after it.</summary>
    public void Stay(bool after)
    {
        AnchorMovementType movement = after ? AnchorMovementType.AfterInsertion : AnchorMovementType.BeforeInsertion;
        _start.MovementType = movement;
        _end.MovementType = movement;
    }

    /// <summary>Whether <paramref name="other"/> is inside it, by their placeholders.</summary>
    public bool Encloses(TabStop other)
    {
        for (TabStop? around = other.Parent; around is not null; around = around.Parent)
        {
            if (around == this)
                return true;
        }
        return false;
    }

    /// <summary>Whether a change goes inside the stop, as its anchors will move: also at an edge that grows.</summary>
    public bool Contains(DocumentChangeEventArgs e)
    {
        (int start, int end) = (StartOffset, EndOffset);
        int changeEnd = e.Offset + e.RemovalLength;
        if (e.Offset < start || changeEnd > end)
            return false;
        if (e.RemovalLength > 0 || (start < e.Offset && e.Offset < end))
            return true;
        return e.Offset == start
            ? _start.MovementType == AnchorMovementType.BeforeInsertion
            : _end.MovementType == AnchorMovementType.AfterInsertion;
    }

    /// <summary>Whether the stops share text, or one is inside the other; not when they only touch.</summary>
    public bool Overlaps(TabStop other) => StartOffset < other.EndOffset && other.StartOffset < EndOffset;
}
