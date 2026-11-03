using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;
using static Darp.CodeEditor.CSharp.Execution.Tests.TestRunners;

namespace Darp.CodeEditor.CSharp.Execution.Tests;

/// <summary>The test host as a separate app process: its runner dies with it, and it publishes as NativeAOT.</summary>
public sealed class HostProcessTests
{
    private const string HostName = "Darp.CodeEditor.CSharp.Execution.TestHost";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Runner_dies_with_its_host_while_a_script_is_busy()
    {
        string host = Path.Combine(
            GetMetadata("TestHostDirectory"),
            HostName + (OperatingSystem.IsWindows() ? ".exe" : "")
        );
        using Process process = Process.Start(
            new ProcessStartInfo(host, ["hold"]) { RedirectStandardOutput = true, RedirectStandardError = true }
        )!;
        try
        {
            string line = await ReadLineStartingWithAsync(process.StandardOutput, "runner ");
            int runner = int.Parse(line["runner ".Length..]);
            IsRunning(runner).ShouldBeTrue();

            process.Kill();

            (await WaitUntilExitedAsync(runner, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task NativeAOT_host_publishes_without_warnings_and_runs_scripts_on_the_bundled_runtime()
    {
        string output = Directory.CreateTempSubdirectory("csharp-runner-aot-").FullName;
        try
        {
            (int exitCode, string log) = await RunAsync(
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                [
                    "publish",
                    GetMetadata("TestHostProject"),
                    "-c",
                    "Release",
                    "-r",
                    RuntimeInformation.RuntimeIdentifier,
                    "-p:PublishAot=true",
                    "-o",
                    output,
                ],
                TimeSpan.FromMinutes(8)
            );
            exitCode.ShouldBe(0, log);
            log.ShouldNotContain("warning", Case.Insensitive, log);

            // The runner runs on the runtime in helpers/dotnet: its app host looks nowhere else.
            Directory
                .EnumerateFiles(
                    Path.Combine(output, "helpers", "dotnet", "host", "fxr"),
                    "*hostfxr*",
                    SearchOption.AllDirectories
                )
                .ShouldNotBeEmpty();
            (int runExitCode, string runLog) = await RunAsync(
                Path.Combine(output, HostName + (OperatingSystem.IsWindows() ? ".exe" : "")),
                ["run"],
                TimeSpan.FromMinutes(1)
            );
            runExitCode.ShouldBe(0, runLog);
            runLog.ShouldContain("result 42 batch 3,4");
            // The globals use a private dependency two levels deep; publish must ship it and the runner must find it.
            runLog.ShouldContain("globals from MetadataLoadContext");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static string GetMetadata(string key) =>
        typeof(HostProcessTests)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key)
            .Value
        ?? throw new InvalidOperationException($"No {key}.");

    private static async Task<string> ReadLineStartingWithAsync(StreamReader reader, string prefix)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line;
        }
        throw new InvalidOperationException($"The host ended without writing \"{prefix}\".");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout
    )
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        var output = new StringBuilder();
        Task stdout = CopyAsync(process.StandardOutput, output);
        Task stderr = CopyAsync(process.StandardError, output);
        try
        {
            await process.WaitForExitAsync(Cancellation).WaitAsync(timeout, Cancellation);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        await Task.WhenAll(stdout, stderr);
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
