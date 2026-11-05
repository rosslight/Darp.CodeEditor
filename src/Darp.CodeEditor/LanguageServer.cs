using System.ComponentModel;
using System.Text.Json.Nodes;

namespace Darp.CodeEditor;

/// <summary>
/// A language server process that speaks the Language Server Protocol over stdio. It serves the documents it creates,
/// in one or more languages.
/// </summary>
/// <remarks>
/// <para>
/// The process starts with the first document it creates, or with <see cref="StartAsync"/>. It is restarted after
/// a crash or when it stops answering, and stopped by <see cref="DisposeAsync"/>, or at once by <see cref="Dispose"/>.
/// It is a child of the app: it ends when the app ends, even when the app is killed.
/// </para>
/// <para>
/// The server is safe to use from any thread. It raises <see cref="PropertyChanged"/> for <see cref="State"/> on the
/// UI thread it belongs to, for a status indicator to bind, and nothing waits for it: that of the thread that created
/// it, if that thread has an Avalonia dispatcher, and otherwise the app's main UI thread. Its documents get their
/// diagnostics on their own UI thread. Before Avalonia is set up, it leaves Avalonia's
/// dispatcher alone and raises the event where the state changed, so a server also works before the app's UI runs.
/// </para>
/// <para>
/// When the process fails, <see cref="State"/> shows the restart, <see cref="StartAsync"/> throws why a process did
/// not start, and the server's own messages arrive through <see cref="LanguageServerOptions.StandardError"/>.
/// </para>
/// </remarks>
public sealed class LanguageServer : INotifyPropertyChanged, IAsyncDisposable, IDisposable
{
    /// <summary>Describes a language server. The process does not start yet.</summary>
    public LanguageServer(LanguageServerOptions options) => throw new NotImplementedException();

    /// <summary>Raised on the UI thread after <see cref="State"/> changed; see the remarks for before the UI runs.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the process is in its life, for a status indicator.</summary>
    public LanguageServerState State => throw new NotImplementedException();

    /// <summary>
    /// Starts the process now rather than with the first document, so that language features are ready sooner.
    /// Completes when the server has answered the protocol's <c>initialize</c> request. After
    /// <see cref="LanguageServerState.Failed"/>, it starts a new process.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process did not start or did not answer in time. The message says why, with the end of its standard error.
    /// </exception>
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

    /// <summary>
    /// Kills the process at once, without the protocol's shutdown, for app exits where only synchronous disposal
    /// runs. Its documents keep their text and lose their language features.
    /// </summary>
    public void Dispose() => throw new NotImplementedException();
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

    /// <summary>
    /// Receives each line that the server writes to standard error, on any thread, for the app's log: servers explain
    /// their failures there. Nothing else arrives here; <see cref="LanguageServer.State"/> and
    /// <see cref="LanguageServer.StartAsync"/> report what the client does.
    /// </summary>
    public Action<string>? StandardError { get; init; }

    /// <summary>
    /// The clock for the delay before a restart and for how long the server may take to answer before it counts as
    /// hung. Default: <see cref="TimeProvider.System"/>; tests pass their own.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
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
