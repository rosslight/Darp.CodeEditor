using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Measures the first call in each of ten new processes. It changes every job of the run, so that a comparison measures
/// the baseline this way too; a job attribute would add a job of its own instead.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class ColdStartAttribute()
    : JobMutatorConfigBaseAttribute(
        new Job().WithStrategy(RunStrategy.ColdStart).WithLaunchCount(10).WithWarmupCount(0).WithIterationCount(1)
    );
