using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>A script open in the server. Used only on the connection's read loop.</summary>
/// <param name="uri">The script's URI.</param>
/// <param name="context">The environment it is open in.</param>
/// <param name="snapshot">Its first version.</param>
internal sealed class OpenScript(string uri, ScriptContext context, ScriptSnapshot snapshot)
{
    private CancellationTokenSource _diagnostics = new();

    public string Uri { get; } = uri;

    /// <summary>The environment it is open in.</summary>
    public ScriptContext Context { get; } = context;

    /// <summary>The latest version.</summary>
    public ScriptSnapshot Snapshot { get; set; } = snapshot;

    /// <summary>The completion list answered last, which the client resolves items of.</summary>
    public ScriptCompletions? Completions { get; private set; }

    /// <summary>Starts a completion request, which from now on is the list that items resolve in.</summary>
    public ScriptCompletions Complete(CompletionParams parameters, int id, CancellationToken cancellationToken) =>
        Completions = new ScriptCompletions(id, Snapshot, parameters, cancellationToken);

    /// <summary>Cancels the diagnostics under way and returns the token of the next ones.</summary>
    public CancellationToken RestartDiagnostics()
    {
        _diagnostics.Cancel();
        _diagnostics = new CancellationTokenSource();
        return _diagnostics.Token;
    }

    /// <summary>The script closed: its diagnostics are no longer wanted.</summary>
    public void StopDiagnostics() => _diagnostics.Cancel();
}
