using Darp.CodeEditor.CSharp.Execution;
using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Execution.TestHost;

// run:  evaluates a script that calls back into this app, prints its result and exits.
// hold: starts a script that never returns, prints the runner's process ID and waits to be killed.
string mode = args.FirstOrDefault() ?? "run";
await using CSharpScriptRunner runner = await CSharpScriptRunner.StartAsync(
    new CSharpScriptRunnerOptions
    {
        Environment = TestEnvironment.Create(),
        Services = [CSharpScriptService.Create<ITestHost>(new Host())],
        CallTimeout = Timeout.InfiniteTimeSpan,
    }
);
using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
    """
    class Handler : ITestHandler
    {
        private readonly ITestHost _host;
        public Handler(ITestHost host) => _host = host;
        public async Task<int> HandleAsync(Sample sample, CancellationToken cancellationToken)
        {
            if (sample.Channel < 0) { while (true) { } }
            return await _host.AddAsync(sample.Channel, sample.Payload.Length, cancellationToken);
        }
        public Task<int[]> HandleBatchAsync(Sample[] samples, CancellationToken cancellationToken) =>
            Task.FromResult(samples.Select(s => s.Payload.Length).ToArray());
        public void Dispose() { }
    }
    new Handler(Host)
    """
);
if (mode == "hold")
{
    _ = handler.HandleAsync(new Sample(-1, []));
    Console.WriteLine($"runner {runner.ProcessId}");
    await Task.Delay(Timeout.Infinite);
}
int result = await handler.HandleAsync(new Sample(40, new byte[2]));
int[] batch = await handler.HandleBatchAsync([new Sample(1, new byte[3]), new Sample(2, new byte[4])]);
Console.WriteLine($"result {result} batch {string.Join(',', batch)}");

internal sealed class Host : ITestHost
{
    public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

    public Task LogAsync(string message, CancellationToken cancellationToken)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }
}
