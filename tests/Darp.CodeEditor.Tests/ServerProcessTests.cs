using System.Diagnostics;

namespace Darp.CodeEditor.Tests;

/// <summary>Language servers are children of the app.</summary>
public sealed class ServerProcessTests
{
    [Fact]
    public async Task A_killed_app_takes_its_language_server_along()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Only Windows has a Job Object; elsewhere servers watch the app."
        );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // The host starts a server that never reads its standard input, as the library starts language servers.
        using Process host = Fixtures.Start("Darp.CodeEditor.Tests.FakeServer", "host");
        string? line = await host.StandardOutput.ReadLineAsync(timeout.Token);
        using Process server = Process.GetProcessById(int.Parse(line ?? throw new InvalidDataException("No id.")));

        host.Kill();

        await server.WaitForExitAsync(timeout.Token);
    }
}
