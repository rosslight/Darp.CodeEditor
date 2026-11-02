using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Runs request work that a change of the document makes worthless.</summary>
internal static class StaleWork
{
    /// <summary>
    /// Runs <paramref name="work"/> until it ends, the client cancels the request, or <paramref name="stale"/> is
    /// cancelled, such as by a new version of the document. A stale request answers
    /// <see cref="ErrorCodes.ContentModified"/>, which clients drop silently; a cancelled one answers
    /// <see cref="ErrorCodes.RequestCancelled"/>.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        CancellationToken request,
        CancellationToken stale,
        Func<CancellationToken, Task<T>> work
    )
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(request, stale);
        try
        {
            return await work(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stale.IsCancellationRequested && !request.IsCancellationRequested)
        {
            throw new JsonRpcException(ErrorCodes.ContentModified, "The document changed; the result would be stale.");
        }
    }
}
