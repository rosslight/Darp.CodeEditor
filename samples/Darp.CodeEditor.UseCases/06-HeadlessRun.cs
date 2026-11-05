using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.Headless;

// Scenario: the app runs the script the user saved last time, at startup or on a schedule, with no editor open and
// nobody watching. The server compiles the file's text in the same environment its editors use; the runner runs it.
// The app's Stop button cancels it.
//
// - Compiling needs the C# server, so the first run after app start waits for Roslyn to start (under a second with
//   ReadyToRun in the prototype).
// - Each saved script gets a session of its own, set up through the bridge before the script starts. Its AppSide
//   answers every question with "yes", since nobody is there to ask, and logs the values.
// - Stop gives the script two seconds, then kills the session. Disposing the session afterwards is safe either way.
// - Every failure ends up in the log, also a session that does not start.
// - The runner package depends on the C# package, which depends on the editor package and Avalonia. A console tool can
//   reference them without starting an Avalonia app; a package without Avalonia comes when an app needs one.
// - Each run compiles again. Storing a compiled script, so that later runs skip the compiler, comes after v1.

/// <summary>Runs saved scripts with nobody watching.</summary>
public sealed class SavedScripts
{
    private readonly CSharpScriptServer _scripts;
    private readonly ILogger _log;

    /// <summary>Compiles with <paramref name="scripts"/> and logs to <paramref name="log"/>.</summary>
    public SavedScripts(CSharpScriptServer scripts, ILogger log)
    {
        _scripts = scripts;
        _log = log;
    }

    /// <summary>Compiles and runs the script in <paramref name="path"/>; <paramref name="stop"/> stops it.</summary>
    /// <returns>Whether the script compiled and ran to its end.</returns>
    public async Task<bool> RunAsync(string path, CancellationToken stop)
    {
        string code = await File.ReadAllTextAsync(path, stop);
        CSharpScriptCompilation<ScriptGlobals> compilation = await _scripts.CompileAsync(
            ScriptEnvironments.Default,
            code,
            stop
        );
        if (!compilation.Succeeded)
        {
            foreach (Diagnostic error in compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                _log.LogError(
                    "{Path}({Line},{Column}): {Message}",
                    path,
                    error.Start.Line,
                    error.Start.Column,
                    error.Message
                );
            return false;
        }

        string runId = Path.GetFileNameWithoutExtension(path);
        var appSide = new AppSide((question, _) => Task.FromResult("yes"));
        appSide.Received += (id, values) => _log.LogInformation("{RunId}: {Count} values", id, values.Length);
        CSharpScriptSession<ISessionSide, Session>? session = null;
        try
        {
            session = await Scripting.StartSessionAsync(appSide, stop);
            await session.Remote.InitializeAsync(runId, stop);
            CSharpScriptRun run = await session.StartScriptAsync(
                compilation.Script,
                new RunInput(runId, "bench"),
                stop
            );
            await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            _log.LogInformation("{RunId}: stopped", runId);
            return false;
        }
        catch (CSharpScriptException exception)
        {
            // From the script, its globals, or the session object while the session starts.
            _log.LogError("{RunId}: {Type}: {Message}", runId, exception.ScriptExceptionType, exception.Message);
            return false;
        }
        catch (ConnectionLostException) when (session is { } ended)
        {
            CSharpScriptSessionExit exit = await ended.Exited;
            _log.LogError(
                "{RunId}: the session ended ({Reason})\n{StandardError}",
                runId,
                exit.Reason,
                exit.StandardError
            );
            return false;
        }
        catch (InvalidOperationException exception) when (session is null)
        {
            // The runner did not start or answer; the message has its standard error.
            _log.LogError(exception, "{RunId}: the session did not start", runId);
            return false;
        }
        finally
        {
            if (session is not null)
                await session.DisposeAsync();
        }
    }
}
