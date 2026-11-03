using System.Diagnostics;
using Avalonia.Controls;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Samples;

/// <summary>Milliseconds since the process started, for startup milestones.</summary>
internal static class ProcessClock
{
    private static readonly DateTime s_processStart = Process.GetCurrentProcess().StartTime;

    public static double ElapsedMs => (DateTime.Now - s_processStart).TotalMilliseconds;
}

/// <summary>Command line flags shared by the demos.</summary>
/// <param name="CSharpServer">The C# server's .dll or app host, instead of the one next to the app.</param>
/// <param name="DotNet">The dotnet executable that runs the C# server.</param>
/// <param name="ServerLog">Writes the C# server's log to standard error.</param>
internal sealed record DemoOptions(
    bool Smoke,
    bool Bench,
    bool Hold,
    bool Scene,
    bool NoLanguageService,
    bool SkipHighlightingBench,
    string? OutputPath,
    string? CSharpServer = null,
    string? DotNet = null,
    bool ServerLog = false
)
{
    public bool Measure => Smoke || Bench;

    public static DemoOptions Parse(string[] args) =>
        new(
            args.Contains("--smoke"),
            args.Contains("--bench"),
            args.Contains("--hold"),
            args.Contains("--scene"),
            args.Contains("--no-language-service"),
            args.Contains("--skip-highlighting-bench"),
            Value(args, "--out"),
            Value(args, "--csharp-server"),
            Value(args, "--dotnet"),
            args.Contains("--server-log")
        );

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal static class Ui
{
    public static Task NextFrameAsync(TopLevel topLevel)
    {
        var completion = new TaskCompletionSource();
        topLevel.RequestAnimationFrame(_ => completion.SetResult());
        return completion.Task;
    }

    /// <summary>Completes after the editor next rebuilt its visual lines, i.e. right before they are rendered.</summary>
    public static Task NextLayoutAsync(CodeEditor editor)
    {
        var completion = new TaskCompletionSource();
        TextView textView = editor.TextArea.TextView;
        textView.VisualLinesChanged += OnChanged;
        return completion.Task;

        void OnChanged(object? sender, EventArgs e)
        {
            textView.VisualLinesChanged -= OnChanged;
            completion.TrySetResult();
        }
    }

    /// <summary>True when the rendered lines use more than one text color, i.e. highlighting is visible.</summary>
    public static bool IsHighlighted(CodeEditor editor)
    {
        TextView textView = editor.TextArea.TextView;
        if (!textView.VisualLinesValid)
            return false;
        object? first = null;
        foreach (VisualLine line in textView.VisualLines)
        {
            foreach (VisualLineElement element in line.Elements)
            {
                object brush = element.TextRunProperties.ForegroundBrush;
                first ??= brush;
                if (!ReferenceEquals(first, brush))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Completes with <see cref="ProcessClock.ElapsedMs"/> when the editor first shows text (and colors).</summary>
    public static Task<double> WaitForVisibleAsync(CodeEditor editor, bool highlighted)
    {
        var completion = new TaskCompletionSource<double>();
        TextView textView = editor.TextArea.TextView;
        textView.VisualLinesChanged += Check;
        Check(null, EventArgs.Empty);
        return completion.Task;

        void Check(object? sender, EventArgs e)
        {
            if (
                !textView.VisualLinesValid
                || textView.VisualLines.Count == 0
                || (highlighted && !IsHighlighted(editor))
            )
                return;
            textView.VisualLinesChanged -= Check;
            completion.TrySetResult(ProcessClock.ElapsedMs);
        }
    }

    /// <summary>How long the UI thread spent handling each typed character, in ms (input handling only, not layout).</summary>
    public static List<double> KeystrokeMs { get; } = [];

    /// <summary>Types text the way the keyboard does, one character at a time.</summary>
    public static async Task TypeAsync(CodeEditor editor, string text, int delayMs)
    {
        foreach (char c in text)
        {
            long start = Stopwatch.GetTimestamp();
            editor.TextArea.PerformTextInput(c.ToString());
            KeystrokeMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            await Task.Delay(delayMs);
        }
    }

    /// <summary>Summarizes and clears <see cref="KeystrokeMs"/>.</summary>
    public static string TakeKeystrokeSummary()
    {
        double[] sorted = [.. KeystrokeMs.Order()];
        KeystrokeMs.Clear();
        return sorted.Length == 0
            ? "no keystrokes"
            : $"keystroke handling: n {sorted.Length}, p50 {sorted[sorted.Length / 2]:F2} ms, p99 {sorted[(int)(sorted.Length * 0.99)]:F2} ms, max {sorted[^1]:F2} ms";
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

/// <summary>Counts editor timings by name while a phase runs.</summary>
internal sealed class TimingLog : IDisposable
{
    private readonly CodeEditor _editor;
    private readonly List<EditorTiming> _timings = [];

    public TimingLog(CodeEditor editor)
    {
        _editor = editor;
        _timings.AddRange(editor.StartupTimings);
        editor.Timing += OnTiming;
    }

    public event Action<EditorTiming>? Recorded;

    public IReadOnlyList<EditorTiming> Timings => _timings;

    public string Summary() =>
        string.Join(
            ", ",
            _timings
                .GroupBy(t => t.Name)
                .Select(g =>
                    $"{g.Key} x{g.Count()} (p50 {Percentile(g, 0.5):F0} ms, max {g.Max(t => t.Duration.TotalMilliseconds):F0} ms)"
                )
        );

    public void Dispose() => _editor.Timing -= OnTiming;

    /// <summary>Completes with the next timing that matches.</summary>
    public Task<EditorTiming> NextAsync(Func<EditorTiming, bool> match)
    {
        var completion = new TaskCompletionSource<EditorTiming>();
        Recorded += OnRecorded;
        return completion.Task;

        void OnRecorded(EditorTiming timing)
        {
            if (!match(timing))
                return;
            Recorded -= OnRecorded;
            completion.TrySetResult(timing);
        }
    }

    /// <summary>Completes with <see cref="ProcessClock.ElapsedMs"/> when the first timing of that name arrives.</summary>
    public Task<double> WhenFirstAsync(string name) => WhenFirstAsync(t => t.Name == name);

    /// <summary>Completes with <see cref="ProcessClock.ElapsedMs"/> when the first matching timing arrives.</summary>
    public async Task<double> WhenFirstAsync(Func<EditorTiming, bool> match)
    {
        await NextAsync(match);
        return ProcessClock.ElapsedMs;
    }

    public static double Percentile(IEnumerable<EditorTiming> timings, double percentile)
    {
        double[] sorted = [.. timings.Select(t => t.Duration.TotalMilliseconds).Order()];
        return sorted.Length == 0
            ? 0
            : sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(percentile * sorted.Length) - 1)];
    }

    private void OnTiming(object? sender, EditorTiming timing)
    {
        _timings.Add(timing);
        Recorded?.Invoke(timing);
    }
}
