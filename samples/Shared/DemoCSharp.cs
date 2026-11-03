using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Samples;

/// <summary>The demos' C# scripts: their environment, and the C# language service for it.</summary>
internal static class DemoCSharp
{
    /// <summary>
    /// The globals, references and imports of the sample script. The globals assembly is a file next to the app, also
    /// in the NativeAOT demo.
    /// </summary>
    public static CSharpScriptEnvironment Environment { get; } =
        new()
        {
            Globals = new CSharpScriptGlobals(
                Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Demo.Globals.dll"),
                "Darp.CodeEditor.Demo.ScriptGlobals"
            ),
            FrameworkReferences = ["System.Linq"],
            Imports = ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"],
        };

    /// <summary>The service of the C# script language server, which the demo's targets put next to the app.</summary>
    public static LspLanguageService CreateService(DemoOptions options) =>
        CSharpLanguageServer.CreateService(
            Environment,
            new CSharpLanguageServerOptions
            {
                ServerPath = options.CSharpServer,
                DotNetPath = options.DotNet,
                Log = options.ServerLog ? line => Console.Error.WriteLine(line) : null,
            }
        );

    /// <summary>A status line for the service's state.</summary>
    public static string Describe(LspLanguageService service, double startedMs) =>
        service.State switch
        {
            LanguageServerState.Stopped => "C# server not started",
            LanguageServerState.Starting => "Starting the C# server…",
            LanguageServerState.Running =>
                $"C# server running ({startedMs:F0} ms after process start) · Ctrl+Space completes, hover for info",
            LanguageServerState.Restarting =>
                "The C# server stopped; it restarts. Highlighting and search keep working.",
            _ => "C# server stopped",
        };
}
