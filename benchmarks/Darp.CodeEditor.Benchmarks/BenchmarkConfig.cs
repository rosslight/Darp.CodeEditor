using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
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

        StatisticalTestColumn timeVerdict = StatisticalTestColumn.CreateDefault();
        return DefaultConfig
            .Instance.AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(
                Job.Default.WithMsBuildArguments($"/p:DarpCodeEditorBaselineVersion={baselineVersion}")
                    .WithId("Baseline")
                    .AsBaseline()
            )
            .AddJob(Job.Default.WithId("Current"))
            .AddColumn(timeVerdict)
            .AddExporter(new ComparisonExporter(timeVerdict))
            .HideColumns(Column.Arguments)
            .WithOrderer(new PairedJobsOrderer());
    }
}
