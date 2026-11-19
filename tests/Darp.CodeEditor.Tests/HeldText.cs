using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Clipboard data whose text comes only once <see cref="Release"/> is called, as a platform's clipboard can take a
/// while. The read completes on the thread that releases it: a paste on the UI thread goes on before it returns.
/// </summary>
internal sealed class HeldText(string text) : IAsyncDataTransfer, IAsyncDataTransferItem
{
    private readonly string _text = text;
    private readonly TaskCompletionSource<object?> _read = new();

    public IReadOnlyList<DataFormat> Formats => [DataFormat.Text];

    public IReadOnlyList<IAsyncDataTransferItem> Items => [this];

    public void Release() => _read.TrySetResult(_text);

    public Task<object?> TryGetRawAsync(DataFormat format) =>
        format == DataFormat.Text ? _read.Task : Task.FromResult<object?>(null);

    public void Dispose() { }
}
