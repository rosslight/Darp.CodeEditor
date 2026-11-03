using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Darp.CodeEditor.Testing;
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

            (await WaitUntilExitedAsync(runner, TimeSpan.FromSeconds(30))).ShouldBeTrue();
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
            (int runExitCode, string runLog) = await CommandLine.RunAsync(
                Path.Combine(output, HostName + (OperatingSystem.IsWindows() ? ".exe" : "")),
                ["run"],
                TimeSpan.FromMinutes(1),
                Cancellation
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

    [Fact]
    public async Task Build_fails_when_a_script_reference_needs_another_shared_framework()
    {
        string root = Directory.CreateTempSubdirectory("csharp-runner-framework-").FullName;
        try
        {
            // Empty build files keep the repository's out of the fixture.
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");
            Directory.CreateDirectory(Path.Combine(root, "Api"));
            File.WriteAllText(
                Path.Combine(root, "Api", "Api.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
                </Project>
                """
            );
            File.WriteAllText(
                Path.Combine(root, "Api", "Web.cs"),
                "namespace Api; public static class Web { public static string Name => typeof(Microsoft.AspNetCore.Http.HttpContext).Name; }"
            );
            Directory.CreateDirectory(Path.Combine(root, "Host"));
            File.WriteAllText(
                Path.Combine(root, "Host", "Host.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Api/Api.csproj" CSharpScriptReference="true" /></ItemGroup>
                  <Import Project="{GetMetadata("CSharpTargets")}" />
                </Project>
                """
            );
            File.WriteAllText(Path.Combine(root, "Host", "Program.cs"), "System.Console.WriteLine(Api.Web.Name);");

            (int exitCode, string log) = await DotNetAsync([
                "build",
                Path.Combine(root, "Host", "Host.csproj"),
                "-c",
                GetMetadata("Configuration"),
            ]);

            exitCode.ShouldNotBe(0, log);
            log.ShouldContain(
                "The script reference Api needs the shared framework Microsoft.AspNetCore.App",
                Case.Sensitive,
                log
            );
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    private static Task<(int ExitCode, string Output)> DotNetAsync(IEnumerable<string> arguments) =>
        CommandLine.DotNetAsync(arguments, TimeSpan.FromMinutes(8), Cancellation);
}
