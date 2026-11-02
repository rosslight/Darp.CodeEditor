namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// Language features for one kind of document.
/// </summary>
/// <remarks>
/// <para>
/// Every member is asynchronous and self-contained: a request carries the document text and the version it was made
/// for, and all types are plain data. The same contract can therefore be served in-process or over JSON-RPC from
/// another process.
/// </para>
/// <para>
/// The editor calls a service only from thread-pool threads, never from the UI thread, and never waits for it while
/// laying out or rendering. A keystroke cancels requests that are in flight. The editor maps results for an older
/// version forward to the current text or drops them.
/// </para>
/// </remarks>
public interface ILanguageService
{
    LanguageServiceCapabilities Capabilities { get; }

    /// <summary>Computes the completion list at <paramref name="position"/>. Returns null when nothing applies.</summary>
    /// <remarks>Items are sorted. The editor filters the list locally while the user keeps typing.</remarks>
    Task<CompletionList?> GetCompletionsAsync(
        DocumentSnapshot document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    );

    /// <summary>Loads the description of one item of a list returned by <see cref="GetCompletionsAsync"/>.</summary>
    /// <returns>Null when the list is no longer known to the service or the item has no description.</returns>
    Task<string?> GetCompletionDescriptionAsync(CompletionItemReference item, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        DocumentSnapshot document,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns semantic classifications in <paramref name="range"/>: only what syntax highlighting cannot know,
    /// such as whether an identifier is a type, a method or a local.
    /// </summary>
    Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentSnapshot document,
        TextRange range,
        CancellationToken cancellationToken
    );

    Task<QuickInfo?> GetQuickInfoAsync(DocumentSnapshot document, int position, CancellationToken cancellationToken);

    Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentSnapshot document,
        int position,
        CancellationToken cancellationToken
    );
}
