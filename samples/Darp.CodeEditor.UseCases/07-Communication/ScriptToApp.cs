using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.Communication;

// Scenario: scripts call the app's side of the bridge, in three ways.
//
// - A call: the script asks the app and waits for the answer, as long as the app needs. The app may show the question
//   to the user; there is no time limit. Cancelling stops the run, and with it the wait for the answer.
// - A notification: the script tells the app whenever it has news. Subscribing happens in the app: whatever listens to
//   AppSide.Notified gets it, marked with the run that sent it. Nothing has to be registered in the runner.
// - Fast data: SendAsync queues values in the runner, and they reach the app in batches, in order. While a batch is on
//   its way, new values wait for the next one, so batches grow with the load and a lone value goes out at once. When
//   the run has ended, all its values have reached the app. A full queue makes the script wait: no value is lost.

/// <summary>Scripts call the app.</summary>
public sealed class ScriptToApp
{
    private const string Asks = """
        // Asks the app, and waits as long as the app needs.
        string answer = await AskAsync("Continue?");
        await NotifyAsync($"The answer was {answer}.");
        """;

    private const string Ticks = """
        // Notifies the app once a second, until the app cancels the run.
        for (int i = 0; ; i++)
        {
            await NotifyAsync($"tick {i}");
            await Task.Delay(1000, Stop);
        }
        """;

    private const string Streams = """
        // Sends a million values as fast as the queue allows.
        for (int i = 0; i < 1_000_000; i++)
            await SendAsync(Math.Sin(i / 100.0));
        """;

    private readonly CSharpScriptServer _scripts;

    /// <summary>Compiles with <paramref name="scripts"/>.</summary>
    public ScriptToApp(CSharpScriptServer scripts) => _scripts = scripts;

    /// <summary>
    /// Runs the three scripts one after another in one session: questions go to <paramref name="answer"/>,
    /// notifications to <paramref name="onMessage"/>, values to <paramref name="onValues"/>, each with the run's id.
    /// </summary>
    public async Task RunAsync(
        Func<string, CancellationToken, Task<string>> answer,
        Action<string, string> onMessage,
        Action<string, double[]> onValues,
        CancellationToken cancellationToken
    )
    {
        var appSide = new AppSide(answer);
        appSide.Notified += onMessage;
        appSide.Received += onValues;
        await using CSharpScriptSession<ISessionSide, Session> session = await Scripting.StartSessionAsync(
            appSide,
            cancellationToken
        );

        // A call.
        await RunToEndAsync(session, Asks, "asking", cancellationToken);

        // Notifications, until the app stops the run after three seconds. RunToEndAsync reports how it ended.
        CSharpScriptRun ticking = await session.StartScriptAsync(
            await CompileAsync(Ticks, cancellationToken),
            new RunInput("ticking", "none"),
            cancellationToken
        );
        using var threeSeconds = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        threeSeconds.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await ticking.RunToEndAsync(threeSeconds.Token, killSessionAfter: TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Stopped after three seconds, as planned.
        }

        // Fast data. Once the run has ended, all its values have reached the app.
        await RunToEndAsync(session, Streams, "streaming", cancellationToken);
    }

    private async Task RunToEndAsync(
        CSharpScriptSession<ISessionSide, Session> session,
        string code,
        string runId,
        CancellationToken cancellationToken
    )
    {
        CSharpScriptRun run = await session.StartScriptAsync(
            await CompileAsync(code, cancellationToken),
            new RunInput(runId, "none"),
            cancellationToken
        );
        await run.RunToEndAsync(cancellationToken, killSessionAfter: TimeSpan.FromSeconds(2));
    }

    private Task<CSharpScriptAssembly<ScriptGlobals>> CompileAsync(string code, CancellationToken cancellationToken) =>
        ScriptRuns.CompileAsync(_scripts, ScriptEnvironments.Default, code, cancellationToken);
}
