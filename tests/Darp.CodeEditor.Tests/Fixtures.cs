using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Darp.CodeEditor.Tests;

/// <summary>The fixture processes beside the tests.</summary>
internal static class Fixtures
{
    /// <summary>
    /// The command line of the fixture <paramref name="name"/>: published as NativeAOT, its native executable beside
    /// the tests; otherwise its DLL on dotnet.
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) CommandLine(
        string name,
        params IEnumerable<string> arguments
    )
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, name);
        if (!RuntimeFeature.IsDynamicCodeSupported)
            return (OperatingSystem.IsWindows() ? $"{fixture}.exe" : fixture, [.. arguments]);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";
        return (dotnet, [$"{fixture}.dll", .. arguments]);
    }

    /// <summary>Starts the fixture <paramref name="name"/> with redirected output.</summary>
    public static Process Start(string name, params IEnumerable<string> arguments)
    {
        (string fileName, IReadOnlyList<string> commandLine) = CommandLine(name, arguments);
        var start = new ProcessStartInfo(fileName, commandLine)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return Process.Start(start) ?? throw new InvalidOperationException($"{name} did not start.");
    }
}
