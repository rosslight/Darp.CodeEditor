using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Localization;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The text that a hover is about, in the current text, and what its hover knows of it: from the pointer's arrival on
/// a character, or the key that asked for it, until the hover ends.
/// </summary>
/// <param name="offset">The character the pointer rested on, or the key asked at.</param>
/// <param name="word">The word there, which the popup is next to.</param>
/// <param name="along">The word characters around it, which the pointer may move along.</param>
/// <param name="snapshot">The text when the hover started: the hover ends when it changes.</param>
/// <param name="fromKeyboard">Whether the user asked for it by key, at the caret.</param>
internal sealed class Hovered(int offset, TextRange word, TextRange along, TextSnapshot snapshot, bool fromKeyboard)
{
    private readonly TextRange _along = along;

    public int Offset { get; } = offset;

    public TextRange Word { get; } = word;

    public TextSnapshot Snapshot { get; } = snapshot;

    public bool FromKeyboard { get; } = fromKeyboard;

    /// <summary>The text that the popup stays open over: the word, and the server's range.</summary>
    public TextRange Range { get; private set; } = word;

    /// <summary>The diagnostics' messages, once the delay is over.</summary>
    public IReadOnlyList<HoverBlock> Messages { get; set; } = [];

    /// <summary>Cancels the server's request and the reading of its answer.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>The timers of the wait for the pointer's hover: for asking, showing and "Loading…".</summary>
    public ITimer[] Timers { get; set; } = [];

    /// <summary>Whether its delay is over, so that what is known shows.</summary>
    public bool IsDue { get; set; }

    /// <summary>Whether the server answered, also with nothing, or failed.</summary>
    public bool IsAnswered { get; set; }

    /// <summary>Whether the server is late: "Loading…" shows until it answers.</summary>
    public bool IsLate { get; set; }

    /// <summary>The server's hover, once it answered with one and its Markdown was read; null before.</summary>
    public IReadOnlyList<HoverBlock>? Answer { get; private set; }

    /// <summary>
    /// What shows once the delay is over: the diagnostics' messages, and below them the server's hover, or "Loading…"
    /// while it is late; nothing before. Not read here: the diagnostics' blocks are made as the hover's parts read them.
    /// </summary>
    public IEnumerable<HoverBlock> Blocks()
    {
        IReadOnlyList<HoverBlock> below =
            Answer ?? (IsLate && !IsAnswered ? [new HoverParagraph([new HoverSpan(Strings.HoverLoading)])] : []);
        return !IsDue || (Messages.Count == 0 && below.Count == 0) ? []
            : Messages.Count == 0 ? below
            : below.Count == 0 ? Messages
            : Messages.Append(new HoverRule()).Concat(below);
    }

    /// <summary>Takes the server's hover, which the popup stays open over too.</summary>
    public void Answered(IReadOnlyList<HoverBlock> answer, TextRange? range)
    {
        Answer = answer;
        if (range is { } over)
            Range = TextRange.FromBounds(Math.Min(Range.Start, over.Start), Math.Max(Range.End, over.End));
    }

    /// <summary>
    /// Whether <paramref name="offset"/> is in the hovered text: in its range, or further along a word that was cut.
    /// Reads no text: how far the word goes was found when the hover started.
    /// </summary>
    public bool IsOver(int offset) =>
        (offset >= Range.Start && offset < Math.Max(Range.End, Range.Start + 1))
        || (offset >= _along.Start && offset < _along.End);

    /// <summary>Stops the timers, the server's request and the reading of its answer.</summary>
    public void Stop()
    {
        foreach (ITimer timer in Timers)
            timer.Dispose();
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
    /// How far the hovered word reaches on either side of the pointer, at most, as in VS Code: a longer word is cut
    /// there, so that a line of millions of word characters costs the pointer no more than this.
    /// </summary>
    internal const int MaxWordReach = 1_000;

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
