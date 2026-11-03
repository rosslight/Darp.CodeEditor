using System.Text.Json;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Hosting;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>How <see cref="LspLanguageService"/> starts its language server and talks to it.</summary>
/// <param name="StartServer">
/// Starts the server, usually with <see cref="LanguageServerTransport.StartProcessAsync"/>; called again after a crash.
/// </param>
/// <param name="LanguageId">The language id of opened documents, such as <c>csharp</c> or <c>luau</c>.</param>
public sealed record LspLanguageServiceOptions(
    Func<CancellationToken, Task<LanguageServerTransport>> StartServer,
    string LanguageId
)
{
    /// <summary>The server's <c>initializationOptions</c>.</summary>
    public JsonElement? InitializationOptions { get; init; }

    /// <summary>Answers the server's <c>workspace/configuration</c> requests per item; null lets it use its defaults.</summary>
    public Func<ConfigurationItem, JsonElement?>? Settings { get; init; }

    /// <summary>
    /// The URI of a document id. By default a rooted path becomes its <c>file</c> URI and any other id
    /// <c>file:///</c> followed by the escaped id.
    /// </summary>
    public Func<string, string>? DocumentUri { get; init; }

    /// <summary>The workspace folder sent in <c>initialize</c>, if the server needs one.</summary>
    public string? RootUri { get; init; }

    /// <summary>
    /// Semantic token types of the server, by name, in addition to the standard ones and C#'s; null leaves a type
    /// uncolored.
    /// </summary>
    public IReadOnlyDictionary<string, ClassificationKind?>? TokenTypes { get; init; }

    /// <summary>The client name in <c>initialize</c>.</summary>
    public string ClientName { get; init; } = "Darp.CodeEditor";

    /// <summary>Receives protocol errors, the server's log messages and its restarts.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long a crashed server waits before it restarts.</summary>
    public SupervisorOptions? Restart { get; init; }

    /// <summary>How much the server may send in one message.</summary>
    public JsonRpcLimits? Limits { get; init; }
}

/// <summary>Where a <see cref="LspLanguageService"/> is with its language server.</summary>
public enum LanguageServerState
{
    /// <summary>Not started: no document was opened and <see cref="LspLanguageService.PrewarmAsync"/> was not called.</summary>
    Stopped,

    /// <summary>The first start is in progress.</summary>
    Starting,

    /// <summary>The server runs and serves requests.</summary>
    Running,

    /// <summary>The server crashed or failed to start; it restarts after a delay. Requests answer nothing meanwhile.</summary>
    Restarting,

    Disposed,
}
