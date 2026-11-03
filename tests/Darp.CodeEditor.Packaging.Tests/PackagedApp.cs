using System.Reflection;
using System.Xml.Linq;
using Darp.CodeEditor.Testing;
using Shouldly;

namespace Darp.CodeEditor.Packaging.Tests;

/// <summary>
/// The packages of this repository in a folder feed, and an app outside the repository that references them like any
/// app does: by package ID and version, with nuget.org for everything else.
/// </summary>
public sealed class PackagedApp : IAsyncLifetime
{
    private static readonly string[] s_packages =
    [
        "Darp.CodeEditor.Lsp",
        "Darp.CodeEditor",
        "Darp.CodeEditor.CSharp",
        "Darp.CodeEditor.Luau",
    ];
    private static readonly TimeSpan s_buildTimeout = TimeSpan.FromMinutes(10);

    private readonly string _root = Directory.CreateTempSubdirectory("codeeditor-packages-").FullName;

    /// <summary>A version no other build uses, so restore cannot take a package from an earlier run out of its cache.</summary>
    private readonly string _version = $"0.0.0-e2e.{DateTime.UtcNow:yyyyMMddHHmmss}.{Environment.ProcessId}";

    private string AppDirectory => Path.Combine(_root, "app");

    public async ValueTask InitializeAsync()
    {
        string repository = GetMetadata("RepositoryRoot");
        string feed = Path.Combine(_root, "feed");
        foreach (string package in s_packages)
        {
            // The build of this test built them; only the package version differs from that build.
            (int exitCode, string log) = await CommandLine.DotNetAsync(
                [
                    "pack",
                    Path.Combine(repository, "src", package, package + ".csproj"),
                    "-c",
                    GetMetadata("Configuration"),
                    "--no-build",
                    $"-p:PackageVersion={_version}",
                    "-o",
                    feed,
                ],
                s_buildTimeout,
                CancellationToken.None
            );
            exitCode.ShouldBe(0, log);
        }

        CopyDirectory(Path.Combine(repository, "tests", "Darp.CodeEditor.Packaging.Tests", "Fixture"), AppDirectory);
        File.Copy(Path.Combine(repository, "global.json"), Path.Combine(AppDirectory, "global.json"));
        // Empty build files keep those of directories above out of the app.
        File.WriteAllText(Path.Combine(AppDirectory, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(AppDirectory, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(
            Path.Combine(AppDirectory, "Directory.Packages.props"),
            $"""
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Darp.CodeEditor.CSharp" Version="{_version}" />
                <PackageVersion Include="Darp.CodeEditor.Luau" Version="{_version}" />
                <PackageVersion Include="StreamJsonRpc" Version="{RepositoryPackageVersion(repository, "StreamJsonRpc")}" />
              </ItemGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(AppDirectory, "nuget.config"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="packages" value="{feed}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
            </configuration>
            """
        );
    }

    /// <summary>Publishes the app for this machine's runtime identifier and returns the directory it went to.</summary>
    public async Task<(string Directory, string Log)> PublishAsync(string name, IEnumerable<string> arguments)
    {
        string output = Path.Combine(_root, name);
        (int exitCode, string log) = await CommandLine.DotNetAsync(
            [
                "publish",
                Path.Combine(AppDirectory, "Host", "Host.csproj"),
                "-c",
                "Release",
                "-r",
                PortableRuntimeIdentifier,
                "-o",
                output,
                .. arguments,
            ],
            s_buildTimeout,
            TestContext.Current.CancellationToken,
            AppDirectory
        );
        exitCode.ShouldBe(0, log);
        return (output, log);
    }

    /// <summary>Builds the app and returns its output directory.</summary>
    public async Task<string> BuildAsync()
    {
        string output = Path.Combine(_root, "build");
        (int exitCode, string log) = await CommandLine.DotNetAsync(
            ["build", Path.Combine(AppDirectory, "Host", "Host.csproj"), "-c", "Release", "-o", output],
            s_buildTimeout,
            TestContext.Current.CancellationToken,
            AppDirectory
        );
        exitCode.ShouldBe(0, log);
        return output;
    }

    public async ValueTask DisposeAsync()
    {
        // Packages of this run in the global packages folder: no other build uses their version.
        (int exitCode, string locals) = await CommandLine.DotNetAsync(
            ["nuget", "locals", "global-packages", "--list"],
            TimeSpan.FromMinutes(1),
            CancellationToken.None,
            _root
        );
        const string prefix = "global-packages: ";
        if (exitCode == 0 && locals.Split('\n').FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } line)
        {
            string packages = line[prefix.Length..].Trim();
            foreach (string package in s_packages)
            {
                string cached = Path.Combine(packages, package.ToLowerInvariant(), _version);
                if (Directory.Exists(cached))
                    await DeleteAsync(cached);
            }
        }
        await DeleteAsync(_root);
    }

    /// <summary>
    /// Deletes a directory of this run. On Windows, a virus scanner or the indexer can hold files that were just built
    /// for a moment, so it tries again for a while; what is still left then stays behind, and the tests do not fail.
    /// </summary>
    private static async Task DeleteAsync(string directory)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20)
                {
                    TestContext.Current.SendDiagnosticMessage($"Could not delete {directory}: {exception.Message}");
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    /// <summary>
    /// The portable runtime identifier of this machine, such as linux-x64. <see cref="System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier"/>
    /// is a distribution's own identifier on runtimes the distribution built.
    /// </summary>
    public static string PortableRuntimeIdentifier =>
        (
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux"
        )
        + "-"
        + System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    private static string RepositoryPackageVersion(string repository, string id) =>
        XDocument
            .Load(Path.Combine(repository, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(p => (string?)p.Attribute("Include") == id)
            .Attribute("Version")
            ?.Value
        ?? throw new InvalidOperationException($"Directory.Packages.props has no version of {id}.");

    private static string GetMetadata(string key) =>
        typeof(PackagedApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value
        ?? throw new InvalidOperationException($"No {key}.");

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            // Left behind by building the fixture in place.
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target);
        }
    }
}
