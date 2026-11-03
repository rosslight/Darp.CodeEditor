using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Luau;

namespace Darp.CodeEditor.Samples;

/// <summary>The demos' Luau scripts: the app's Luau API, and the luau-lsp service for it.</summary>
internal static class DemoLuau
{
    /// <summary><c>device</c>, <c>limits</c> and <c>log</c>, as in the C# script's globals, with documentation.</summary>
    public static LuauEnvironment Environment { get; } =
        new()
        {
            Definitions = [new LuauDefinitions("demo", Path.Combine(AppContext.BaseDirectory, "Luau", "demo.d.luau"))],
            DocumentationFiles = [Path.Combine(AppContext.BaseDirectory, "Luau", "demo.docs.json")],
            Mode = LuauMode.Strict,
        };

    /// <summary>The service of luau-lsp, which the demo's targets put next to the app.</summary>
    public static LspLanguageService CreateService(DemoOptions options) =>
        LuauLanguageServer.CreateService(
            Environment,
            new LuauLanguageServerOptions
            {
                ServerPath = options.LuauLsp,
                Log = options.ServerLog ? line => Console.Error.WriteLine(line) : null,
            }
        );

    /// <summary>A status for the service's state.</summary>
    public static string Describe(LspLanguageService service) =>
        service.State switch
        {
            LanguageServerState.Starting => "Luau: starting",
            LanguageServerState.Running => "Luau: luau-lsp running",
            LanguageServerState.Restarting => "Luau: luau-lsp restarts",
            _ => "Luau: off",
        };
}
