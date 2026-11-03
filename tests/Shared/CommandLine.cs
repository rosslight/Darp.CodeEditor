using System.Diagnostics;
using System.Text;

namespace Darp.CodeEditor.Testing;

/// <summary>Runs programs for tests, such as the dotnet CLI, and collects what they write.</summary>
internal static class CommandLine
{
    /// <summary>
    /// Runs the dotnet CLI with build processes of its own: MSBuild nodes and compiler servers are shared by every
    /// build of the user, and a reused one outside this test can end the test's process.
    /// </summary>
    public static Task<(int ExitCode, string Output)> DotNetAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null
    ) =>
        RunAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [.. arguments, "-nodeReuse:false", "-p:UseSharedCompilation=false"],
            timeout,
            cancellationToken,
            new Dictionary<string, string?> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" },
            workingDirectory
        );

    /// <summary>Runs <paramref name="fileName"/> to its end and returns its exit code and output.</summary>
    /// <param name="environment">Variables to set, or with a null value to remove.</param>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? workingDirectory = null
    )
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach ((string name, string? value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
                start.Environment.Remove(name);
            else
                start.Environment[name] = value;
        }
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var output = new StringBuilder();
        Task standardOutput = CopyAsync(process.StandardOutput, output);
        Task standardError = CopyAsync(process.StandardError, output);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        await Task.WhenAll(standardOutput, standardError);
        return (process.ExitCode, output.ToString());
    }

    private static async Task CopyAsync(StreamReader reader, StringBuilder output)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (output)
                output.AppendLine(line);
        }
    }
}
