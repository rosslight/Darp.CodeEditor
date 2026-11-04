using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Darp.CodeEditor;

/// <summary>
/// A language server process that speaks the Language Server Protocol over stdio. All documents of the languages it
/// serves share it.
/// </summary>
/// <remarks>
/// <para>
/// The process starts with the first document that needs it, or with <see cref="StartAsync"/>. It is restarted after
/// a crash or when it stops answering, and stopped by <see cref="DisposeAsync"/>. It is a child of the app: it ends
/// when the app ends, even when the app is killed.
/// </para>
/// <para>
/// The server is safe to use from any thread. It raises <see cref="StateChanged"/> on the UI thread.
/// </para>
/// </remarks>
public sealed class LanguageServer : IAsyncDisposable
{
    /// <summary>Describes a language server. The process does not start yet.</summary>
    public LanguageServer(LanguageServerOptions options) => throw new NotImplementedException();

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Where the process is in its life, for a status indicator.</summary>
    public LanguageServerState State => throw new NotImplementedException();

    /// <summary>
    /// Starts the process now rather than with the first document, so that language features are ready sooner.
    /// Completes when the server has answered the protocol's <c>initialize</c> request.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Stops the process. Documents of its languages keep their text and lose their language features.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>How to start a language server and what to tell it.</summary>
public sealed record LanguageServerOptions
{
    /// <summary>The server's executable.</summary>
    public required string FileName { get; init; }

    /// <summary>The command-line arguments, such as <c>--stdio</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The working directory of the process. Default: the app's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Environment variables to set or, with a null value, to remove for the process.</summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>The workspace folder the server receives, if it needs one to find configuration or other files.</summary>
    public string? RootPath { get; init; }

    /// <summary>The <c>initializationOptions</c> of the <c>initialize</c> request, in the server's own format.</summary>
    public JsonNode? InitializationOptions { get; init; }

    /// <summary>
    /// Answers the server's <c>workspace/configuration</c> requests: gets a section name, such as <c>yaml</c>, and
    /// returns its settings, or null when the app has none. Called on any thread.
    /// </summary>
    public Func<string?, JsonNode?>? Settings { get; init; }

    /// <summary>Receives the server's standard error and the client's own messages: starts, restarts, failures.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>Where a <see cref="LanguageServer"/>'s process is in its life.</summary>
public enum LanguageServerState
{
    /// <summary>No document needed the server yet.</summary>
    NotStarted,

    /// <summary>The process is starting and has not answered <c>initialize</c> yet.</summary>
    Starting,

    /// <summary>The server answers requests.</summary>
    Running,

    /// <summary>The process crashed or stopped answering, and a new one starts after a short delay.</summary>
    Restarting,

    /// <summary>The process failed too often in a row; no new one starts until the next <see cref="LanguageServer.StartAsync"/>.</summary>
    Failed,

    /// <summary>The server was disposed.</summary>
    Stopped,
}
