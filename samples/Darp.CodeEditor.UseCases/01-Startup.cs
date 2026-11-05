using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UseCases.ScriptApi;

namespace UseCases.Startup;

// Scenario: an app with two kinds of scripts sets up scripting once, at startup, in its service container.
//
// - One C# server for the whole app: all script kinds and all editors share its Roslyn process.
// - One environment per kind of script. The server creates the scripts' documents in it, and compiles in it.
// - One runner for all scripts here, with the app's services for scripts. A hang in one script kills the process, and
//   with it the other scripts' handlers.
// - The container disposes the server and the runner on exit; that takes `await provider.DisposeAsync()`, since both
//   are only async-disposable.
//
// Questions:
// - Do some scripts need a runner of their own, so that a hang in one cannot stop the others?
// - Is one set of services per runner enough? A runner per device would give each device's scripts their own station.
// - Should the library offer this registration itself, as an `AddCSharpScripts(...)` extension?

/// <summary>The two kinds of scripts of the app.</summary>
public static class ScriptEnvironments
{
    /// <summary>Packet scripts: return a handler that decodes packets.</summary>
    public static CSharpScriptEnvironment Packets { get; } =
        new()
        {
            GlobalsType = typeof(PacketScriptGlobals),
            Imports = ["System", "System.Linq", "System.Threading", "System.Threading.Tasks", "UseCases.ScriptApi"],
        };

    /// <summary>Sequence scripts: run from start to end on a station.</summary>
    public static CSharpScriptEnvironment Sequences { get; } =
        new()
        {
            GlobalsType = typeof(SequenceScriptGlobals),
            Imports = ["System", "System.Threading", "System.Threading.Tasks", "UseCases.ScriptApi"],
            Nullable = true,
        };
}

/// <summary>Registers and warms up scripting.</summary>
public static class Scripting
{
    /// <summary>
    /// Adds the C# server and the runner as singletons. The app registers its <see cref="IStation"/> elsewhere.
    /// </summary>
    public static IServiceCollection AddScripting(this IServiceCollection services) =>
        services
            .AddSingleton(provider => new CSharpScriptServer(
                new CSharpScriptServerOptions
                {
                    Logger = provider.GetService<ILoggerFactory>()?.CreateLogger("CSharpScriptServer"),
                }
            ))
            .AddSingleton<ScriptOutput>()
            .AddSingleton(provider => new CSharpScriptRunner(
                new CSharpScriptRunnerOptions
                {
                    Services =
                    [
                        CSharpScriptService.Create<IOutputLog>(provider.GetRequiredService<ScriptOutput>()),
                        CSharpScriptService.Create(provider.GetRequiredService<IStation>()),
                    ],
                    CallTimeout = TimeSpan.FromSeconds(5),
                    Logger = provider.GetService<ILoggerFactory>()?.CreateLogger("CSharpScriptRunner"),
                }
            ));

    /// <summary>
    /// Called once the main window shows: starts Roslyn before the user opens a script. Optional: the first script
    /// document starts it otherwise. The C# grammar needs no warm-up; it starts loading with the first document.
    /// </summary>
    public static Task WarmUpAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<CSharpScriptServer>().LanguageServer.StartAsync(cancellationToken);
}

/// <summary>The output that scripts write, for every page that shows it.</summary>
public sealed class ScriptOutput : IOutputLog
{
    /// <summary>Raised on any thread for every line a script writes.</summary>
    public event Action<string>? Written;

    /// <inheritdoc />
    public Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        Written?.Invoke(message);
        return Task.CompletedTask;
    }
}
