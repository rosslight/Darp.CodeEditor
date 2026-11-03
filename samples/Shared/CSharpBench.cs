using System.Diagnostics;
using Darp.CodeEditor.LanguageServices;

namespace Darp.CodeEditor.Samples;

/// <summary>Startup milestones and memory of a C# editor on the C# server.</summary>
internal sealed class CSharpBench
{
    private readonly LspLanguageService _service;
    private readonly Task<double> _running;
    private readonly Task<double> _classified;
    private readonly Task<double> _diagnosed;

    /// <summary>Watches the milestones; create it before the first frame.</summary>
    public CSharpBench(LspLanguageService service, TimingLog timings)
    {
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
        report.Add($"  C# server running:          {running, 7:F0} ms after process start (initialize answered)");
        report.Add($"  semantic colors shown:      {classified, 7:F0} ms");
        report.Add($"  diagnostics shown:          {diagnosed, 7:F0} ms");
        report.Add(
            $"  C# ready (colors + diagnostics): {ready, 7:F0} ms after process start, {ready - editorsCreatedMs:F0} ms after editor creation"
        );
    }

    /// <summary>Resident memory of this process and of the C# server process.</summary>
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
            ? $"memory (RSS): host {hostMiB:F0} MiB + C# server {s:F0} MiB = {hostMiB + s:F0} MiB"
            : $"memory (RSS): host {hostMiB:F0} MiB, no C# server";
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
