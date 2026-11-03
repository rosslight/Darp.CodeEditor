using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Shouldly;

namespace Darp.CodeEditor.Luau.Tests;

/// <summary>
/// <c>Darp.CodeEditor.Luau.targets</c> checks luau-lsp's archive against the pinned SHA-256 before it uses it, whether
/// downloaded or given as <c>LuauLspArchive</c>.
/// </summary>
public sealed class LuauLspFetchTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("luau-lsp-fetch-");

    public LuauLspFetchTests() => WriteProject(_directory.FullName);

    /// <summary>The portable runtime identifier of this machine, which has a luau-lsp build.</summary>
    private static string RuntimeIdentifier =>
        (
            OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux"
        ) + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64" : "-x64");

    /// <summary>The pinned release's archive, which the build of these tests downloaded and checked.</summary>
    private static string ReleaseArchive =>
        Path.Combine(
            Metadata("LuauLspCacheDirectory"),
            LuauLanguageServer.Version,
            $"luau-lsp-{(
                OperatingSystem.IsWindows() ? "win64"
                : OperatingSystem.IsMacOS() ? "macos"
                : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64"
                : "linux-x86_64"
            )}.zip"
        );

    [Fact]
    public async Task A_download_with_another_checksum_fails_the_build_and_stays_out_of_the_cache()
    {
        string cache = Path.Combine(_directory.FullName, "cache");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/luau-lsp.zip";
        Task serving = ServeAsync(listener, "not luau-lsp"u8.ToArray());

        (int exitCode, string output) = await MsBuildAsync(
            $"-p:LuauLspCacheDirectory={cache}{Path.DirectorySeparatorChar}",
            $"-p:LuauLspDownloadUrl={url}"
        );
        listener.Stop();
        await serving;

        exitCode.ShouldNotBe(0, output);
        output.ShouldContain("has the SHA-256");
        output.ShouldContain("It was deleted.");
        Directory.GetFiles(cache, "*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_local_archive_is_unpacked_only_with_the_pinned_checksum()
    {
        string tampered = Path.Combine(_directory.FullName, "tampered.zip");
        byte[] release = await File.ReadAllBytesAsync(ReleaseArchive, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(tampered, [.. release, 0], TestContext.Current.CancellationToken);

        (int tamperedExitCode, string tamperedOutput) = await MsBuildAsync($"-p:LuauLspArchive={tampered}");
        (int exitCode, string output) = await MsBuildAsync($"-p:LuauLspArchive={ReleaseArchive}");

        tamperedExitCode.ShouldNotBe(0, tamperedOutput);
        tamperedOutput.ShouldContain("has the SHA-256");
        exitCode.ShouldBe(0, output);
        string unpacked = UnpackedPath(output);
        File.Exists(unpacked).ShouldBeTrue(unpacked);
        new FileInfo(unpacked).Length.ShouldBeGreaterThan(1_000_000);
    }

    [Fact]
    public async Task A_project_path_with_shell_syntax_is_never_run_as_a_command()
    {
        // Windows file names cannot hold quotes.
        string name = OperatingSystem.IsWindows()
            ? "luau review `touch REVIEW_MARKER`"
            : "luau \"review\" `touch REVIEW_MARKER`";
        string project = Directory.CreateDirectory(Path.Combine(_directory.FullName, name)).FullName;
        WriteProject(project);

        (int exitCode, string output) = await MsBuildInAsync(
            project,
            RuntimeIdentifier,
            $"-p:LuauLspArchive={ReleaseArchive}"
        );

        exitCode.ShouldBe(0, output);
        Directory.GetFiles(_directory.FullName, "REVIEW_MARKER", SearchOption.AllDirectories).ShouldBeEmpty();
        string unpacked = UnpackedPath(output);
        unpacked.ShouldStartWith(project);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(unpacked).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue(unpacked);
    }

    public void Dispose() => _directory.Delete(recursive: true);

    private static void WriteProject(string directory) =>
        File.WriteAllText(
            Path.Combine(directory, "fetch.proj"),
            $"""
            <Project>
              <PropertyGroup>
                <IntermediateOutputPath>obj/</IntermediateOutputPath>
              </PropertyGroup>
              <Import Project="{Metadata("LuauTargets")}" />
              <Target Name="Resolve" DependsOnTargets="ResolveLuauLsp">
                <Message Importance="high" Text="LuauLspPath=$(LuauLspPath)" />
              </Target>
            </Project>
            """
        );

    private static string UnpackedPath(string output) =>
        output.Split('\n').Single(l => l.Contains("LuauLspPath=")).Split("LuauLspPath=")[1].Trim();

    private static string Metadata(string key) =>
        typeof(LuauLspFetchTests)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == key)
            .Value!;

    /// <summary>Answers every HTTP request with <paramref name="body"/> until the listener stops.</summary>
    private static async Task ServeAsync(TcpListener listener, byte[] body)
    {
        try
        {
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                NetworkStream stream = client.GetStream();
                // The request ends with an empty line.
                var request = new List<byte>();
                var buffer = new byte[1024];
                while (request.Count < 4 || request[^4..] is not [13, 10, 13, 10])
                {
                    int read = await stream.ReadAsync(buffer);
                    if (read == 0)
                        break;
                    request.AddRange(buffer.AsSpan(0, read));
                }
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                    )
                );
                await stream.WriteAsync(body);
            }
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
        {
            // Stopped.
        }
    }

    private Task<(int ExitCode, string Output)> MsBuildAsync(params string[] properties) =>
        MsBuildInAsync(_directory.FullName, RuntimeIdentifier, properties);

    /// <summary>
    /// Runs the targets' resolve step in the project in <paramref name="directory"/>, with MSBuild processes of its own.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> MsBuildInAsync(
        string directory,
        string runtimeIdentifier,
        params string[] properties
    )
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (
            string argument in (string[])
                [
                    "msbuild",
                    "fetch.proj",
                    "-t:Resolve",
                    "-nologo",
                    "-nodeReuse:false",
                    $"-p:RuntimeIdentifier={runtimeIdentifier}",
                    .. properties,
                ]
        )
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        using Process process = Process.Start(start)!;
        var output = new StringBuilder();
        Task stdout = CopyAsync(process.StandardOutput, output);
        Task stderr = CopyAsync(process.StandardError, output);
        try
        {
            await process
                .WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
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
