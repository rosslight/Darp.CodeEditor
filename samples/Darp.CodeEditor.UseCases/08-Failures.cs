using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;

namespace UseCases.Failures;

// Scenario: scripts fail in every way: they throw, loop forever or crash the process. The app reports each failure
// and keeps working; the next run starts a fresh runner process.
//
// - An exception in the script reaches the app as ScriptException, with the script's stack trace.
// - A call that takes longer than CallTimeout is cancelled; if it does not stop, the process is killed. Every
//   handler of that runner is gone then, and their calls throw ScriptRunnerExitedException.
// - Exited tells the app when handlers died, so it can rerun the scripts it depends on.
//
// Questions:
// - Should the runner rerun scripts on its own after a crash, or is that always the app's decision?
// - Is one CallTimeout per runner enough, or do some handler methods need their own?

/// <summary>Calls a packet handler and reports what went wrong.</summary>
public sealed class GuardedHandler
{
    private readonly Action<string> _report;
    private IPacketHandler? _handler;

    /// <summary>Watches <paramref name="runner"/> and reports to <paramref name="report"/>.</summary>
    public GuardedHandler(CSharpScriptRunner runner, IPacketHandler handler, Action<string> report)
    {
        _handler = handler;
        _report = report;
        runner.Exited += (_, e) =>
        {
            _handler = null;
            _report($"The script runner ended ({e.Reason}); run the script again.\n{e.StandardError}");
        };
    }

    /// <summary>Decodes a packet, or returns null and reports why it could not.</summary>
    public async Task<Reading?> TryHandleAsync(Packet packet, CancellationToken cancellationToken)
    {
        if (_handler is null)
            return null;
        try
        {
            return await _handler.HandleAsync(packet, cancellationToken);
        }
        catch (ScriptException exception)
        {
            _report(
                $"The script failed: {exception.ScriptExceptionType}: {exception.Message}\n{exception.ScriptStackTrace}"
            );
        }
        catch (ScriptRunnerExitedException exception) when (exception.Reason == ScriptRunnerExitReason.CallTimedOut)
        {
            _report("The script took too long and was stopped.");
        }
        catch (ScriptRunnerExitedException exception)
        {
            _report($"The script runner ended: {exception.Reason}.");
        }
        return null;
    }
}
