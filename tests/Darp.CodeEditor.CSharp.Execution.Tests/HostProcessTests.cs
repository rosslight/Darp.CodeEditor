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
            (int exitCode, string log) = await DotNetAsync([
                "publish",
                GetMetadata("TestHostProject"),
                "-c",
                "Release",
                "-r",
                PortableRuntimeIdentifier,
                "-p:PublishAot=true",
                "-o",
                output,
            ]);
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
            // Satellite assemblies ship in their culture directories, like in a build.
            File.Exists(Path.Combine(output, "en", "Darp.CodeEditor.CSharp.Execution.TestDependency.resources.dll"))
                .ShouldBeTrue();
            // What scripts compile against ships; what the runner already has, the same assembly, does not.
            File.Exists(Path.Combine(output, "StreamJsonRpc.dll")).ShouldBeTrue();
            File.Exists(Path.Combine(output, "PolyType.dll")).ShouldBeTrue();
            File.Exists(Path.Combine(output, "Nerdbank.MessagePack.dll")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    /// <summary>
    /// The portable runtime identifier of this machine, such as linux-x64. <see cref="RuntimeInformation.RuntimeIdentifier"/>
    /// is a distribution's own identifier on runtimes the distribution built.
    /// </summary>
    private static string PortableRuntimeIdentifier =>
        (
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux"
        )
        + "-"
        + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

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

    /// <summary>
    /// Runs the dotnet CLI with build processes of its own: MSBuild nodes and compiler servers are shared by every
    /// build of the user, and a reused one outside this test can end the test's process.
    /// </summary>
    private static Task<(int ExitCode, string Output)> DotNetAsync(IEnumerable<string> arguments) =>
        RunAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [.. arguments, "-nodeReuse:false", "-p:UseSharedCompilation=false"],
            TimeSpan.FromMinutes(8),
            new Dictionary<string, string> { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0" }
        );

    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment = null
    )
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
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
