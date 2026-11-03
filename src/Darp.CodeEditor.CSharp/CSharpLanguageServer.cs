using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp.Hosting;

namespace Darp.CodeEditor.CSharp;

/// <summary>C# script editing: the language service of the C# script language server.</summary>
/// <example>
/// <code>
/// LspLanguageService csharp = CSharpLanguageServer.CreateService(environment);
/// _ = csharp.PrewarmAsync();          // optional: start before the first editor opens
/// editor.LanguageService = csharp;    // every C# editor of the app shares the service
/// await csharp.DisposeAsync();        // when the app ends
/// </code>
/// </example>
public static class CSharpLanguageServer
{
    /// <summary>The file name of the server's main assembly.</summary>
    public const string ServerFileName = "Darp.CodeEditor.CSharp.Server.dll";

    /// <summary>
    /// Creates a service that starts the server for <paramref name="environment"/> when the first C# document opens, or
    /// on <see cref="LspLanguageService.PrewarmAsync"/>. One service, and so one server, serves all C# editors of an app.
    /// </summary>
    public static LspLanguageService CreateService(
        CSharpScriptEnvironment environment,
        CSharpLanguageServerOptions? options = null
    )
    {
        options ??= new CSharpLanguageServerOptions();
        string server = Path.GetFullPath(
            options.ServerPath ?? Path.Combine(AppContext.BaseDirectory, "helpers", "csharp-server", ServerFileName)
        );
        using JsonDocument initializationOptions = JsonDocument.Parse(
            CSharpScriptInitializationOptions.Create(environment).ToJsonString()
        );
        return new LspLanguageService(
            new LspLanguageServiceOptions(_ => StartAsync(server, options), "csharp")
            {
                InitializationOptions = initializationOptions.RootElement.Clone(),
                ClientName = "Darp.CodeEditor.CSharp",
                Log = options.Log,
                Restart = options.Restart,
            }
        );
    }

    private static Task<LanguageServerTransport> StartAsync(string server, CSharpLanguageServerOptions options)
    {
        if (!File.Exists(server))
        {
            throw new FileNotFoundException(
                $"The C# script language server is not at {server}. Import Darp.CodeEditor.CSharp.targets into the app "
                    + "project, or set CSharpLanguageServerOptions.ServerPath.",
                server
            );
        }
        bool assembly = server.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(assembly ? options.DotNetPath ?? FindDotNet() : server)
        {
            WorkingDirectory = Path.GetDirectoryName(server),
        };
        if (assembly)
            start.ArgumentList.Add(server);
        foreach (string argument in options.Arguments)
            start.ArgumentList.Add(argument);
        if (options.DotNetRoot is { } root)
            start.Environment["DOTNET_ROOT"] = root;
        Action<string>? log = options.Log;
        return LanguageServerTransport.StartProcessAsync(start, log is null ? null : line => log($"C# server: {line}"));
    }

    /// <summary>
    /// The <c>dotnet</c> executable: the one that runs the app's tooling, the one in <c>DOTNET_ROOT</c>, the one of the
    /// runtime that runs the app, or else the one on the <c>PATH</c>.
    /// </summary>
    private static string FindDotNet()
    {
        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host))
            return host;
        if (
            Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root
            && Path.Combine(root, executable) is var rooted
            && File.Exists(rooted)
        )
        {
            return rooted;
        }
        // A JIT app runs on <root>/shared/Microsoft.NETCore.App/<version>/.
        string fromRuntime = Path.GetFullPath(
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", executable)
        );
        return File.Exists(fromRuntime) ? fromRuntime : executable;
    }
}

/// <summary>Where the C# script language server is, and how it runs.</summary>
public sealed record CSharpLanguageServerOptions
{
    /// <summary>
    /// The server: its <c>.dll</c>, which runs with <c>dotnet</c>, or its app host. By default
    /// <c>helpers/csharp-server/Darp.CodeEditor.CSharp.Server.dll</c> next to the app, where
    /// <c>Darp.CodeEditor.CSharp.targets</c> puts it.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>
    /// The <c>dotnet</c> executable that runs a <c>.dll</c> server. By default the one that runs the app's tooling or
    /// the app, or the one on the <c>PATH</c>.
    /// </summary>
    public string? DotNetPath { get; init; }

    /// <summary>The .NET runtime that an app-host server runs on, such as a runtime bundled with the app.</summary>
    public string? DotNetRoot { get; init; }

    /// <summary>Further server arguments, such as <c>--diagnostics-delay 100</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Receives the server's standard error, its log messages and its restarts.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long a crashed server waits before it restarts.</summary>
    public SupervisorOptions? Restart { get; init; }
}
