using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The text that a hover is about, in the current text, and what is known of it: from the pointer's arrival on a
/// character, or the key that asked for it, until the hover ends.
/// </summary>
/// <param name="offset">The character the pointer rested on, or the key asked at.</param>
/// <param name="word">The word there, which the popup is next to.</param>
/// <param name="along">The word characters around it, which the pointer may move along.</param>
/// <param name="snapshot">The text when the hover started: the hover ends when it changes.</param>
/// <param name="fromKeyboard">Whether the user asked for it by key, at the caret.</param>
internal sealed class Hovered(int offset, TextRange word, TextRange along, TextSnapshot snapshot, bool fromKeyboard)
{
    /// <summary>
    /// How far the hovered word reaches on either side of the pointer, at most, as in VS Code: a longer word is cut
    /// there, so that a line of millions of word characters costs the pointer no more than this.
    /// </summary>
    internal const int MaxWordReach = 1_000;

    private readonly TextRange _along = along;

    public int Offset { get; } = offset;

    public TextRange Word { get; } = word;

    public TextSnapshot Snapshot { get; } = snapshot;

    public bool FromKeyboard { get; } = fromKeyboard;

    /// <summary>The text that the popup stays open over: the word, and the server's range.</summary>
    public TextRange Range { get; set; } = word;

    /// <summary>The diagnostics' messages, once the delay is over.</summary>
    public IReadOnlyList<HoverBlock> Messages { get; set; } = [];

    /// <summary>What is left of the text the hover shows, for the server's hover after the messages.</summary>
    public HoverBudget Budget { get; } = new();

    /// <summary>Cancels the server's request and the reading of its answer.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The wait before the pointer's hover shows; null for a hover asked for by key.</summary>
    public ITimer? Delay { get; set; }

    /// <summary>The time the pointer has to reach the popup after leaving the text; null while it is over it.</summary>
    public ITimer? Leaving { get; set; }

    /// <summary>Whether the delay is over.</summary>
    public bool IsDue { get; set; }

    /// <summary>Whether <paramref name="offset"/> is in the hovered text, also along a word that was cut; reads no text.</summary>
    public bool IsOver(int offset) =>
        (offset >= Range.Start && offset < Math.Max(Range.End, Range.Start + 1))
        || (offset >= _along.Start && offset < _along.End);

    /// <summary>The pointer is back over the hover's text, or the wait is over.</summary>
    public void StopLeaving()
    {
        Leaving?.Dispose();
        Leaving = null;
    }

    public void Stop()
    {
        Delay?.Dispose();
        StopLeaving();
        Cancellation.Cancel();
    }

    /// <summary>The hover of the character at <paramref name="offset"/> of <paramref name="buffer"/>, its word found.</summary>
    public static Hovered At(TextDocument buffer, int offset, TextSnapshot snapshot, bool fromKeyboard)
    {
        TextRange word = WordAt(buffer, offset);
        return new Hovered(offset, word, AlongWord(buffer, word), snapshot, fromKeyboard);
    }

    /// <summary>
    /// The word characters around <paramref name="word"/>, beyond where <see cref="WordAt"/> cut it, in the shown part
    /// of its line, its first 3,000 characters: the pointer may move along them without ending the hover. Just the word
    /// when it is not one, as a space or a line's end. Reads at most the shown part of one line.
    /// </summary>
    internal static TextRange AlongWord(TextDocument buffer, TextRange word)
    {
        if (word.Length == 0 || !WordCharacters.IsWordCharacter(buffer.GetCharAt(word.Start)))
            return word;
        DocumentLine line = buffer.GetLineByOffset(word.Start);
        int start = word.Start;
        while (start > line.Offset && WordCharacters.IsWordCharacter(buffer.GetCharAt(start - 1)))
            start--;
        int last = VisibleText.End(line);
        int end = word.End;
        while (end < last && WordCharacters.IsWordCharacter(buffer.GetCharAt(end)))
            end++;
        return TextRange.FromBounds(start, end);
    }

    /// <summary>
    /// The word at <paramref name="offset"/>, by VS Code's word characters, reaching at most
    /// <see cref="MaxWordReach"/> characters to either side, and not into a long line's hidden tail; the character alone
    /// if it is none, and nothing at a line's end.
    /// </summary>
    internal static TextRange WordAt(TextDocument buffer, int offset)
    {
        DocumentLine line = buffer.GetLineByOffset(offset);
        if (offset == line.EndOffset)
            return new TextRange(offset, 0);
        if (!WordCharacters.IsWordCharacter(buffer.GetCharAt(offset)))
            return new TextRange(offset, 1);
        int first = Math.Max(line.Offset, offset - MaxWordReach);
        int start = offset;
        while (start > first && WordCharacters.IsWordCharacter(buffer.GetCharAt(start - 1)))
            start--;
        int last = Math.Min(VisibleText.End(line), offset + MaxWordReach);
        int end = offset;
        while (end < last && WordCharacters.IsWordCharacter(buffer.GetCharAt(end)))
            end++;
        return TextRange.FromBounds(start, end);
    }
}
