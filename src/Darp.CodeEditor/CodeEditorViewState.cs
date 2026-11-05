namespace Darp.CodeEditor;

/// <summary>
/// Where an editor was in its document: the caret, the selection and the scroll position. Taken with
/// <see cref="CodeEditor.SaveViewState"/> and given back with <see cref="CodeEditor.RestoreViewState"/>.
/// </summary>
/// <remarks>
/// It refers to the document's text at the time it was saved; restoring it follows the edits made since, as
/// <see cref="CodeDocument.MapToCurrent"/> does. It lives in memory only and cannot be stored across app starts.
/// Immutable, so it can be kept anywhere, such as in a tab's view model.
/// </remarks>
public sealed class CodeEditorViewState
{
    internal CodeEditorViewState(
        CodeDocument document,
        TextSnapshot snapshot,
        TextSelection selection,
        ScrollPlace scroll
    )
    {
        Document = document;
        Snapshot = snapshot;
        Selection = selection;
        Scroll = scroll;
    }

    /// <summary>The document the state belongs to.</summary>
    public CodeDocument Document { get; }

    /// <summary>The text that <see cref="Selection"/> and <see cref="Scroll"/> refer to.</summary>
    internal TextSnapshot Snapshot { get; }

    internal TextSelection Selection { get; }

    internal ScrollPlace Scroll { get; }

    /// <summary>The same place in the document's current text, following the edits made since.</summary>
    internal CodeEditorViewState ToCurrent()
    {
        TextRange range = Document.MapToCurrent(Snapshot, Selection.Range);
        TextSelection selection =
            Selection.Anchor <= Selection.Caret ? new(range.Start, range.End) : new(range.End, range.Start);
        int top = Document.MapToCurrent(Snapshot, new TextRange(Scroll.TopOffset, 0)).Start;
        return new CodeEditorViewState(Document, Document.CurrentSnapshot, selection, Scroll with { TopOffset = top });
    }
}

/// <summary>
/// A scroll position that follows edits, as Monaco keeps it: an offset in the line at the top of the view, how far the
/// view is scrolled past that line's top, and the horizontal offset, both in pixels.
/// </summary>
internal readonly record struct ScrollPlace(int TopOffset, double TopDelta, double Horizontal);
