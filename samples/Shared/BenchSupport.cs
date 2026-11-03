using System.Diagnostics;
using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Samples;

/// <summary>Milliseconds since the process started, for startup milestones.</summary>
internal static class ProcessClock
{
    private static readonly DateTime s_processStart = Process.GetCurrentProcess().StartTime;

    public static double ElapsedMs => (DateTime.Now - s_processStart).TotalMilliseconds;
}

/// <summary>Command line flags shared by the demos.</summary>
internal sealed record DemoOptions(
    bool Smoke,
    bool Bench,
    bool Hold,
    bool Scene,
    bool NoLanguageService,
    bool SkipHighlightingBench,
    bool RunScript,
    string? OutputPath,
    string? E2eOutput,
    bool Light
)
{
    public bool Measure => Smoke || Bench;

    public static DemoOptions Parse(string[] args)
    {
        return new DemoOptions(
            args.Contains("--smoke"),
            args.Contains("--bench"),
            args.Contains("--hold"),
            args.Contains("--scene"),
            args.Contains("--no-language-service"),
            args.Contains("--skip-highlighting-bench"),
            args.Contains("--run-script"),
            Value(args, "--out"),
            Value(args, "--e2e"),
            args.Contains("--light")
        );
    }

    private static string? Value(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

/// <summary>Collects result lines, prints them and saves them to a file.</summary>
internal sealed class BenchReport(string? outputPath)
{
    private readonly string? _outputPath = outputPath;
    private readonly List<string> _lines = [];

    public void Add(string line)
    {
        _lines.Add(line);
        Console.WriteLine(line);
    }

    public void Add(StallReport report) => Add(report.ToString());

    public void Save()
    {
        if (_outputPath is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_outputPath)) ?? ".");
        File.WriteAllLines(_outputPath, _lines);
    }
}
