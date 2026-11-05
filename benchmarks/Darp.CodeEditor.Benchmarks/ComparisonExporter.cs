using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.Common;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Writes one row per benchmark that compares the current version with the baseline. Meant to be read in a pull
/// request; the full BenchmarkDotNet report holds the details.
/// </summary>
internal sealed class ComparisonExporter : ExporterBase
{
    protected override string FileExtension => "md";
    protected override string FileNameSuffix => "-comparison";

    public override void ExportToLog(Summary summary, ILogger logger)
    {
        Comparison[] comparisons =
        [
            .. summary
                .BenchmarksCases.Where(benchmarkCase => !summary.IsBaseline(benchmarkCase))
                .Select(benchmarkCase => Compare(summary, benchmarkCase))
                .OrderBy(comparison => comparison.Benchmark, StringComparer.Ordinal),
        ];

        logger.WriteLine(Summarize(comparisons));
        logger.WriteLine(
            "Time: 95% confidence intervals of the process means, with at least three launches and a 10% margin. Cold-start allocations are not compared."
        );
        logger.WriteLine();
        logger.WriteLine("| Benchmark | Baseline | Current | Change | Time | Allocated |");
        logger.WriteLine("| --- | ---: | ---: | ---: | --- | ---: |");
        foreach (Comparison comparison in comparisons)
            logger.WriteLine(comparison.ToMarkdownRow());
    }

    private Comparison Compare(Summary summary, BenchmarkCase current)
    {
        BenchmarkCase? baseline = summary.GetBaseline(summary.GetLogicalGroupKey(current));
        string name = $"{current.Descriptor.Type.Name}.{current.Descriptor.WorkloadMethod.Name}";
        return new Comparison(
            Benchmark: current.HasParameters ? $"{name} {current.Parameters.DisplayInfo}" : name,
            Baseline: baseline is null ? null : Measurement.From(summary, baseline),
            Current: Measurement.From(summary, current)
        );
    }

    private static string Summarize(Comparison[] comparisons)
    {
        int compared = comparisons.Count(comparison => comparison.IsCompared);
        if (compared == 0)
            return "No benchmark could be compared with the target branch.";
        string benchmarks =
            compared == comparisons.Length
                ? $"{compared} benchmarks"
                : $"{compared} of {comparisons.Length} benchmarks compared";

        int slower = comparisons.Count(comparison => comparison.IsSlower);
        int faster = comparisons.Count(comparison => comparison.IsFaster);
        int allocatingMore = comparisons.Count(comparison => comparison.AllocatesMore);
        int allocatingLess = comparisons.Count(comparison => comparison.AllocatesLess);
        if (slower + faster + allocatingMore + allocatingLess == 0)
            return $"No detected change in {benchmarks}.";

        return $"{benchmarks}: {slower} slower, {faster} faster, "
            + $"{allocatingMore} allocating more, {allocatingLess} allocating less.";
    }

    /// <summary>Mean time and allocation per operation. Null when the benchmark could not be built or run.</summary>
    private sealed record Measurement(double Nanoseconds, double Lower, double Upper, long? AllocatedBytes)
    {
        public static Measurement? From(Summary summary, BenchmarkCase benchmarkCase)
        {
            BenchmarkReport? report = summary[benchmarkCase];
            if (report?.ResultStatistics is null)
                return null;
            // Iterations within one process are not independent observations of process-to-process noise.
            double[] launches = report
                .GetResultRuns()
                .GroupBy(measurement => measurement.LaunchIndex)
                .Select(launch => launch.Average(measurement => measurement.Nanoseconds / measurement.Operations))
                .ToArray();
            var statistics = new Statistics(launches);
            var interval = statistics.GetConfidenceInterval(ConfidenceLevel.L95);
            return new Measurement(
                statistics.Mean,
                launches.Length < 3 ? double.NegativeInfinity : interval.Lower,
                launches.Length < 3 ? double.PositiveInfinity : interval.Upper,
                benchmarkCase.Job.Run.RunStrategy == RunStrategy.ColdStart
                    ? null
                    : report.GcStats.GetBytesAllocatedPerOperation(benchmarkCase)
            );
        }
    }

    private sealed record Comparison(string Benchmark, Measurement? Baseline, Measurement? Current)
    {
        /// <summary>Whether both versions were measured.</summary>
        [MemberNotNullWhen(true, nameof(Baseline), nameof(Current))]
        public bool IsCompared => Baseline is not null && Current is not null;

        public bool IsSlower => IsCompared && Current.Lower > Baseline.Upper * 1.10;
        public bool IsFaster => IsCompared && Current.Upper < Baseline.Lower * 0.90;
        public bool AllocatesMore => Current?.AllocatedBytes > Baseline?.AllocatedBytes;
        public bool AllocatesLess => Current?.AllocatedBytes < Baseline?.AllocatedBytes;

        public string ToMarkdownRow() =>
            $"| {Benchmark} | {FormatTime(Baseline)} | {FormatTime(Current)} | {FormatChange()} | {FormatVerdict()} | {FormatAllocation()} |";

        private string FormatChange()
        {
            if (!IsCompared)
                return "–";
            double percent = (Current.Nanoseconds / Baseline.Nanoseconds - 1) * 100;
            return percent.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
        }

        private string FormatVerdict()
        {
            if (!IsCompared)
                return "no comparison";
            if (IsSlower)
                return "🔴 slower";
            return IsFaster ? "🟢 faster" : "no detected change";
        }

        private string FormatAllocation()
        {
            if (Current is null)
                return "–";
            if (Current.AllocatedBytes is null)
                return "not compared";
            if (Baseline?.AllocatedBytes is null || Baseline.AllocatedBytes == Current.AllocatedBytes)
                return $"{Current.AllocatedBytes} B";
            string marker = AllocatesMore ? "🔴" : "🟢";
            return $"{marker} {Baseline.AllocatedBytes} B → {Current.AllocatedBytes} B";
        }

        private static string FormatTime(Measurement? measurement)
        {
            if (measurement is null)
                return "–";
            (double value, string unit) = measurement.Nanoseconds switch
            {
                < 1_000 => (measurement.Nanoseconds, "ns"),
                < 1_000_000 => (measurement.Nanoseconds / 1_000, "μs"),
                < 1_000_000_000 => (measurement.Nanoseconds / 1_000_000, "ms"),
                _ => (measurement.Nanoseconds / 1_000_000_000, "s"),
            };
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + unit;
        }
    }
}
