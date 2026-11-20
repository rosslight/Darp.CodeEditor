using AvaloniaEdit.Document;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Completion;

/// <summary>A request for completion.</summary>
/// <param name="snapshot">The text it was asked for.</param>
/// <param name="word">The word before the caret then: what items without an edit replace.</param>
internal sealed class CompletionRequest(TextSnapshot snapshot, TextRange word)
{
    public TextSnapshot Snapshot { get; } = snapshot;

    public TextRange Word { get; } = word;

    /// <summary>
    /// Where the caret was, in <see cref="Snapshot"/>; also in the current text, while the request is not stale.
    /// </summary>
    public int Offset => Word.End;

    /// <summary>Cancels the request.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// Whether the text before <see cref="Offset"/> changed since: then the answer's positions no longer fit the
    /// current text.
    /// </summary>
    public bool IsStale { get; set; }

    /// <summary>
    /// The word before <paramref name="caret"/>, which completion asks for, by VS Code's word characters, which ends at
    /// the caret. It reaches back at most <see cref="HoverController.MaxWordReach"/> characters, as a hovered word does:
    /// a line of millions of word characters costs no more.
    /// </summary>
    public static TextRange WordBefore(TextDocument buffer, int caret)
    {
        DocumentLine line = buffer.GetLineByOffset(caret);
        int first = Math.Max(line.Offset, caret - HoverController.MaxWordReach);
        int start = caret;
        while (start > first && WordCharacters.IsWordCharacter(buffer.GetCharAt(start - 1)))
            start--;
        return TextRange.FromBounds(start, caret);
    }
}

/// <summary>A server's list, with the request it answered.</summary>
/// <param name="List">The list.</param>
/// <param name="Request">The request it answered.</param>
/// <param name="Clipboard">The clipboard's text, read for its snippets with <c>CLIPBOARD</c>; null for none.</param>
internal sealed record CompletionAnswer(CompletionList List, CompletionRequest Request, string? Clipboard = null)
{
    /// <summary>Whether it is the options of a snippet's choice: committing one goes on to the snippet's next stop.</summary>
    public bool IsChoice { get; init; }
}

/// <summary>An item's resolve, under way or done.</summary>
internal sealed record ItemResolve(Task<CompletionItem?> Task, CancellationTokenSource Cancellation);

/// <summary>What a list is filtered for: an answer, at a version of the text and a caret.</summary>
internal readonly record struct FilterKey(CompletionAnswer Answer, int Version, int Caret);

/// <summary>A filter under way.</summary>
internal sealed record FilterRun(FilterKey Key, CancellationTokenSource Cancellation);

/// <summary>A filter's result: the items that match, in order, and the one to select.</summary>
internal sealed record RankedItems(List<ShownItem> Items, int Selected);

/// <summary>Completion from its first request until it ends.</summary>
internal sealed class CompletionSession
{
    /// <summary>The answer whose items show; null until the first one.</summary>
    public CompletionAnswer? Answer { get; set; }

    /// <summary>The request under way; null for none.</summary>
    public CompletionRequest? Pending { get; set; }

    /// <summary>Whether the user asked by key: when nothing matches, the list freezes or says so.</summary>
    public bool ByKey { get; set; }

    /// <summary>The choice's text, selected as its options were asked for, which may stay selected; null for none.</summary>
    public TextRange? ChoiceSelection { get; set; }

    /// <summary>The request asked last: the one under way, or the one whose answer shows.</summary>
    public CompletionRequest Latest =>
        Pending ?? Answer?.Request ?? throw new InvalidOperationException("Completion was not asked for.");
}
