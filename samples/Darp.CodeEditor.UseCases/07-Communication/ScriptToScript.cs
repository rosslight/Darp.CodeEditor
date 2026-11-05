using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.Communication;

// Scenario: two scripts of different kinds run at the same time in one session and talk through the session object.
// An ordinary script writes messages; a reader script reads them and sends their lengths to the app. The messages
// never leave the runner.
//
// - The two kinds see different globals, ScriptGlobals and ReaderGlobals, over the same session object. Reader runs
//   take no input.
// - Scripts of one session share a process: when one has to be killed, the other ends with it. Scripts in different
//   sessions talk through the app instead.
// - The app keeps both runs: when one fails early, it stops the other and reports the failure.
//
// Questions:
// - Do scripts of different kinds need to share a session, or is one globals type for everything enough?

/// <summary>Two scripts talk to each other.</summary>
public sealed class ScriptToScript
{
    private const string Writer = """
        // Writes a message every 100 ms, until the run is cancelled.
        for (int i = 0; ; i++)
        {
            await Messages.Writer.WriteAsync($"message {i}", Stop);
            await Task.Delay(100, Stop);
        }
        """;

    private const string Reader = """
        // Reads the other script's messages, until the run is cancelled.
        await foreach (string message in Messages.ReadAllAsync(Stop))
            await SendAsync(message.Length);
        """;

    private readonly CSharpScriptServer _scripts;

    /// <summary>Compiles with <paramref name="scripts"/>.</summary>
    public ScriptToScript(CSharpScriptServer scripts) => _scripts = scripts;

    /// <summary>
    /// Runs both scripts for <paramref name="duration"/>, or until one of them ends early, handing the reader's values
    /// to <paramref name="onValues"/>.
    /// </summary>
    public async Task RunAsync(
        Action<string, double[]> onValues,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        var appSide = new AppSide(ScriptRuns.NoAnswer);
        appSide.Received += onValues;
        await using CSharpScriptSession<ISessionSide, Session> session = await Scripting.StartSessionAsync(
            appSide,
            cancellationToken
        );

        CSharpScriptAssembly<Session, RunInput> writerScript = await ScriptRuns.CompileAsync(
            _scripts,
            ScriptEnvironments.Default,
            Writer,
            cancellationToken
        );
        CSharpScriptAssembly<Session, ValueTuple> readerScript = await ScriptRuns.CompileAsync(
            _scripts,
            ScriptEnvironments.Reader,
            Reader,
            cancellationToken
        );
        CSharpScriptRun writer = await session.StartScriptAsync(
            writerScript,
            new RunInput("writer", "none"),
            cancellationToken
        );
        CSharpScriptRun reader = await session.StartScriptAsync(readerScript, cancellationToken);

        // Until the duration has passed, or one of them has ended early, for example because it failed.
        await Task.WhenAny(writer.Completion, reader.Completion, Task.Delay(duration, cancellationToken));
        await Task.WhenAll(writer.StopAsync(), reader.StopAsync());
        // Observe both outcomes; a failure of either throws here.
        await Task.WhenAll(ScriptRuns.ObserveStoppedAsync(writer), ScriptRuns.ObserveStoppedAsync(reader));
        cancellationToken.ThrowIfCancellationRequested();
    }
}
