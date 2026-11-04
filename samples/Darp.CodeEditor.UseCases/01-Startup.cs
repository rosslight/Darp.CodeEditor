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
// - One CodeLanguage per kind of script, created once from its environment.
// - One runner for all scripts here. A hang in one script kills the process, and with it the other scripts' handlers.
// - The container disposes the server and the runner on exit; that takes `await provider.DisposeAsync()`, since both
//   are only async-disposable.
//
// Questions:
// - Do some scripts need a runner of their own, so that a hang in one cannot stop the others?
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

/// <summary>The editor languages of the two kinds of scripts, created once.</summary>
public sealed class ScriptLanguages(CSharpScriptServer server)
{
    /// <summary>Documents of packet scripts.</summary>
    public CodeLanguage Packets { get; } = server.CreateLanguage(ScriptEnvironments.Packets);

    /// <summary>Documents of sequence scripts.</summary>
    public CodeLanguage Sequences { get; } = server.CreateLanguage(ScriptEnvironments.Sequences);
}

/// <summary>Registers and warms up scripting.</summary>
public static class Scripting
{
    /// <summary>Adds the C# server, the runner and the script languages as singletons.</summary>
    public static IServiceCollection AddScripting(this IServiceCollection services) =>
        services
            .AddSingleton(provider => new CSharpScriptServer(
                new CSharpScriptServerOptions
                {
                    Logger = provider.GetService<ILoggerFactory>()?.CreateLogger("CSharpScriptServer"),
                }
            ))
            .AddSingleton(provider => new CSharpScriptRunner(
                new CSharpScriptRunnerOptions
                {
                    CallTimeout = TimeSpan.FromSeconds(5),
                    Logger = provider.GetService<ILoggerFactory>()?.CreateLogger("CSharpScriptRunner"),
                }
            ))
            .AddSingleton<ScriptLanguages>();

    /// <summary>
    /// Called once the main window shows. Loads the C# grammar and theme off the UI thread, so that the first script
    /// editor is colored in its first frame, and starts Roslyn before the user opens a script. Both are optional:
    /// without them, the first editor loads what it needs itself.
    /// </summary>
    public static async Task WarmUpAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<ScriptLanguages>().Packets.PreloadAsync(cancellationToken);
        await CodeEditorTheme.DarkPlus.PreloadAsync(cancellationToken);
        await services.GetRequiredService<CSharpScriptServer>().LanguageServer.StartAsync(cancellationToken);
    }
}
