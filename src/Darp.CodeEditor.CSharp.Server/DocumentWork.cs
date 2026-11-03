using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Runs the work of a request on the snapshot of an open document that the request read.</summary>
internal static class DocumentWork
{
    /// <summary>
    /// Runs <paramref name="work"/> until it ends, the client cancels the request, or <paramref name="document"/>
    /// closes. A newer version does not stop it: the answer is for the text the request was sent for.
    /// </summary>
    /// <returns>The result, or null when the document closed before the result was ready.</returns>
    /// <remarks>
    /// LSP 3.17 on <see cref="ErrorCodes.ContentModified"/>: "A server should NOT send this error code if it detects a
    /// content change in its unprocessed messages. The result even computed on an older state might still be useful
    /// for the client. If a client decides that a result is not of any use anymore the client should cancel the
    /// request." A cancelled request answers <see cref="ErrorCodes.RequestCancelled"/>. After <c>didClose</c> the
    /// server no longer knows the document, so it answers as for any document it does not know: with null, which
    /// completion, hover, signature help and semantic tokens all allow.
    /// </remarks>
    public static async Task<T?> RunAsync<T>(
        CancellationToken request,
        OpenDocument document,
        Func<CancellationToken, Task<T?>> work
    )
        where T : class
    {
        CancellationToken closed = document.Closed;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request, closed);
        try
        {
            T? result = await work(cancellation.Token).ConfigureAwait(false);
            return closed.IsCancellationRequested ? null : result;
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested && !request.IsCancellationRequested)
        {
            return null;
        }
    }
}
