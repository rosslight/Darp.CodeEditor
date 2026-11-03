using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>A language service whose answers the test gives: one completion item, its resolve, and signature help.</summary>
internal sealed class ScriptedService : ILanguageService
{
    private readonly List<SignatureRequest> _signatureRequests = [];

    /// <summary>The range the item replaces, if it differs from the list's: the letter typed at offset 2.</summary>
    public TextRange? ItemRange { get; init; }

    /// <summary>Completes the resolve of the item.</summary>
    public TaskCompletionSource<CompletionItemDetails?> Details { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<SignatureRequest> SignatureRequests
    {
        get
        {
            lock (_signatureRequests)
                return [.. _signatureRequests];
        }
    }

    public LanguageServiceCapabilities Capabilities { get; } = new([], [], ['(']);

    public event EventHandler? Invalidated
    {
        add { }
        remove { }
    }

    public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged
    {
        add { }
        remove { }
    }

    public void OpenDocument(DocumentSnapshot document) { }

    public void ChangeDocument(DocumentChange change) { }

    public void CloseDocument(string documentId) { }

    public Task<CompletionList?> GetCompletionsAsync(
        DocumentVersion document,
        int position,
        CompletionTrigger trigger,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult<CompletionList?>(
            new CompletionList(
                1,
                new TextRange(2, 1),
                [new CompletionItem("beta", "beta", "beta", CompletionItemKind.Class, Range: ItemRange)]
            )
        );

    public Task<CompletionItemDetails?> ResolveCompletionItemAsync(
        CompletionItemReference item,
        CancellationToken cancellationToken
    ) => Details.Task;

    public Task<IReadOnlyList<ClassifiedRange>> GetClassificationsAsync(
        DocumentVersion document,
        TextRange range,
        CancellationToken cancellationToken
    ) => Task.FromResult<IReadOnlyList<ClassifiedRange>>([]);

    public Task<QuickInfo?> GetQuickInfoAsync(
        DocumentVersion document,
        int position,
        CancellationToken cancellationToken
    ) => Task.FromResult<QuickInfo?>(null);

    public Task<SignatureHelp?> GetSignatureHelpAsync(
        DocumentVersion document,
        int position,
        SignatureHelpTrigger trigger,
        CancellationToken cancellationToken
    )
    {
        var request = new SignatureRequest(document, position, trigger, cancellationToken);
        lock (_signatureRequests)
            _signatureRequests.Add(request);
        return request.Answer.Task;
    }

    /// <summary>A signature help request; the test answers it.</summary>
    internal sealed record SignatureRequest(
        DocumentVersion Document,
        int Position,
        SignatureHelpTrigger Trigger,
        CancellationToken Token
    )
    {
        public TaskCompletionSource<SignatureHelp?> Answer { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
