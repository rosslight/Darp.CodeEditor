using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using StreamJsonRpc;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.ScriptEditor;

// Scenario: a page where the user edits a script and presses Run (F5). Run compiles exactly the text the editor shows
// and runs it in the page's session; Stop cancels it. Once the run has ended, the page calls the calculation it set.
// The error list shows the document's diagnostics; clicking one moves the caret there (see the view).
//
// - The document lives in the view model: leaving the page and coming back keeps the text and its undo history.
// - Run does not wait for the document's diagnostics: the compilation has its own, for exactly the version it
//   compiled, and Run prints those.
// - The page starts its session with the first Run and keeps it. A calculation a run sets belongs to the session, so
//   it is still there after the run; anything the run owned ended with it.
// - Each run gets its own input, so the output shows which run wrote it.
// - Stop gives a script two seconds, then kills the session. RunToEndAsync reports the kill, and the next Run starts a
//   new session: sessions don't restart by themselves. Closing the page ends a Run in progress before it disposes the
//   session.
// - A failure says where it happened in the current text, also one in the calculation that an earlier run left.

/// <summary>The script editor page.</summary>
public sealed partial class ScriptEditorViewModel : ObservableObject, IAsyncDisposable
{
    private const string DefaultScript = """
        // Sets a calculation that the page calls with 21 once this run has ended. It belongs to the session.
        SetCalculation(x => x * 2);
        await NotifyAsync("Calculation set.");
        """;

    private readonly CSharpScriptServer _scripts;
    private readonly AppSide _appSide;
    private readonly CancellationTokenSource _lifetime = new();
    private CSharpScriptSession<ISessionSide, Session>? _session;
    private int _runCount;

    /// <summary>Creates the page with the default script.</summary>
    public ScriptEditorViewModel(CSharpScriptServer scripts)
    {
        _scripts = scripts;
        _appSide = new AppSide((question, _) => Task.FromResult("yes"));
        _appSide.Notified += (runId, message) => Dispatcher.UIThread.Post(() => WriteLine($"{runId}: {message}"));
        Script = scripts.CreateDocument(ScriptEnvironments.Default, DefaultScript);
    }

    /// <summary>The script the editor shows: a document of the default kind of script.</summary>
    public CSharpScriptDocument<ScriptGlobals> Script { get; }

    /// <summary>What runs printed, for the output panel.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = "";

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await RunScriptAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            WriteLine("Stopped.");
        }
        catch (CSharpScriptException exception)
        {
            WriteLine($"The script failed{Where(exception)}: {exception.ScriptExceptionType}: {exception.Message}");
        }
        catch (ConnectionLostException)
        {
            if (_session is { } session)
            {
                CSharpScriptSessionExit exit = await session.Exited;
                WriteLine($"The session ended ({exit.Reason}); the next Run starts a new one.");
            }
            await EndSessionAsync();
        }
    }

    private async Task RunScriptAsync(CancellationToken stop)
    {
        CSharpScriptCompilation<ScriptGlobals> compilation = await Script.CompileAsync(stop);
        if (!compilation.Succeeded)
        {
            foreach (Diagnostic error in compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                WriteLine($"({error.Start.Line},{error.Start.Column}): {error.Code} {error.Message}");
            return;
        }

        if (_session is { Exited.IsCompleted: true })
            await EndSessionAsync();
        _session ??= await Scripting.StartSessionAsync(_appSide, stop);
        CSharpScriptSession<ISessionSide, Session> session = _session;
        var input = new RunInput($"run {++_runCount}", "editor");
        CSharpScriptRun run = await session.StartScriptAsync(compilation.Script, input, stop);
        await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
        WriteLine($"21 -> {await session.Remote.CalculateAsync(21, stop)}");
    }

    private async Task EndSessionAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }
    }

    // The failure's position refers to the text the script was compiled from; the user may have typed since.
    private string Where(CSharpScriptException exception)
    {
        if (exception is not { ScriptSnapshot: { } compiled, ScriptPosition: { } position })
            return "";
        TextRange range = Script.MapToCurrent(compiled, new TextRange(compiled.GetOffset(position), 0));
        TextPosition now = Script.CurrentSnapshot.GetPosition(range.Start);
        return $" at ({now.Line},{now.Column})";
    }

    private void WriteLine(string line) => Output += line + "\n";

    /// <summary>Ends a Run in progress, then the session and the document.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        if (RunCommand.ExecutionTask is { } running)
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await EndSessionAsync();
        Script.Dispose();
        _lifetime.Dispose();
    }
}
