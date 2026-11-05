using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.Communication;

// Scenario: the app calls the session side of the bridge, in three ways. The session object answers; scripts only
// tell it what to do.
//
// - A call: the session object answers by itself, before any script has run. Setup, such as InitializeAsync, is a
//   call like any other.
// - A callback, with two lifetimes. A calculation that a script sets belongs to the session: the script ends, and the
//   app calls the calculation afterwards. A trigger that a script registers belongs to its run: the script waits
//   until the app cancels it, and the trigger is gone when the run has ended.
// - Fast data, such as thousands of values per second that the app itself receives: one call carries a batch. A call
//   per value cost about 30 µs in the prototype, so about 30,000 values per second at best; batches of 1,024 reached
//   about 1.5 million. Most fast data never crosses at all: drivers in the runner read their devices there.
//
// Readiness belongs to the script API: the trigger script notifies the app once it has registered, and the app waits
// for that before it triggers.
//
// Questions:
// - Should the library batch single values behind the scenes, or is a batch method in the script API enough?

/// <summary>The app calls a session.</summary>
public sealed class AppToScript
{
    private const string SetsCalculation = """
        // Sets a calculation, then ends: the calculation belongs to the session and stays.
        SetCalculation(x => x * x);
        """;

    private const string AnswersTrigger = """
        // Answers the "reset" trigger until the app cancels this run.
        int resets = 0;
        OnTrigger("reset", async cancellationToken => await NotifyAsync($"reset {++resets}"));
        await NotifyAsync("ready");
        await WaitUntilStoppedAsync();
        """;

    private readonly CSharpScriptServer _scripts;

    /// <summary>Compiles with <paramref name="scripts"/>.</summary>
    public AppToScript(CSharpScriptServer scripts) => _scripts = scripts;

    /// <summary>Makes a call, both kinds of callbacks and a batch, and reports each result to <paramref name="log"/>.</summary>
    public async Task RunAsync(Action<string> log, CancellationToken cancellationToken)
    {
        var appSide = new AppSide(ScriptRuns.NoAnswer);
        var triggerReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        appSide.Notified += (runId, message) =>
        {
            log($"{runId}: {message}");
            if (runId == "trigger" && message == "ready")
                triggerReady.TrySetResult();
        };
        await using CSharpScriptSession<ISessionSide, Session> session = await Scripting.StartSessionAsync(
            appSide,
            cancellationToken
        );

        // A call, answered by the session object.
        await session.Remote.InitializeAsync("demo", cancellationToken);
        log(await session.Remote.GetStatusAsync(cancellationToken));

        // A callback that the session owns: set by a run that has already ended.
        CSharpScriptRun setting = await session.StartScriptAsync(
            await CompileAsync(SetsCalculation, cancellationToken),
            new RunInput("calculation", "none"),
            cancellationToken
        );
        await setting.Completion;
        log($"3 -> {await session.Remote.CalculateAsync(3, cancellationToken)}");

        // A callback that the run owns: there while the run lasts.
        CSharpScriptRun triggering = await session.StartScriptAsync(
            await CompileAsync(AnswersTrigger, cancellationToken),
            new RunInput("trigger", "none"),
            cancellationToken
        );
        if (await Task.WhenAny(triggerReady.Task, triggering.Completion) == triggering.Completion)
        {
            await triggering.Completion;
            throw new InvalidOperationException("The trigger script ended before it was ready.");
        }
        await session.Remote.TriggerAsync("reset", cancellationToken);
        await triggering.StopAsync();
        await ScriptRuns.ObserveStoppedAsync(triggering);
        log(await session.Remote.GetStatusAsync(cancellationToken)); // the trigger is gone

        // Fast data: many inputs in one call.
        double[] inputs = [.. Enumerable.Range(0, 1024).Select(i => (double)i)];
        double[] results = await session.Remote.CalculateManyAsync(inputs, cancellationToken);
        log($"{results.Length} results, the last {results[^1]}");
    }

    private Task<CSharpScriptAssembly<Session, RunInput>> CompileAsync(
        string code,
        CancellationToken cancellationToken
    ) => ScriptRuns.CompileAsync(_scripts, ScriptEnvironments.Default, code, cancellationToken);
}
