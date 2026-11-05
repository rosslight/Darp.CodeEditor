using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

// Fails loudly: a benchmark that throws, or a run that measured nothing, makes the exit code 1.
Summary[] summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray();
bool failed =
    summaries.Length == 0
    || summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Length == 0 || s.Reports.Any(r => !r.Success));
if (failed)
    Console.Error.WriteLine("Benchmarks failed.");
return failed ? 1 : 0;
