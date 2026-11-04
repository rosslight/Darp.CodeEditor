namespace Darp.CodeEditor;

/// <summary>A selection in a document's text, and the caret at one of its ends.</summary>
/// <remarks>
/// The anchor is where the selection started, and the caret is where it ends: selecting backwards puts the caret
/// before the anchor. Offsets are not checked when created; the editor clamps them to the text.
/// </remarks>
/// <param name="Anchor">The offset where the selection started.</param>
/// <param name="Caret">The offset of the caret.</param>
public readonly record struct TextSelection(int Anchor, int Caret)
{
    /// <summary>No selection, with the caret at <paramref name="caret"/>.</summary>
    public TextSelection(int caret)
        : this(caret, caret) { }

    /// <summary>Whether nothing is selected.</summary>
    public bool IsEmpty => Anchor == Caret;

    /// <summary>The selected text's span, from its lower to its higher offset.</summary>
    public TextRange Range => TextRange.FromBounds(Math.Min(Anchor, Caret), Math.Max(Anchor, Caret));
}
