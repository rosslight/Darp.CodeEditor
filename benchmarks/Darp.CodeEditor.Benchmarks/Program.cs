using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Darp.CodeEditor.Benchmarks;

// Fails loudly: a benchmark that throws, or a run that measured nothing, makes the exit code 1. Only a baseline may
// fail: the target branch may lack what the benchmarks use, and the comparison says so. Listing the benchmarks
// measures nothing on purpose.
Summary[] summaries = BenchmarkSwitcher
    .FromAssembly(typeof(Program).Assembly)
    .Run(args, BenchmarkConfig.Create())
    .ToArray();
bool listed = args.Contains("--list");
bool failed =
    (summaries.Length == 0 && !listed)
    || summaries.Any(s =>
        s.HasCriticalValidationErrors
        || s.Reports.Length == 0
        || s.Reports.Any(r => !r.Success && !r.BenchmarkCase.Job.Meta.Baseline)
    );
if (failed)
    Console.Error.WriteLine("Benchmarks failed.");
return failed ? 1 : 0;
