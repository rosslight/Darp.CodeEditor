using System.Diagnostics;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Samples;

/// <summary>Startup milestones and memory of an editor on a language server, such as the C# server or luau-lsp.</summary>
internal sealed class LanguageServerBench
{
    private readonly string _name;
    private readonly LspLanguageService _service;
    private readonly Task<double> _running;
    private readonly Task<double> _classified;
    private readonly Task<double> _diagnosed;

    /// <summary>Watches the milestones; create it before the first frame.</summary>
    /// <param name="name">The server's name in the report, such as "C# server" or "luau-lsp".</param>
    public LanguageServerBench(string name, LspLanguageService service, TimingLog timings)
    {
        _name = name;
        _service = service;
        _running = WhenRunningAsync(service);
        // Before the server runs, classification answers nothing; the first colors are the first non-empty answer.
        _classified = timings.WhenFirstAsync(t => t.Name == "classification" && t.Count > 0);
        _diagnosed = timings.WhenFirstAsync(t => t.Name == "diagnostics");
    }

    /// <summary>Waits until the server runs and the editor shows semantic colors and diagnostics.</summary>
    public Task WaitUntilReadyAsync() =>
        Task.WhenAll(_running, _classified, _diagnosed).WaitAsync(TimeSpan.FromSeconds(60));

    public async Task ReportAsync(BenchReport report, double editorsCreatedMs)
    {
        double running = await _running;
        double classified = await _classified;
        double diagnosed = await _diagnosed;
        double ready = Math.Max(classified, diagnosed);
        report.Add($"  {_name} running: {running, 7:F0} ms after process start (initialize answered)");
        report.Add($"  {_name} semantic colors shown: {classified, 7:F0} ms");
        report.Add($"  {_name} diagnostics shown: {diagnosed, 7:F0} ms");
        report.Add(
            $"  {_name} ready (colors + diagnostics): {ready, 7:F0} ms after process start, {ready - editorsCreatedMs:F0} ms after editor creation"
        );
    }

    /// <summary>Resident memory of this process and of the server process.</summary>
    public string DescribeMemory()
    {
        using Process host = Process.GetCurrentProcess();
        double hostMiB = host.WorkingSet64 / 1048576.0;
        double? serverMiB = null;
        if (_service.ServerProcessId is { } id)
        {
            try
            {
                using Process server = Process.GetProcessById(id);
                serverMiB = server.WorkingSet64 / 1048576.0;
            }
            catch (ArgumentException) { }
        }
        return serverMiB is { } s
            ? $"memory (RSS): host {hostMiB:F0} MiB + {_name} {s:F0} MiB = {hostMiB + s:F0} MiB"
            : $"memory (RSS): host {hostMiB:F0} MiB, no {_name}";
    }

    private static Task<double> WhenRunningAsync(LspLanguageService service)
    {
        var running = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, _) =>
        {
            if (service.State == LanguageServerState.Running)
                running.TrySetResult(ProcessClock.ElapsedMs);
        };
        if (service.State == LanguageServerState.Running)
            running.TrySetResult(ProcessClock.ElapsedMs);
        return running.Task;
    }
}
