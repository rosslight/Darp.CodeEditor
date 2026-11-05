using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Darp.CodeEditor;

/// <summary>
/// A language server process that speaks the Language Server Protocol over stdio. It serves the documents it creates,
/// in one or more languages.
/// </summary>
/// <remarks>
/// <para>
/// The process starts with the first document it creates, or with <see cref="StartAsync"/>. It is restarted after
/// a crash or when it stops answering, and stopped by <see cref="DisposeAsync"/>. It is a child of the app: it ends
/// when the app ends, even when the app is killed.
/// </para>
/// <para>
/// The server is safe to use from any thread. It posts <see cref="StateChanged"/> to the UI thread, and nothing waits
/// for it, so a server also works before the app's UI runs.
/// </para>
/// </remarks>
public sealed class LanguageServer : IAsyncDisposable
{
    /// <summary>Describes a language server. The process does not start yet.</summary>
    public LanguageServer(LanguageServerOptions options) => throw new NotImplementedException();

    /// <summary>Raised on the UI thread after <see cref="State"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Where the process is in its life, for a status indicator.</summary>
    public LanguageServerState State => throw new NotImplementedException();

    /// <summary>
    /// Starts the process now rather than with the first document, so that language features are ready sooner.
    /// Completes when the server has answered the protocol's <c>initialize</c> request.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>
    /// Creates a document that the server serves, open in it until the document's <see cref="CodeDocument.Dispose"/>.
    /// </summary>
    /// <param name="language">The language: highlighting, editing rules and the identifier the server receives.</param>
    /// <param name="text">The initial text.</param>
    /// <param name="filePath">
    /// The file the text belongs to, if any. Servers that look at nearby files or configuration use it. The document
    /// never reads or writes it.
    /// </param>
    /// <exception cref="InvalidOperationException">Another open document of this server has <paramref name="filePath"/>.</exception>
    public CodeDocument CreateDocument(CodeLanguage language, string text = "", string? filePath = null) =>
        throw new NotImplementedException();

    /// <summary>
    /// Tells the server that the answers of <see cref="LanguageServerOptions.Settings"/> may have changed, so that it
    /// asks again.
    /// </summary>
    public void NotifySettingsChanged() => throw new NotImplementedException();

    /// <summary>Stops the process. Its documents keep their text and lose their language features.</summary>
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
    /// Answers the server's <c>workspace/configuration</c> requests: gets the section, such as <c>yaml</c>, and the file
    /// or folder it is for, and returns the settings, or null when the app has none. Called on any thread.
    /// </summary>
    public Func<LanguageServerSettingsRequest, JsonNode?>? Settings { get; init; }

    /// <summary>Receives the server's standard error and the client's own messages: starts, restarts, failures.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>A language server's question for settings.</summary>
/// <param name="Section">The settings section, such as <c>yaml</c>; null for all settings.</param>
/// <param name="ScopePath">The file or folder the settings are for; null for settings that apply everywhere.</param>
public sealed record LanguageServerSettingsRequest(string? Section, string? ScopePath);

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
