using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using StreamJsonRpc;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.Failures;

// Scenario: scripts fail in every way: they throw, ignore their stop token or crash the process. The app reports each
// failure and keeps working.
//
// - An exception ends the run: Completion throws ScriptException, with the script's stack trace. So does an
//   OperationCanceledException from any token other than the run's stop token, such as a timeout inside the script.
//   The session stays usable.
// - A script that ignores its stop token never ends. The app decides how long to wait, then kills the session: every
//   script in it ends.
// - A crash ends the session the same way. Runs and calls on Remote then fail with StreamJsonRpc's
//   ConnectionLostException; the session's Exited task says why, and the app starts a new session.
//
// Questions:
// - Should a session restart its process by itself after a crash, or is that always the app's decision?

/// <summary>Runs scripts in a session and reports what went wrong.</summary>
public sealed class GuardedRuns
{
    private readonly CSharpScriptSession<ISessionSide, Session> _session;
    private readonly Action<string> _report;

    /// <summary>Runs scripts in <paramref name="session"/> and reports to <paramref name="report"/>.</summary>
    public GuardedRuns(CSharpScriptSession<ISessionSide, Session> session, Action<string> report)
    {
        _session = session;
        _report = report;
    }

    /// <summary>
    /// Runs <paramref name="script"/> to its end. When <paramref name="stop"/> is cancelled, stops it, and kills the
    /// session if the script has not ended two seconds later.
    /// </summary>
    public async Task RunAsync(CompiledScript<Session, RunInput> script, RunInput input, CancellationToken stop)
    {
        try
        {
            ScriptRun run = await _session.StartScriptAsync(script, input, stop);
            await run.WaitAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _report("Stopped.");
        }
        catch (OperationCanceledException)
        {
            _report("The session ended the run: someone else disposed it.");
        }
        catch (ScriptException exception)
        {
            _report(
                $"The script failed: {exception.ScriptExceptionType}: {exception.Message}\n{exception.ScriptStackTrace}"
            );
        }
        catch (ConnectionLostException)
        {
            ScriptSessionExit exit = await _session.Exited;
            _report(
                $"The session ended ({exit.Reason}, exit code {exit.ExitCode}); start a new one.\n{exit.StandardError}"
            );
        }
    }
}
