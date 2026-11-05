using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UseCases.ScriptApi;

namespace UseCases.Startup;

// Scenario: an app with two kinds of scripts sets up scripting once, at startup, in its service container.
//
// - One C# server for the whole app: all script kinds and all editors share its Roslyn process.
// - One environment per kind of script, typed by the kind's globals. The session object says which kinds run in it
//   and what input each takes, so the compiler rejects a script started in the wrong session or with the wrong
//   input.
// - Sessions are started where scripts run, such as a page: each is a runner process of its own, with its own
//   AppSide. A script that has to be killed takes only its own session down.
// - The server takes no logger: it hands its process's standard error to the app, which logs it as it likes.
// - The registration is the app's own two lines; the library has no extension for service containers.
// - The container disposes the server on exit. `await provider.DisposeAsync()` shuts it down properly; a synchronous
//   `Dispose()`, as in Avalonia's exit handlers, kills its process at once.
// - A minimal session takes 80 to 110 ms to answer its first call. An app that wants no wait starts its session
//   ahead of time, for example when a page opens, and only starts scripts later.

/// <summary>The two kinds of scripts of the app.</summary>
public static class ScriptEnvironments
{
    /// <summary>Ordinary scripts: they see everything the session offers, and each run gets a RunInput.</summary>
    public static CSharpScriptEnvironment<ScriptGlobals> Default { get; } = new() { Imports = ["UseCases.ScriptApi"] };

    /// <summary>Reader scripts: they only read the session's messages and send values on. Their runs take no input.</summary>
    public static CSharpScriptEnvironment<ReaderGlobals> Reader { get; } = new() { CheckOverflow = true };
}

/// <summary>Registers scripting, and starts and stops what runs it.</summary>
public static class Scripting
{
    /// <summary>Adds the C# server as a singleton.</summary>
    public static IServiceCollection AddScripting(this IServiceCollection services) =>
        services.AddSingleton(provider =>
        {
            ILogger? log = provider.GetService<ILoggerFactory>()?.CreateLogger("CSharpScriptServer");
            return new CSharpScriptServer(
                new CSharpScriptServerOptions { StandardError = line => log?.LogWarning("{Line}", line) }
            );
        });

    /// <summary>
    /// Called once the main window shows: starts Roslyn before the user opens a script. Optional: the first script
    /// document starts it otherwise. The C# grammar needs no warm-up; it starts loading with the first document.
    /// </summary>
    public static Task WarmUpAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<CSharpScriptServer>().StartAsync(cancellationToken);

    /// <summary>Starts a session of the use cases' script API, with <paramref name="appSide"/>.</summary>
    public static Task<CSharpScriptSession<ISessionSide, Session>> StartSessionAsync(
        AppSide appSide,
        CancellationToken cancellationToken
    ) => CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(appSide, cancellationToken);
}

/// <summary>
/// The app's half of the bridge: it answers the scripts' questions and hands their notifications and values on to the
/// app, marked with the run that sent them. One per session; calls arrive on any thread.
/// </summary>
public sealed class AppSide : IAppSide
{
    private readonly Func<string, CancellationToken, Task<string>> _answer;

    /// <summary>Answers the scripts' questions with <paramref name="answer"/>.</summary>
    public AppSide(Func<string, CancellationToken, Task<string>> answer) => _answer = answer;

    /// <summary>Raised on any thread for each notification: the run's id, and the message.</summary>
    public event Action<string, string>? Notified;

    /// <summary>Raised on any thread for each batch of values: the run's id, and the values in order.</summary>
    public event Action<string, double[]>? Received;

    /// <inheritdoc />
    public Task<string> AskAsync(string question, CancellationToken cancellationToken) =>
        _answer(question, cancellationToken);

    /// <inheritdoc />
    public Task NotifyAsync(string runId, string message, CancellationToken cancellationToken)
    {
        Notified?.Invoke(runId, message);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ReceiveAsync(string runId, double[] values, CancellationToken cancellationToken)
    {
        Received?.Invoke(runId, values);
        return Task.CompletedTask;
    }
}
