using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.Lsp.Testing;

/// <summary>A luau-lsp process with a connection to it, for the interop checks.</summary>
internal sealed class LuauLsp : IAsyncDisposable
{
    /// <summary>A Luau script with a function, an unfinished member access, and a call to hover over.</summary>
    public const string Script = """
        local function greet(name: string): string
            return "Hello, " .. name
        end

        local s = string.
        print(greet("you"))

        """;

    /// <summary>Right after <c>string.</c>.</summary>
    public static readonly Position MemberAccess = new(4, 17);

    /// <summary>On <c>greet</c> in the call.</summary>
    public static readonly Position Call = new(5, 8);

    private readonly Process _process;
    private readonly StringBuilder _log = new();

    public LuauLsp(string executable)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("lsp");
        startInfo.ArgumentList.Add("--stdio");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("luau-lsp did not start.");
        _process.ErrorDataReceived += (_, e) => Log($"stderr: {e.Data}");
        _process.BeginErrorReadLine();
        Connection = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream, Log);
        // Requests that luau-lsp sends to its client. A null setting makes it use its default.
        Connection.OnRequest(
            LspMethods.WorkspaceConfiguration,
            (p, _) => Task.FromResult<IReadOnlyList<JsonElement?>?>([.. p.Items.Select(_ => (JsonElement?)null)])
        );
        Connection.OnRequest(LspMethods.RegisterCapability, (_, _) => Task.FromResult<JsonElement?>(null));
        Connection.OnRequest(LspMethods.WorkDoneProgressCreate, (_, _) => Task.FromResult<JsonElement?>(null));
        Connection.OnNotification(
            LspMethods.PublishDiagnostics,
            (p, token) => Diagnostics.Writer.WriteAsync(p, token).AsTask()
        );
        Connection.OnNotification(
            LspMethods.LogMessage,
            (p, _) =>
            {
                Log($"log: {p.Message}");
                return Task.CompletedTask;
            }
        );
        Connection.Start();
    }

    public JsonRpcConnection Connection { get; }

    public Channel<PublishDiagnosticsParams> Diagnostics { get; } = Channel.CreateUnbounded<PublishDiagnosticsParams>();

    /// <summary>What luau-lsp wrote to stderr and logged, and protocol errors.</summary>
    public string Output
    {
        get
        {
            lock (_log)
                return _log.ToString();
        }
    }

    /// <summary>
    /// The luau-lsp that <c>Darp.CodeEditor.Luau.targets</c> puts next to the tests, or the one <c>LUAU_LSP</c> names;
    /// null when missing.
    /// </summary>
    public static string? FindExecutable()
    {
        string path = Environment.GetEnvironmentVariable("LUAU_LSP") is { Length: > 0 } named
            ? named
            : Path.Combine(
                AppContext.BaseDirectory,
                "helpers",
                "luau-lsp",
                OperatingSystem.IsWindows() ? "luau-lsp.exe" : "luau-lsp"
            );
        return File.Exists(path) ? path : null;
    }

    public static InitializeParams InitializeParams(string rootUri) =>
        new(
            new ClientCapabilities
            {
                General = new GeneralClientCapabilities { PositionEncodings = ["utf-16"] },
                Workspace = new WorkspaceClientCapabilities { Configuration = true, WorkspaceFolders = true },
                TextDocument = new TextDocumentClientCapabilities
                {
                    Completion = new CompletionClientCapabilities
                    {
                        ContextSupport = true,
                        CompletionItem = new CompletionItemClientCapabilities
                        {
                            DocumentationFormat = [MarkupKind.Markdown, MarkupKind.PlainText],
                            InsertReplaceSupport = true,
                            LabelDetailsSupport = true,
                            ResolveSupport = new CompletionResolveSupport(["documentation", "detail"]),
                        },
                    },
                    Hover = new HoverClientCapabilities { ContentFormat = [MarkupKind.Markdown, MarkupKind.PlainText] },
                    PublishDiagnostics = new PublishDiagnosticsClientCapabilities { VersionSupport = true },
                    SemanticTokens = new SemanticTokensClientCapabilities(
                        new SemanticTokensRequests(Range: true, Full: true),
                        ["namespace", "type", "class", "function", "method", "property", "variable", "parameter"],
                        ["readonly", "defaultLibrary"],
                        ["relative"]
                    ),
                },
            }
        )
        {
            ProcessId = Environment.ProcessId,
            ClientInfo = new ClientInfo("Darp.CodeEditor.Lsp tests"),
            RootUri = rootUri,
            WorkspaceFolders = [new WorkspaceFolder(rootUri, "scripts")],
        };

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill();
        await _process.WaitForExitAsync();
        await Connection.DisposeAsync();
        _process.Dispose();
    }

    private void Log(string? message)
    {
        lock (_log)
            _log.AppendLine(message);
    }
}
