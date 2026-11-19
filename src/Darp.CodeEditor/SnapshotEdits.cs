using AvaloniaEdit.Document;

namespace Darp.CodeEditor;

/// <summary>
/// The edits from a snapshot of a document to a later one, read once, to map many ranges over them as
/// <see cref="CodeDocument.MapToCurrent"/> maps one. Any thread may use it.
/// </summary>
internal sealed class SnapshotEdits
{
    private readonly TextChangeEventArgs[] _edits;

    /// <exception cref="ArgumentException">The snapshots are of different documents.</exception>
    public SnapshotEdits(TextSnapshot snapshot, TextSnapshot later)
    {
        if (
            snapshot.SourceVersion is not { } version
            || later.SourceVersion is not { } target
            || !version.BelongsToSameDocumentAs(target)
        )
        {
            throw new ArgumentException("The snapshot is of another document.", nameof(snapshot));
        }
        Snapshot = snapshot;
        _edits = [.. version.GetChangesTo(target)];
    }

    /// <summary>The snapshot that the ranges to map are about.</summary>
    public TextSnapshot Snapshot { get; }

    /// <summary>
    /// <paramref name="range"/>, clamped to the text of <see cref="Snapshot"/>, in the later text. Text typed at either
    /// edge stays outside it, and a range whose text was deleted shrinks to where it was.
    /// </summary>
    public TextRange Map(TextRange range)
    {
        (int start, int length) = Snapshot.Clamp(range);
        int end = start + length;
        foreach (TextChangeEventArgs edit in _edits)
        {
            start = edit.GetNewOffset(start, AnchorMovementType.AfterInsertion);
            end = edit.GetNewOffset(end, AnchorMovementType.BeforeInsertion);
        }
        return TextRange.FromBounds(start, Math.Max(start, end));
    }

    /// <summary>
    /// Whether an edit changed the text before <paramref name="offset"/>, in the text of <see cref="Snapshot"/>: one
    /// whose range, before it was made, ended before where the offset was then.
    /// </summary>
    public bool EndsBefore(int offset)
    {
        foreach (TextChangeEventArgs edit in _edits)
        {
            if (edit.Offset + edit.RemovalLength < offset)
                return true;
            offset = edit.GetNewOffset(offset, AnchorMovementType.AfterInsertion);
        }
        return false;
    }

    /// <summary>
    /// Whether an edit changed the text of <paramref name="range"/>, in the text of <see cref="Snapshot"/>, or took away
    /// where an empty range is: it removed some of it, or put text inside it.
    /// </summary>
    public bool Overlaps(TextRange range)
    {
        (int start, int length) = Snapshot.Clamp(range);
        int end = start + length;
        foreach (TextChangeEventArgs edit in _edits)
        {
            int removed = edit.Offset + edit.RemovalLength;
            if ((edit.Offset < end && removed > start) || (edit.Offset > start && edit.Offset < end))
                return true;
            start = edit.GetNewOffset(start, AnchorMovementType.AfterInsertion);
            end = edit.GetNewOffset(end, AnchorMovementType.BeforeInsertion);
        }
        return false;
    }
}
