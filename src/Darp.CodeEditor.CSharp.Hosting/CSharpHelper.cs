using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Darp.CodeEditor.CSharp.Hosting;

/// <summary>
/// Finds and starts the C# helpers, the language server and the script runner. <c>Darp.CodeEditor.CSharp.targets</c>
/// puts them in <c>helpers/</c> next to the app: a published app has their app hosts, which run on the .NET runtime in
/// <c>helpers/dotnet</c> and look nowhere else; a build output has only their assemblies, which run with <c>dotnet</c>
/// on the runtime that runs the app.
/// </summary>
internal static class CSharpHelper
{
    public const string ServerName = "Darp.CodeEditor.CSharp.Server";
    public const string RunnerName = "Darp.CodeEditor.CSharp.Runner";

    /// <summary>
    /// Prefixes of the variables through which .NET loads or attaches code at startup or tunes the runtime: startup
    /// hooks, profilers, diagnostic ports, extra dependencies and runtime knobs. They are meant for the app.
    /// </summary>
    private static readonly string[] s_removedVariablePrefixes = ["DOTNET_", "COMPlus_", "CORECLR_"];

    /// <summary>The directory a helper has next to the app, such as <c>helpers/csharp-server</c>.</summary>
    public static string DefaultDirectory(string directoryName) =>
        Path.Combine(AppContext.BaseDirectory, "helpers", directoryName);

    /// <summary>
    /// The helper <paramref name="name"/> in <paramref name="directory"/>: its app host when it has one, else its
    /// assembly.
    /// </summary>
    /// <exception cref="FileNotFoundException">Neither is there.</exception>
    public static string Find(string directory, string name, string howToFix)
    {
        string appHost = Path.Combine(directory, OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (File.Exists(appHost))
            return appHost;
        string assembly = Path.Combine(directory, name + ".dll");
        if (File.Exists(assembly))
            return assembly;
        throw new FileNotFoundException($"{name} is not in {directory}. {howToFix}", assembly);
    }

    /// <summary>
    /// The start of <paramref name="path"/>, an app host or an assembly, which runs with <paramref name="dotNetPath"/>
    /// or else the <c>dotnet</c> of the runtime that runs the app. It inherits the app's environment without the
    /// variables that start with <c>DOTNET_</c>, <c>COMPlus_</c> or <c>CORECLR_</c>, then gets
    /// <paramref name="environmentVariables"/>. Arguments follow the assembly in its argument list.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(
        string path,
        string? dotNetPath,
        IReadOnlyDictionary<string, string> environmentVariables
    )
    {
        bool assembly = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(assembly ? dotNetPath ?? FindDotNet() : path)
        {
            WorkingDirectory = Path.GetDirectoryName(path),
        };
        if (assembly)
            start.ArgumentList.Add(path);
        foreach (string variable in start.Environment.Keys.ToArray())
        {
            if (s_removedVariablePrefixes.Any(prefix => variable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                start.Environment.Remove(variable);
        }
        foreach ((string variable, string value) in environmentVariables)
            start.Environment[variable] = value;
        return start;
    }

    /// <summary>
    /// The <c>dotnet</c> executable: the one that runs the app's tooling, the one in <c>DOTNET_ROOT</c>, the one of the
    /// runtime that runs the app, or else the one on the <c>PATH</c>.
    /// </summary>
    public static string FindDotNet()
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
