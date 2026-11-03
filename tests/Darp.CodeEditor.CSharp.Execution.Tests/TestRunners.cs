using System.Collections.Concurrent;
using System.Diagnostics;
using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Execution.TestHost;

namespace Darp.CodeEditor.CSharp.Execution.Tests;

internal static class TestRunners
{
    public static Task<CSharpScriptRunner> StartAsync(
        ITestHost? host = null,
        TimeSpan? callTimeout = null,
        TimeSpan? gracePeriod = null,
        Action<string>? output = null
    ) =>
        CSharpScriptRunner.StartAsync(
            new CSharpScriptRunnerOptions
            {
                Environment = TestEnvironment.Create(),
                Services = [CSharpScriptService.Create(host ?? new RecordingHost())],
                CallTimeout = callTimeout ?? TimeSpan.FromSeconds(30),
                CancellationGracePeriod = gracePeriod ?? TimeSpan.FromMilliseconds(300),
                Output = output,
            },
            TestContext.Current.CancellationToken
        );

    /// <summary>A script that returns an <see cref="ITestHandler"/> with the given method bodies.</summary>
    public static string Handler(
        string handle,
        string handleBatch = "return samples.Select(s => s.Channel * 2).ToArray();"
    ) =>
        $$"""
            class Handler : ITestHandler
            {
                private readonly ITestHost _host;
                public Handler(ITestHost host) => _host = host;
                public async Task<int> HandleAsync(Sample sample, CancellationToken cancellationToken)
                {
                    {{handle}}
                }
                public async Task<int[]> HandleBatchAsync(Sample[] samples, CancellationToken cancellationToken)
                {
                    {{handleBatch}}
                }
                public void Dispose() { }
            }
            new Handler(Host)
            """;

    /// <summary>Completes with the next <see cref="CSharpScriptRunner.RunnerExited"/> event.</summary>
    public static Task<ScriptRunnerExitedEventArgs> NextExitAsync(this CSharpScriptRunner runner)
    {
        var exited = new TaskCompletionSource<ScriptRunnerExitedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        runner.RunnerExited += (_, e) => exited.TrySetResult(e);
        return exited.Task;
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static Task<bool> WaitUntilExitedAsync(int processId, TimeSpan timeout) =>
        WaitUntilAsync(() => !IsRunning(processId), timeout);

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start) > timeout)
                return false;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return true;
    }
}

internal sealed class RecordingHost : ITestHost
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

    public Task LogAsync(string message, CancellationToken cancellationToken)
    {
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}
