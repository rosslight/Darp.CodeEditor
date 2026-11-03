namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// Language features for one kind of document, usually from a language server (<see cref="LspLanguageService"/>).
/// </summary>
/// <remarks>
/// <para>
/// The editor calls every member on the UI thread, and implementations must return at once: they queue their work and
/// complete the returned task later, on any thread. The editor never waits for a task while it lays out or renders.
/// Because calls arrive in the order the user acted, a request always follows the changes that led to its version.
/// </para>
/// <para>
/// Documents are opened, changed and closed incrementally; requests name a document version and use offsets into
/// that version's text. A keystroke cancels requests that are in flight, except completion, which the editor keeps
/// filtering while the user types the same word. The editor maps results for an older version forward to the current
/// text or drops them.
/// </para>
/// </remarks>
public interface ILanguageService
{
    /// <summary>The trigger and commit characters; they can change when <see cref="Invalidated"/> is raised.</summary>
    LanguageServiceCapabilities Capabilities { get; }

    /// <summary>
    /// Raised on any thread when earlier results may be out of date although no document changed, for example because
    /// a language server restarted. Editors request their results again.
    /// </summary>
    event EventHandler? Invalidated;

    /// <summary>Raised on any thread with the diagnostics of one document version, which replace earlier ones.</summary>
    event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

    void OpenDocument(DocumentSnapshot document);

    void ChangeDocument(DocumentChange change);

    void CloseDocument(string documentId);

    /// <summary>Computes the completion list at <paramref name="position"/>. Returns null when nothing applies.</summary>
    /// <remarks>Items are sorted. The editor filters the list locally while the user keeps typing.</remarks>
    Task<CompletionList?> GetCompletionsAsync(
        DocumentVersion document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Loads what an item of a list from <see cref="GetCompletionsAsync"/> leaves out: its description and the edits
    /// that go with it, such as a <c>using</c> directive.
    /// </summary>
    /// <returns>Null when the list is no longer known to the service.</returns>
    Task<CompletionItemDetails?> ResolveCompletionItemAsync(
        CompletionItemReference item,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Returns semantic classifications in <paramref name="range"/>, usually the visible lines: only what syntax
    /// highlighting cannot know, such as whether an identifier is a type, a method or a local.
    /// </summary>
    Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentVersion document,
        TextRange range,
        CancellationToken cancellationToken
    );

    Task<QuickInfo?> GetQuickInfoAsync(DocumentVersion document, int position, CancellationToken cancellationToken);

    Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentVersion document,
        int position,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    );
}
