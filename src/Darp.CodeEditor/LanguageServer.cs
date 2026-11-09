using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Avalonia;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor;

/// <summary>
/// A language server process that speaks the Language Server Protocol over stdio. It serves the documents it creates,
/// in one or more languages.
/// </summary>
/// <remarks>
/// <para>
/// The process starts with the first document it creates, or with <see cref="StartAsync"/>. It is restarted after
/// a crash or when it stops answering, and stopped by <see cref="DisposeAsync"/>, or at once by <see cref="Dispose"/>.
/// It is a child of the app: disposing the server ends it. When the app is killed instead, Windows ends it with the app;
/// on Linux and macOS it ends once it notices that its input closed or that the app's process, whose id it gets in
/// <c>initialize</c>, is gone, as the protocol asks servers to do.
/// </para>
/// <para>
/// The server is safe to use from any thread. It raises <see cref="PropertyChanged"/> for <see cref="State"/> on the
/// app's main UI thread, for a status indicator to bind, and nothing waits for it. Its documents get their diagnostics
/// there too. Before Avalonia is set up, it leaves Avalonia's
/// dispatcher alone and raises the event where the state changed, so a server also works before the app's UI runs.
/// </para>
/// <para>
/// When the process fails, <see cref="State"/> shows the restart, <see cref="StartAsync"/> throws why a process did
/// not start, and the server's own messages arrive through <see cref="LanguageServerOptions.StandardError"/>.
/// </para>
/// </remarks>
public sealed class LanguageServer : INotifyPropertyChanged, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// How many failures in a row make the server <see cref="LanguageServerState.Failed"/>. A server that fails at once
    /// gets about 8 seconds of restarts, with delays from 0.5 to 4 seconds.
    /// </summary>
    private const int MaxFailures = 5;

    private static readonly TimeSpan s_firstRestartDelay = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan s_maxRestartDelay = TimeSpan.FromSeconds(30);

    /// <summary>A server that has been running this long counts as stable: the failures and hangs before it are forgiven.</summary>
    private static readonly TimeSpan s_stableAfter = TimeSpan.FromSeconds(30);

    /// <summary>How long a request may go unanswered; a server that has not answered yet gets twice that.</summary>
    private static readonly TimeSpan s_firstHangLimit = TimeSpan.FromSeconds(30);

    /// <summary>Each hang doubles the limit, up to this, which also caps what a new server gets.</summary>
    private static readonly TimeSpan s_maxHangLimit = TimeSpan.FromMinutes(2);

    /// <summary>How long <see cref="DisposeAsync"/> waits for <c>shutdown</c> and <c>exit</c> before it kills, as VS Code.</summary>
    private static readonly TimeSpan s_shutdownLimit = TimeSpan.FromSeconds(2);

    private static readonly PropertyChangedEventArgs s_stateChanged = new(nameof(State));

    private readonly LanguageServerOptions _options;

    /// <summary>Guards the server's state, its documents and its connections' state.</summary>
    private readonly Lock _lock = new();

    /// <summary>Guarded: the open documents.</summary>
    private readonly List<ServerDocument> _documents = [];

    /// <summary>Guarded.</summary>
    private LanguageServerState _state;

    /// <summary>
    /// Guarded: the process while <see cref="LanguageServerState.Starting"/> or <see cref="LanguageServerState.Running"/>,
    /// the next one while <see cref="LanguageServerState.Restarting"/>, otherwise null.
    /// </summary>
    private ServerConnection? _connection;

    /// <summary>Guarded: starts the next process after its delay.</summary>
    private ITimer? _restartTimer;

    /// <summary>Guarded: processes that failed in a row.</summary>
    private int _failures;

    /// <summary>Guarded: processes that hung in a row.</summary>
    private int _hangs;

    /// <summary>Describes a language server. The process does not start yet.</summary>
    public LanguageServer(LanguageServerOptions options) => _options = options;

    /// <summary>Raised on the UI thread after <see cref="State"/> changed; see the remarks for before the UI runs.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the process is in its life, for a status indicator.</summary>
    public LanguageServerState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>
    /// Starts the process now rather than with the first document, so that language features are ready sooner.
    /// Completes when the server has answered the protocol's <c>initialize</c> request. After
    /// <see cref="LanguageServerState.Failed"/>, it starts a new process.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process did not start or did not answer in time. The message says why, with the end of its standard error.
    /// </exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ServerConnection connection;
        bool changed = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_state == LanguageServerState.Stopped, this);
            if (_connection is null)
            {
                // Not started yet, or failed: a new start, with nothing to forgive.
                _failures = 0;
                _hangs = 0;
                changed = StartConnection();
            }
            connection = _connection;
        }
        if (changed)
            OnStateChanged();
        // The process keeps starting for the documents when the caller stops waiting.
        if (await connection.Initialized.WaitAsync(cancellationToken).ConfigureAwait(false) is { } failure)
            throw failure;
    }

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
    public CodeDocument CreateDocument(CodeLanguage language, string text = "", string? filePath = null)
    {
        var document = new ServerDocument(this, language, text, filePath, DocumentUris.Of(filePath));
        bool changed = false;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_state == LanguageServerState.Stopped, this);
            ThrowIfOpen(document.Uri, filePath, document);
            _documents.Add(document);
            if (_state == LanguageServerState.NotStarted)
                changed = StartConnection();
            else if (_state == LanguageServerState.Running)
                _connection?.Open(document);
        }
        if (changed)
            OnStateChanged();
        return document;
    }

    /// <summary>
    /// Tells the server that the answers of <see cref="LanguageServerOptions.Settings"/> may have changed: the client
    /// sends all settings again, the answer for a null section, and servers that ask for their settings ask again.
    /// </summary>
    public void NotifySettingsChanged()
    {
        // A server that starts asks for its settings anyway.
        lock (_lock)
            _connection?.NotifySettingsChanged();
    }

    /// <summary>Stops the process. Its documents keep their text and lose their language features.</summary>
    public async ValueTask DisposeAsync()
    {
        ServerConnection? connection;
        lock (_lock)
        {
            if (_state == LanguageServerState.Stopped)
                return;
            connection = Stop();
        }
        OnStateChanged();
        if (connection is not null)
            await connection.StopAsync(s_shutdownLimit).ConfigureAwait(false);
    }

    /// <summary>
    /// Kills the process at once, without the protocol's shutdown, for app exits where only synchronous disposal
    /// runs. Its documents keep their text and lose their language features.
    /// </summary>
    public void Dispose()
    {
        ServerConnection? connection;
        lock (_lock)
        {
            if (_state == LanguageServerState.Stopped)
                return;
            connection = Stop();
        }
        OnStateChanged();
        connection?.Abandon();
    }

    /// <summary>
    /// A document's text changed, on the thread that changed it: sends the change to the running server.
    /// </summary>
    internal void Changed(
        ServerDocument document,
        TextSnapshot snapshot,
        IReadOnlyList<TextDocumentContentChangeEvent> changes
    )
    {
        lock (_lock)
            _connection?.Change(document, snapshot, changes);
    }

    /// <summary>Moves a document to a new path, before it takes it, on the UI thread.</summary>
    /// <exception cref="InvalidOperationException">Another open document of this server has <paramref name="path"/>.</exception>
    internal void Move(ServerDocument document, string? path)
    {
        string uri = DocumentUris.Of(path);
        lock (_lock)
        {
            // A closed document, or one whose server stopped, is no longer the server's.
            if (!_documents.Contains(document))
                return;
            ThrowIfOpen(uri, path, document);
            document.Uri = uri;
            _connection?.Move(document);
        }
    }

    /// <summary>Closes a document in the server, on the thread that disposes it.</summary>
    internal void Close(ServerDocument document)
    {
        lock (_lock)
        {
            if (_documents.Remove(document))
                _connection?.Close(document);
        }
    }

    /// <summary>
    /// How long <paramref name="connection"/> may take to answer a request it is about to send. A server that has run
    /// long enough is stable, and its earlier hangs no longer count.
    /// </summary>
    internal TimeSpan HangLimit(ServerConnection connection)
    {
        lock (_lock)
        {
            if (connection.RunningFor >= s_stableAfter)
                (_failures, _hangs) = (0, 0);
            TimeSpan limit = s_firstHangLimit;
            for (int i = 0; i < _hangs; i++)
                limit = Min(limit * 2, s_maxHangLimit);
            // No start or request waits longer than the longest limit.
            return connection.IsRunning ? limit : Min(limit * 2, s_maxHangLimit);
        }
    }

    /// <summary>
    /// The server answered <c>initialize</c>, and the app's settings are computed: it runs, and gets its settings, then
    /// the documents.
    /// </summary>
    internal void OnInitialized(ServerConnection connection, JsonRpcConnection rpc, bool isFullSync, JsonNode? settings)
    {
        bool changed;
        lock (_lock)
        {
            if (_connection != connection)
                return;
            connection.OnRunning(rpc, isFullSync, settings, _documents);
            changed = SetState(LanguageServerState.Running);
        }
        if (changed)
            OnStateChanged();
    }

    /// <summary>
    /// The process ended, and nothing more arrives from it: unless the server stopped, a new one starts after a delay,
    /// or the server failed too often in a row.
    /// </summary>
    /// <param name="connection">The process.</param>
    /// <param name="hung">Whether it was killed because it did not answer.</param>
    /// <param name="ranFor">How long it ran after it answered <c>initialize</c>; null when it never answered.</param>
    internal void OnEnded(ServerConnection connection, bool hung, TimeSpan? ranFor)
    {
        bool changed;
        lock (_lock)
        {
            if (_connection != connection)
                return;
            if (!hung && ranFor >= s_stableAfter)
                (_failures, _hangs) = (0, 0);
            _failures++;
            if (hung)
                _hangs++;
            if (_failures >= MaxFailures)
            {
                _connection = null;
                changed = SetState(LanguageServerState.Failed);
            }
            else
            {
                var next = new ServerConnection(this, _options, _lock);
                _connection = next;
                _restartTimer = _options.TimeProvider.CreateTimer(
                    _ => Restart(next),
                    null,
                    RestartDelay(),
                    Timeout.InfiniteTimeSpan
                );
                changed = SetState(LanguageServerState.Restarting);
            }
        }
        if (changed)
            OnStateChanged();
    }

    /// <summary>The delay before a restart: it doubles with each failure in a row.</summary>
    private TimeSpan RestartDelay()
    {
        TimeSpan delay = s_firstRestartDelay;
        for (int i = 1; i < _failures; i++)
            delay = Min(delay * 2, s_maxRestartDelay);
        return delay;
    }

    /// <summary>Starts the next process, once its delay has passed.</summary>
    private void Restart(ServerConnection next)
    {
        bool changed;
        lock (_lock)
        {
            if (_connection != next || _state != LanguageServerState.Restarting)
                return;
            _restartTimer?.Dispose();
            _restartTimer = null;
            next.Start();
            changed = SetState(LanguageServerState.Starting);
        }
        if (changed)
            OnStateChanged();
    }

    /// <summary>Guarded: starts a new process.</summary>
    /// <returns>Whether the state changed.</returns>
    [MemberNotNull(nameof(_connection))]
    private bool StartConnection()
    {
        _connection = new ServerConnection(this, _options, _lock);
        _connection.Start();
        return SetState(LanguageServerState.Starting);
    }

    /// <summary>Guarded: stops the server for good.</summary>
    /// <returns>The process to end, if there is one.</returns>
    private ServerConnection? Stop()
    {
        SetState(LanguageServerState.Stopped);
        _restartTimer?.Dispose();
        _restartTimer = null;
        _documents.Clear();
        ServerConnection? connection = _connection;
        _connection = null;
        return connection;
    }

    /// <summary>Guarded.</summary>
    /// <returns>Whether the state changed.</returns>
    private bool SetState(LanguageServerState state)
    {
        if (_state == state)
            return false;
        _state = state;
        return true;
    }

    /// <summary>
    /// Raises <see cref="PropertyChanged"/> for <see cref="State"/> on the app's main UI thread, or here before Avalonia
    /// is set up. Called outside the lock; handlers read the state that is current when they run.
    /// </summary>
    private void OnStateChanged()
    {
        // Application.Current is null before setup, so reading it ties no dispatcher to this thread.
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(() => PropertyChanged?.Invoke(this, s_stateChanged));
        else
            PropertyChanged?.Invoke(this, s_stateChanged);
    }

    /// <summary>Guarded.</summary>
    private void ThrowIfOpen(string uri, string? path, ServerDocument document)
    {
        string identity = DocumentUris.Identity(uri);
        if (_documents.Any(open => open != document && DocumentUris.Identity(open.Uri) == identity))
            throw new InvalidOperationException(
                $"Another open document of this language server has the path '{path}'."
            );
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
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
    /// The app's settings for the server: gets the section, such as <c>yaml</c>, and the file or folder it is for, and
    /// returns the settings, or null when the app has none. The client sends all settings, the answer for a null
    /// section, when the server starts and on <see cref="LanguageServer.NotifySettingsChanged"/>; servers that ask for
    /// settings get the answers per section. Called on any thread.
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
