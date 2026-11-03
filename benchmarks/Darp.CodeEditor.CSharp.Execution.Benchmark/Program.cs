using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Darp.CodeEditor.CSharp.Execution;
using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Execution.TestHost;

// Measures the script runner from this process: cold start to the first handler result, and warm calls.
// Run it as a JIT app (dotnet run -c Release) and published with NativeAOT; see the README.
//   --runs <n>      cold starts to measure (default 5)
//   --label <name>  names the report (default: the host kind)
//   --out <dir>     report directory (default: measurements/csharp-runner)
int runs = GetOption("--runs") is { } runsText ? int.Parse(runsText, CultureInfo.InvariantCulture) : 5;
string host = RuntimeFeature() ? "JIT" : "NativeAOT";
string label = GetOption("--label") ?? host.ToLowerInvariant();
string outDirectory = GetOption("--out") ?? Path.Combine("measurements", "csharp-runner");
bool bundledRuntime = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "helpers", "dotnet"));

const string Script = """
    class Handler : ITestHandler
    {
        private readonly ITestHost _host;
        public Handler(ITestHost host) => _host = host;
        public async Task<int> HandleAsync(Sample sample, CancellationToken cancellationToken) =>
            sample.Channel < 0 ? await _host.AddAsync(sample.Channel, sample.Payload.Length, cancellationToken) : sample.Payload.Length;
        public Task<int[]> HandleBatchAsync(Sample[] samples, CancellationToken cancellationToken)
        {
            var results = new int[samples.Length];
            for (int i = 0; i < samples.Length; i++)
                results[i] = samples[i].Payload.Length;
            return Task.FromResult(results);
        }
        public void Dispose() { }
    }
    new Handler(Host)
    """;

var report = new StringBuilder();
report.AppendLine($"# C# script runner: {label}");
report.AppendLine();
report.AppendLine(
    $"{host} host, runner on {(bundledRuntime ? "the bundled runtime (ReadyToRun)" : "the installed runtime (build output)")}, "
        + $"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, "
        + $"{DateTime.UtcNow:yyyy-MM-dd}."
);
report.AppendLine();

// Cold starts: a fresh runner each time; the clock starts right before the runner process starts.
var starts = new List<(double Ready, double Evaluated, double FirstResult, double RssMiB)>();
for (int run = 0; run < runs; run++)
{
    long start = Stopwatch.GetTimestamp();
    await using CSharpScriptRunner runner = await StartAsync();
    double ready = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(Script);
    double evaluated = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    await handler.HandleAsync(new Sample(1, new byte[100]));
    double firstResult = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    using Process process = Process.GetProcessById(runner.ProcessId ?? throw new InvalidOperationException());
    starts.Add((ready, evaluated, firstResult, process.WorkingSet64 / 1048576.0));
    Console.WriteLine($"cold start {run + 1}/{runs}: first result after {firstResult:F0} ms");
}
report.AppendLine($"Cold start, {runs} fresh runners, ms: median [min–max]");
report.AppendLine();
report.AppendLine("| Step | Value |");
report.AppendLine("| --- | ---: |");
report.AppendLine(
    $"| runner process start → runner ready (environment loaded) | {Summary(starts.Select(s => s.Ready))} |"
);
report.AppendLine($"| → script evaluated, handler proxy received | {Summary(starts.Select(s => s.Evaluated))} |");
report.AppendLine($"| → first handler result | {Summary(starts.Select(s => s.FirstResult))} |");
report.AppendLine($"| runner RSS after the first result, MiB | {Summary(starts.Select(s => s.RssMiB))} |");
report.AppendLine();

// Warm calls on one runner.
await using (CSharpScriptRunner runner = await StartAsync())
{
    using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(Script);
    var small = new Sample(1, new byte[100]);
    var nested = new Sample(-1, new byte[100]);
    Sample[] batch = [.. Enumerable.Repeat(small, 100)];
    for (int i = 0; i < 5_000; i++)
        await handler.HandleAsync(i % 2 == 0 ? small : nested);
    for (int i = 0; i < 500; i++)
        await handler.HandleBatchAsync(batch);

    report.AppendLine("Warm calls: per call or batch, µs p50 / p99; rate in calls/s or packets/s");
    report.AppendLine();
    report.AppendLine("| Workload | p50 | p99 | Rate |");
    report.AppendLine("| --- | ---: | ---: | ---: |");
    report.AppendLine(
        await MeasureAsync("small call, 100 bytes, 100k sequential", 100_000, 1, () => handler.HandleAsync(small))
    );
    report.AppendLine(
        await MeasureAsync("call with a callback into the host, 10k", 10_000, 1, () => handler.HandleAsync(nested))
    );
    report.AppendLine(
        await MeasureAsync("batch of 100 × 100 bytes, 1k batches", 1_000, 100, () => handler.HandleBatchAsync(batch))
    );

    // ScriptBatcher: 100k packets submitted as fast as possible, at most 4096 waiting.
    await using var batcher = new ScriptBatcher<Sample, int>(samples => handler.HandleBatchAsync(samples));
    long burstStart = Stopwatch.GetTimestamp();
    var pending = new Queue<Task<int>>();
    for (int i = 0; i < 100_000; i++)
    {
        pending.Enqueue(batcher.SubmitAsync(small));
        if (pending.Count >= 4096)
            await pending.Dequeue();
    }
    while (pending.Count > 0)
        await pending.Dequeue();
    double seconds = Stopwatch.GetElapsedTime(burstStart).TotalSeconds;
    report.AppendLine($"| ScriptBatcher, 100k single packets | – | – | {100_000 / seconds:N0} |");
}

Directory.CreateDirectory(outDirectory);
string path = Path.Combine(outDirectory, $"{label}.md");
File.WriteAllText(path, report.ToString());
Console.WriteLine();
Console.WriteLine(report);
Console.WriteLine($"Written to {path}");
return;

static Task<CSharpScriptRunner> StartAsync() =>
    CSharpScriptRunner.StartAsync(
        new CSharpScriptRunnerOptions
        {
            Environment = TestEnvironment.Create(),
            Services = [CSharpScriptService.Create<ITestHost>(new BenchmarkHost())],
        }
    );

static async Task<string> MeasureAsync(string name, int count, int perCall, Func<Task> call)
{
    var times = new double[count];
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < count; i++)
    {
        long callStart = Stopwatch.GetTimestamp();
        await call();
        times[i] = Stopwatch.GetElapsedTime(callStart).TotalMicroseconds;
    }
    double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    Array.Sort(times);
    return $"| {name} | {times[count / 2]:F1} | {times[(int)(count * 0.99)]:F1} | {count * perCall / seconds:N0} |";
}

static string Summary(IEnumerable<double> values)
{
    double[] sorted = [.. values.Order()];
    return $"{sorted[sorted.Length / 2]:F0} [{sorted[0]:F0}–{sorted[^1]:F0}]";
}

static bool RuntimeFeature() => System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

string? GetOption(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

internal sealed class BenchmarkHost : ITestHost
{
    public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);

    public Task LogAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
}
