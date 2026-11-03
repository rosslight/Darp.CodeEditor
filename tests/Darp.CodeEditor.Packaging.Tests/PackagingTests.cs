using System.Reflection.PortableExecutable;
using Darp.CodeEditor.Testing;
using Shouldly;

namespace Darp.CodeEditor.Packaging.Tests;

/// <summary>
/// An app that references only the packages completes a C# script through the language server and runs a script in
/// the runner. Published, both helpers run with ReadyToRun on the .NET runtime the app ships, with no SDK and no
/// installed .NET in reach; built, they run on the .NET that runs the app.
/// </summary>
public sealed class PackagingTests : IClassFixture<PackagedApp>
{
    private static readonly string s_executable = OperatingSystem.IsWindows() ? ".exe" : "";

    private readonly PackagedApp _app;

    public PackagingTests(PackagedApp app)
    {
        _app = app;
    }

    [Fact]
    public async Task Self_contained_JIT_app_publishes_without_warnings_and_runs_its_helpers_on_the_bundled_runtime()
    {
        (string output, string log) = await _app.PublishAsync("jit", ["--self-contained"]);

        log.ShouldNotContain("warning", Case.Insensitive, log);
        await RunsOnTheBundledRuntimeAsync(output);
    }

    [Fact]
    public async Task NativeAOT_app_publishes_without_warnings_and_runs_its_helpers_on_the_bundled_runtime()
    {
        Assert.SkipUnless(
            OperatingSystem.IsLinux() || OperatingSystem.IsWindows(),
            "NativeAOT apps are checked on Linux and Windows."
        );

        (string output, string log) = await _app.PublishAsync("aot", ["-p:PublishAot=true"]);

        log.ShouldNotContain("warning", Case.Insensitive, log);
        // NativeAOT compiles the app's assemblies into it: the script API must ship as a file for the helpers.
        File.Exists(Path.Combine(output, "ScriptApi.dll")).ShouldBeTrue();
        await RunsOnTheBundledRuntimeAsync(output);
    }

    [Fact]
    public async Task Built_app_runs_its_helpers_on_the_runtime_that_runs_the_app()
    {
        string output = await _app.BuildAsync();

        (int exitCode, string log) = await CommandLine.RunAsync(
            Path.Combine(output, "Host" + s_executable),
            [],
            TimeSpan.FromMinutes(3),
            TestContext.Current.CancellationToken
        );

        exitCode.ShouldBe(0, log);
        File.Exists(Path.Combine(output, "helpers", "csharp-server", "Darp.CodeEditor.CSharp.Server.dll")).ShouldBeTrue();
        Directory.Exists(Path.Combine(output, "helpers", "dotnet")).ShouldBeFalse();
        Value(log, "completion: ").Split(", ").ShouldContain("AddAsync", log);
        Value(log, "handler: ").ShouldBe("42", log);
    }

    private static async Task RunsOnTheBundledRuntimeAsync(string output)
    {
        string helpers = Path.Combine(output, "helpers");
        string runtime = Directory
            .GetDirectories(Path.Combine(helpers, "dotnet", "shared", "Microsoft.NETCore.App"))
            .ShouldHaveSingleItem();
        Directory
            .EnumerateFiles(Path.Combine(helpers, "dotnet", "host", "fxr"), "*hostfxr*", SearchOption.AllDirectories)
            .ShouldNotBeEmpty();
        File.Exists(Path.Combine(helpers, "csharp-server", "Darp.CodeEditor.CSharp.Server" + s_executable))
            .ShouldBeTrue();
        File.Exists(Path.Combine(helpers, "csharp-runner", "Darp.CodeEditor.CSharp.Runner" + s_executable))
            .ShouldBeTrue();
        IsReadyToRun(Path.Combine(helpers, "csharp-server", "Microsoft.CodeAnalysis.CSharp.Features.dll")).ShouldBeTrue();
        IsReadyToRun(Path.Combine(helpers, "csharp-runner", "Microsoft.CodeAnalysis.CSharp.dll")).ShouldBeTrue();

        (int exitCode, string log) = await CommandLine.RunAsync(
            Path.Combine(output, "Host" + s_executable),
            [],
            TimeSpan.FromMinutes(3),
            TestContext.Current.CancellationToken,
            WithoutDotNet()
        );

        exitCode.ShouldBe(0, log);
        Value(log, "completion: ").Split(", ").ShouldContain("AddAsync", log);
        Value(log, "handler: ").ShouldBe("42", log);
        // Compared from the publish directory on: a temporary path can be spelled differently on Windows.
        string bundled = Path.Combine(
            Path.GetFileName(output),
            "helpers",
            "dotnet",
            "shared",
            "Microsoft.NETCore.App",
            Path.GetFileName(runtime)
        );
        Value(log, "server runtime: ").ShouldEndWith(bundled, Case.Insensitive, log);
        Value(log, "runner runtime: ").ShouldEndWith(bundled, Case.Insensitive, log);
    }

    /// <summary>
    /// The test's environment without what points to a .NET installation: no <c>DOTNET_</c> or MSBuild variables, and
    /// no directory with <c>dotnet</c> on the <c>PATH</c>.
    /// </summary>
    private static Dictionary<string, string?> WithoutDotNet()
    {
        var environment = new Dictionary<string, string?>();
        foreach (string name in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            if (
                name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)
            )
            {
                environment[name] = null;
            }
        }
        string dotnet = "dotnet" + s_executable;
        environment["PATH"] = string.Join(
            Path.PathSeparator,
            (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator)
                .Where(directory => directory.Length > 0 && !File.Exists(Path.Combine(directory, dotnet)))
        );
        return environment;
    }

    private static bool IsReadyToRun(string assembly)
    {
        using var reader = new PEReader(File.OpenRead(assembly));
        return reader.PEHeaders.CorHeader is { ManagedNativeHeaderDirectory.Size: > 0 };
    }

    /// <summary>The rest of the line of <paramref name="log"/> that starts with <paramref name="prefix"/>.</summary>
    private static string Value(string log, string prefix) =>
        log.Split('\n').Select(line => line.TrimEnd('\r')).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[
            prefix.Length..
        ] ?? throw new ShouldAssertException($"No line starts with \"{prefix}\":\n{log}");
}
