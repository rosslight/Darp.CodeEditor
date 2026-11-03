using System.Diagnostics;
using System.Text;
using Darp.CodeEditor.Lsp.Hosting;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>Child processes against the test process: standard streams, exit codes, and their end.</summary>
public sealed class ChildProcessTests
{
    private static string DotNet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path ? path : "dotnet";

    private static string TestProcessPath =>
        Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Lsp.TestProcess.dll");

    [Fact]
    public async Task The_child_reads_its_input_writes_its_output_and_reports_its_exit_code()
    {
        var errors = new List<string>();
        await using ChildProcess child = await ChildProcess.StartAsync(
            TestProcess("echo"),
            line =>
            {
                lock (errors)
                    errors.Add(line);
            }
        );
        using var output = new StreamReader(child.StandardOutput, Encoding.UTF8);

        await child.StandardInput.WriteAsync("héllo \"world\"\n"u8.ToArray(), Wire.Timeout);
        await child.StandardInput.FlushAsync(Wire.Timeout);
        string? echoed = await output.ReadLineAsync(Wire.Timeout);
        child.StandardInput.Close();
        int exitCode = await child.Exited.WaitAsync(Wire.Timeout);

        echoed.ShouldBe("héllo \"world\"");
        exitCode.ShouldBe(3);
        await WaitUntilAsync(() => child.StandardErrorTail == "stderr");
        lock (errors)
            errors.ShouldBe(["stderr"]);
    }

    [Fact]
    public async Task Arguments_reach_the_child_unchanged()
    {
        string[] arguments = ["a b", "c\"d", @"e\", "", @"f\""g", @"h\\i"];
        ProcessStartInfo start = TestProcess("args");
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        await using ChildProcess child = await ChildProcess.StartAsync(start);

        string output = await new StreamReader(child.StandardOutput).ReadToEndAsync(Wire.Timeout);

        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ShouldBe(arguments.Select(a => $"[{a}]"));
    }

    [Fact]
    public async Task Disposing_kills_the_child()
    {
        ChildProcess child = await ChildProcess.StartAsync(TestProcess("sleep"));
        (await new StreamReader(child.StandardOutput).ReadLineAsync(Wire.Timeout)).ShouldBe("ready");

        await child.DisposeAsync();

        child.Exited.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task The_child_dies_when_its_host_is_killed()
    {
        var start = new ProcessStartInfo(DotNet) { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(TestProcessPath);
        start.ArgumentList.Add("host");
        using Process host = Process.Start(start) ?? throw new InvalidOperationException("The host did not start.");
        int child = 0;
        try
        {
            string? line = await host.StandardOutput.ReadLineAsync(Wire.Timeout);
            string text = line.ShouldNotBeNull();
            text.ShouldStartWith("child ");
            child = int.Parse(text["child ".Length..]);
            IsRunning(child).ShouldBeTrue();

            // Only the host: its child must not need it.
            host.Kill(entireProcessTree: false);

            await WaitUntilAsync(() => !IsRunning(child));
        }
        finally
        {
            if (!host.HasExited)
                host.Kill(entireProcessTree: true);
            if (child != 0 && IsRunning(child))
                Process.GetProcessById(child).Kill();
        }
    }

    private static ProcessStartInfo TestProcess(string mode)
    {
        var start = new ProcessStartInfo(DotNet);
        start.ArgumentList.Add(TestProcessPath);
        start.ArgumentList.Add(mode);
        return start;
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        CancellationToken timeout = Wire.Timeout;
        while (!condition())
            await Task.Delay(50, timeout);
    }
}
