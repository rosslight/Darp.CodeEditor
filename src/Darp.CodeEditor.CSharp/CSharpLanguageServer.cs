using System.Diagnostics;
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
    public const string ServerFileName = CSharpHelper.ServerName + ".dll";

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
        string? server = options.ServerPath is { } path ? Path.GetFullPath(path) : null;
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
                ResponseTimeout = options.ResponseTimeout,
                MaxResponseTimeout = options.MaxResponseTimeout,
            }
        );
    }

    private static Task<LanguageServerTransport> StartAsync(string? server, CSharpLanguageServerOptions options)
    {
        const string howToFix =
            "Reference the Darp.CodeEditor.CSharp package from the app project, or set "
            + "CSharpLanguageServerOptions.ServerPath.";
        if (server is null)
            server = CSharpHelper.Find(CSharpHelper.DefaultDirectory("csharp-server"), CSharpHelper.ServerName, howToFix);
        else if (!File.Exists(server))
            throw new FileNotFoundException($"The C# script language server is not at {server}. {howToFix}", server);
        Dictionary<string, string> environment = [];
        if (options.DotNetRoot is { } root)
            environment["DOTNET_ROOT"] = root;
        ProcessStartInfo start = CSharpHelper.CreateStartInfo(server, options.DotNetPath, environment);
        foreach (string argument in options.Arguments)
            start.ArgumentList.Add(argument);
        Action<string>? log = options.Log;
        return LanguageServerTransport.StartProcessAsync(start, log is null ? null : line => log($"C# server: {line}"));
    }
}

/// <summary>Where the C# script language server is, and how it runs.</summary>
/// <remarks>
/// The server inherits the app's environment without the variables that start with <c>DOTNET_</c>, <c>COMPlus_</c> or
/// <c>CORECLR_</c>, such as startup hooks, profilers and runtime settings meant for the app.
/// </remarks>
public sealed record CSharpLanguageServerOptions
{
    /// <summary>
    /// The server: its <c>.dll</c>, which runs with <c>dotnet</c>, or its app host. By default the one in
    /// <c>helpers/csharp-server</c> next to the app, where the <c>Darp.CodeEditor.CSharp</c> package puts it: a
    /// published app has its app host, which runs on the .NET runtime in <c>helpers/dotnet</c>; a build output has its
    /// <c>.dll</c>.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>
    /// The <c>dotnet</c> executable that runs a <c>.dll</c> server. By default the one that runs the app's tooling or
    /// the app, or the one on the <c>PATH</c>.
    /// </summary>
    public string? DotNetPath { get; init; }

    /// <summary>
    /// <c>DOTNET_ROOT</c> for the server, for an app host that is not tied to a runtime of its own. The app hosts in
    /// <c>helpers/</c> ignore it: they run only on the runtime in <c>helpers/dotnet</c>.
    /// </summary>
    public string? DotNetRoot { get; init; }

    /// <summary>Further server arguments, such as <c>--diagnostics-delay 100</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Receives the server's standard error, its log messages and its restarts.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long a crashed server waits before it restarts.</summary>
    public SupervisorOptions? Restart { get; init; }

    /// <summary>
    /// How long the server may take to answer or to read before it counts as hung and restarts; see
    /// <see cref="LspLanguageServiceOptions.ResponseTimeout"/>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest that hangs in a row raise <see cref="ResponseTimeout"/> to; see
    /// <see cref="LspLanguageServiceOptions.MaxResponseTimeout"/>.
    /// </summary>
    public TimeSpan MaxResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
