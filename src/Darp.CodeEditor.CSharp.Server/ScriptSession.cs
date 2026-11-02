using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using EmmyLua.LanguageServer.Framework.Protocol.JsonRpc;
using EmmyLua.LanguageServer.Framework.Protocol.Message.Initialize;
using EmmyLua.LanguageServer.Framework.Protocol.Model;
using EmmyLua.LanguageServer.Framework.Protocol.Model.TextDocument;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using LanguageServer = EmmyLua.LanguageServer.Framework.Server.LanguageServer;
using LspDiagnostic = EmmyLua.LanguageServer.Framework.Protocol.Model.Diagnostic.Diagnostic;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>The server's state: the script workspace, the open documents and the diagnostics of each version.</summary>
/// <remarks>
/// Document state changes only on the thread that reads messages (see <see cref="InlineScheduler"/>). Background
/// work receives immutable snapshots.
/// </remarks>
internal sealed class ScriptSession : IDisposable
{
    private readonly LanguageServer _server;
    private readonly ServerOptions _options;
    private readonly Dictionary<string, OpenDocument> _documents = new(StringComparer.Ordinal);
    private Task<ScriptWorkspace>? _workspace;

    public ScriptSession(LanguageServer server, ServerOptions options)
    {
        _server = server;
        _options = options;
    }

    public ClientFeatures Client { get; private set; } = ClientFeatures.From(new());

    public CompletionResults Completions { get; } = new();

    /// <summary>Completes once the environment is loaded. Faults when it cannot be loaded.</summary>
    public Task<ScriptWorkspace> Workspace =>
        _workspace ?? throw new InvalidOperationException("The server is not initialized.");

    /// <exception cref="FormatException">The initialization options are invalid.</exception>
    /// <exception cref="ArgumentException">The environment names files or values that do not exist.</exception>
    public void Initialize(InitializeParams request)
    {
        Client = ClientFeatures.From(request.Capabilities);
        CSharpScriptEnvironment environment =
            (
                request.InitializationOptions is { } options
                    ? CSharpScriptInitializationOptions.Read(options.RootElement)
                    : null
            ) ?? new CSharpScriptEnvironment();
        // Resolving only reads file headers; errors here answer the initialize request.
        ResolvedScriptEnvironment resolved = ResolvedScriptEnvironment.Resolve(environment);
        _workspace = Task.Run(() => ScriptWorkspace.Load(resolved));
        Observe(_workspace, "Loading the script environment");
        if (_options.WarmUp)
            Observe(WarmUp.RunAsync(_workspace, Client), "Warm-up");
    }

    public void Open(DocumentUri uri, int version, string text)
    {
        string key = uri.Uri.AbsoluteUri;
        if (_documents.Remove(key, out OpenDocument? previous))
            previous.Close();
        var document = new OpenDocument(key, uri.Uri.OriginalString, version, SourceText.From(text));
        _documents.Add(key, document);
        ScheduleDiagnostics(document.Current);
    }

    public void Change(DocumentUri uri, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes)
    {
        if (!_documents.TryGetValue(uri.Uri.AbsoluteUri, out OpenDocument? document))
            return;
        DocumentSnapshot current = document.Current;
        SourceText text = current.Text;
        LineMap lines = current.Lines;
        for (int i = 0; i < changes.Count; i++)
        {
            TextDocumentContentChangeEvent change = changes[i];
            text = change.Range is { } range
                ? text.WithChanges(new TextChange(lines.GetSpan(range), change.Text))
                : SourceText.From(change.Text);
            if (i < changes.Count - 1)
                lines = LineMap.Create(text);
        }
        ScheduleDiagnostics(document.Update(version, text));
    }

    public void Close(DocumentUri uri)
    {
        if (!_documents.Remove(uri.Uri.AbsoluteUri, out OpenDocument? document))
            return;
        document.Close();
        Completions.Remove(document.Key);
        // Clients keep showing published diagnostics until they are replaced.
        Observe(PublishAsync(document.ClientUri, null, []), "Clearing diagnostics");
    }

    public bool TryGetSnapshot(DocumentUri uri, [NotNullWhen(true)] out DocumentSnapshot? snapshot)
    {
        snapshot = _documents.TryGetValue(uri.Uri.AbsoluteUri, out OpenDocument? document) ? document.Current : null;
        return snapshot is not null;
    }

    /// <summary>Stops pending diagnostics; the server answers no more requests.</summary>
    public void Shutdown()
    {
        foreach (OpenDocument document in _documents.Values)
            document.Close();
    }

    public void Dispose()
    {
        if (_workspace is { IsCompletedSuccessfully: true } workspace)
            workspace.Result.Dispose();
    }

    /// <summary>Logs failures of work nobody awaits.</summary>
    public static void Observe(Task task, string what) =>
        task.ContinueWith(
            static (t, state) => Console.Error.WriteLine($"{state} failed: {t.Exception?.GetBaseException()}"),
            what,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

    /// <summary>Publishes compiler diagnostics for <paramref name="snapshot"/> once the text stayed unchanged for a while.</summary>
    private void ScheduleDiagnostics(DocumentSnapshot snapshot) =>
        Observe(PublishDiagnosticsAsync(snapshot), "Diagnostics");

    private async Task PublishDiagnosticsAsync(DocumentSnapshot snapshot)
    {
        CancellationToken changed = snapshot.Changed;
        try
        {
            await Task.Delay(_options.DiagnosticsDelay, changed).ConfigureAwait(false);
            ScriptWorkspace workspace = await Workspace.ConfigureAwait(false);
            Document document = snapshot.Owner.GetDocument(workspace, snapshot);
            ImmutableArray<Diagnostic> diagnostics = await ScriptDiagnostics
                .GetAsync(document, changed)
                .ConfigureAwait(false);
            changed.ThrowIfCancellationRequested();
            await PublishAsync(
                    snapshot.Owner.ClientUri,
                    snapshot.Version,
                    ScriptDiagnostics.ToLsp(diagnostics, snapshot.Lines)
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (changed.IsCancellationRequested)
        {
            // A newer version or closing the document made these diagnostics obsolete.
        }
    }

    /// <remarks>
    /// Written with the client's URI string: the framework's <see cref="DocumentUri"/> writes
    /// <see cref="Uri.ToString"/>, which unescapes, so URIs such as <c>file:///c%3A/a.csx</c> would not round-trip.
    /// </remarks>
    private Task PublishAsync(string clientUri, int? version, List<LspDiagnostic> diagnostics) =>
        _server.SendNotification(
            new NotificationMessage(
                "textDocument/publishDiagnostics",
                JsonSerializer.SerializeToDocument(
                    new PublishDiagnosticsNotification(clientUri, version, diagnostics),
                    _server.JsonSerializerOptions
                )
            )
        );
}
