using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharpScript.Protocol;

namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// What the build puts next to the app for its scripts: the server, and the lists of the assemblies scripts may use.
/// </summary>
internal sealed class ScriptAppFiles
{
    private const string ServerName = "Darp.CodeEditor.CSharpScript.Server";

    private static readonly string s_helpers = Path.Combine(AppContext.BaseDirectory, "helpers");

    /// <summary>The marked assemblies' paths, by assembly name.</summary>
    private readonly Dictionary<string, string> _marked;

    /// <summary>The names of .NET's assemblies, which scripts can always use.</summary>
    private readonly HashSet<string> _framework;

    private ScriptAppFiles(Dictionary<string, string> marked, HashSet<string> framework)
    {
        _marked = marked;
        _framework = framework;
    }

    /// <summary>
    /// How to start the server. In development it runs on the <c>dotnet</c> host of the installation whose runtime runs
    /// the app, never on one from <c>PATH</c>, which may lack that runtime. An app without such a host, as a NativeAOT
    /// app, starts the app host that publishing puts there.
    /// </summary>
    public static (string FileName, string[] Arguments) ServerCommand()
    {
        string server = Path.Combine(s_helpers, "csharp-server", ServerName);
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        return InstalledHost(RuntimeEnvironment.GetRuntimeDirectory(), extension) is { } host
            ? (host, [server + ".dll"])
            : (server + extension, []);
    }

    /// <summary>
    /// The host next to the shared framework that runs the app, in <c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c>;
    /// null for a self-contained or NativeAOT app, whose folder says nothing about a <c>dotnet</c> above it.
    /// </summary>
    internal static string? InstalledHost(string runtimeDirectory, string extension)
    {
        var runtime = new DirectoryInfo(runtimeDirectory);
        return
            runtime.Parent is { Name: "Microsoft.NETCore.App", Parent: { Name: "shared", Parent: { } root } }
            && Path.Combine(root.FullName, "dotnet" + extension) is var host
            && File.Exists(host)
            ? host
            : null;
    }

    /// <summary>Reads the lists that the build wrote; without them, scripts may use only .NET's assemblies.</summary>
    public static ScriptAppFiles Read()
    {
        var marked = new Dictionary<string, string>();
        foreach (string file in Lines("script-references.txt"))
            marked[Path.GetFileNameWithoutExtension(file)] = Path.Combine(AppContext.BaseDirectory, file);
        return new ScriptAppFiles(marked, [.. Lines("script-framework.txt")]);

        static string[] Lines(string name) =>
            Path.Combine(s_helpers, name) is var path && File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <summary>What the server gets of <paramref name="environment"/>, once its references are checked.</summary>
    /// <exception cref="ArgumentException">An assembly is neither .NET's nor marked.</exception>
    public ScriptEnvironmentData Describe<TGlobals>(CSharpScriptEnvironment<TGlobals> environment)
        where TGlobals : class
    {
        string globals = typeof(TGlobals).Assembly.GetName().Name ?? "";
        IEnumerable<string> references = (environment.References ?? [.. _marked.Keys]).Where(n =>
            !_framework.Contains(n)
        );
        return new ScriptEnvironmentData(
            typeof(TGlobals).FullName ?? typeof(TGlobals).Name,
            PathOf(globals),
            [.. references.Select(PathOf)],
            environment.Imports,
            environment.LanguageVersion,
            environment.Nullable
        );
    }

    private string PathOf(string assembly) =>
        _marked.GetValueOrDefault(assembly)
        ?? throw new ArgumentException(
            $"The assembly '{assembly}' is not next to the app. Mark its reference with CSharpScriptReference=\"true\"."
        );
}
