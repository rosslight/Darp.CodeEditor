using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;

namespace Darp.CodeEditor.Benchmarks;

internal static class BenchmarkConfig
{
    /// <summary>
    /// Version of the baseline library, packed into the benchmark-baseline source of nuget.config. When unset, only the
    /// working tree is benchmarked.
    /// </summary>
    private const string BaselineVersionVariable = "DARP_CODEEDITOR_BASELINE_VERSION";

    public static IConfig Create()
    {
        string? baselineVersion = Environment.GetEnvironmentVariable(BaselineVersionVariable);
        if (string.IsNullOrEmpty(baselineVersion))
            return DefaultConfig.Instance;

        return DefaultConfig
            .Instance.AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(
                Job.Default.WithLaunchCount(3)
                    .WithMsBuildArguments($"/p:DarpCodeEditorBaselineVersion={baselineVersion}")
                    .WithId("Baseline")
                    .AsBaseline()
            )
            .AddJob(Job.Default.WithLaunchCount(3).WithId("Current"))
            .AddColumn(StatisticalTestColumn.CreateDefault())
            .AddExporter(new ComparisonExporter())
            .AddFilter(
                new SimpleFilter(benchmarkCase =>
                    !benchmarkCase.Job.Meta.Baseline
                    || !benchmarkCase.Descriptor.Type.IsDefined(typeof(NotInBaselineAttribute), inherit: false)
                )
            )
            .HideColumns(Column.Arguments)
            .WithOrderer(new PairedJobsOrderer());
    }
}
