using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>A script open in the server. Used only on the connection's read loop.</summary>
/// <param name="uri">The script's URI.</param>
/// <param name="context">The environment it is open in.</param>
/// <param name="snapshot">Its first version.</param>
internal sealed class OpenScript(string uri, ScriptContext context, ScriptSnapshot snapshot)
{
    /// <summary>
    /// How many answered lists the script keeps for resolving: the one that the client shows, and the one it asked for
    /// meanwhile. Each keeps its own version of the script, so every list kept costs memory and completion time.
    /// </summary>
    private const int MaxCompletions = 2;

    /// <summary>
    /// The lists whose items the client may still commit, oldest first: those under way, and the last
    /// <see cref="MaxCompletions"/> answered; none of them cancelled or failed.
    /// </summary>
    private readonly List<ScriptCompletions> _completions = [];

    private CancellationTokenSource _diagnostics = new();

    public string Uri { get; } = uri;

    /// <summary>The environment it is open in.</summary>
    public ScriptContext Context { get; } = context;

    /// <summary>The latest version.</summary>
    public ScriptSnapshot Snapshot { get; set; } = snapshot;

    /// <summary>
    /// Starts a completion request. Its list resolves items from now on, beside the latest lists before it that the
    /// client did not cancel: it shows one of them until the new answer comes, and may commit from it meanwhile. A
    /// list takes room among those kept only once it is answered, so that one that is cancelled or fails pushes none
    /// out.
    /// </summary>
    public ScriptCompletions Complete(CompletionParams parameters, int id, CancellationToken cancellationToken)
    {
        var list = new ScriptCompletions(id, Snapshot, parameters, cancellationToken);
        _completions.RemoveAll(kept => kept.IsDropped);
        List<ScriptCompletions> answered = [.. _completions.Where(kept => kept.IsAnswered)];
        foreach (ScriptCompletions oldest in answered.Take(answered.Count - MaxCompletions))
            _completions.Remove(oldest);
        _completions.Add(list);
        return list;
    }

    /// <summary>The list with <paramref name="id"/>, while the script keeps it; null otherwise.</summary>
    public ScriptCompletions? Completions(int id) => _completions.Find(list => list.Id == id);

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
