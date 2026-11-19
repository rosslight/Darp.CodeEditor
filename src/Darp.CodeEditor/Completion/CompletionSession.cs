using Darp.CodeEditor.Commands;
using Darp.CodeEditor.LanguageClient;

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
}

/// <summary>A server's list, with the request it answered.</summary>
internal sealed record CompletionAnswer(CompletionList List, CompletionRequest Request);

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

    /// <summary>What the frozen list's text is: its items show as they were, none of them matching; null otherwise.</summary>
    public FilterKey? FrozenAt { get; set; }

    /// <summary>Whether the list says "No suggestions." instead of showing items.</summary>
    public bool ShowsNoMatch { get; set; }

    /// <summary>Whether the commit that waits for a filter replaces.</summary>
    public bool CommitReplaces { get; set; }

    /// <summary>
    /// What the key that accepted for the commit that waits for a filter goes on to when the filter finds nothing; null
    /// when no key accepted.
    /// </summary>
    public Action? CommitGoOn { get; set; }

    /// <summary>The request asked last: the one under way, or the one whose answer shows.</summary>
    public CompletionRequest Latest =>
        Pending ?? Answer?.Request ?? throw new InvalidOperationException("Completion was not asked for.");

    /// <summary>Whether the list shows.</summary>
    public bool IsShowing => Shown is not null;

    /// <summary>What the items that show were filtered for; null while none show.</summary>
    public FilterKey? Shown { get; set; }

    /// <summary>The filter under way; null for none.</summary>
    public FilterRun? Filtering { get; set; }

    /// <summary>
    /// The filter whose selection Enter or Tab commits once it has run; null for none. A filter for anything else
    /// drops it.
    /// </summary>
    public FilterKey? CommitAt { get; set; }

    /// <summary>The items that show, in order.</summary>
    public List<ShownItem> Items { get; set; } = [];

    /// <summary>The selected item's index in <see cref="Items"/>.</summary>
    public int Selected { get; set; }

    /// <summary>Whether completion ended: what is still under way for it is dropped.</summary>
    public bool HasEnded { get; set; }
}
