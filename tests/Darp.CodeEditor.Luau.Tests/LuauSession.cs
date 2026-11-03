using System.Text;
using System.Threading.Channels;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Luau.Tests;

/// <summary>A Luau language service on the luau-lsp that the targets put next to the tests, with the test API.</summary>
internal sealed class LuauSession : IAsyncDisposable
{
    private readonly StringBuilder _log = new();
    private readonly Channel<DiagnosticsChangedEventArgs> _diagnostics =
        Channel.CreateUnbounded<DiagnosticsChangedEventArgs>();

    private LuauSession(LuauEnvironment environment, LuauLanguageServerOptions options)
    {
        Service = LuauLanguageServer.CreateService(environment, options with { Log = Log });
        Service.DiagnosticsChanged += (_, e) => _diagnostics.Writer.TryWrite(e);
    }

    public LspLanguageService Service { get; }

    /// <summary>Cancelled when the test run is cancelled, or after 30 seconds.</summary>
    public static CancellationToken Timeout
    {
        get
        {
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return timeout.Token;
        }
    }

    /// <summary>The test API: <c>log</c>, <c>device</c> and <c>limits</c>, with documentation.</summary>
    public static LuauEnvironment Environment(LuauMode mode = LuauMode.Strict) =>
        new()
        {
            Definitions =
            [
                new LuauDefinitions("test", Path.Combine(AppContext.BaseDirectory, "TestApi", "test.d.luau")),
            ],
            DocumentationFiles = [Path.Combine(AppContext.BaseDirectory, "TestApi", "test.docs.json")],
            Mode = mode,
        };

    public static async Task<LuauSession> StartAsync(
        LuauEnvironment? environment = null,
        LuauLanguageServerOptions? options = null
    )
    {
        var session = new LuauSession(environment ?? Environment(), options ?? new LuauLanguageServerOptions());
        try
        {
            await session.Service.PrewarmAsync(Timeout);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
        return session;
    }

    /// <summary>Opens <paramref name="text"/> as version 1 of <paramref name="documentId"/>.</summary>
    public DocumentVersion Open(string documentId, string text)
    {
        Service.OpenDocument(new DocumentSnapshot(documentId, 1, text));
        return new DocumentVersion(documentId, 1);
    }

    /// <summary>The next diagnostics luau-lsp publishes for <paramref name="document"/>.</summary>
    public async Task<IReadOnlyList<DiagnosticInfo>> NextDiagnosticsAsync(DocumentVersion document)
    {
        CancellationToken timeout = Timeout;
        while (true)
        {
            DiagnosticsChangedEventArgs published = await _diagnostics.Reader.ReadAsync(timeout);
            if (published.DocumentId == document.DocumentId && published.Version == document.Version)
                return published.Diagnostics;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        lock (_log)
            TestContext.Current.TestOutputHelper?.WriteLine(_log.ToString());
    }

    private void Log(string message)
    {
        lock (_log)
            _log.AppendLine(message);
    }
}
