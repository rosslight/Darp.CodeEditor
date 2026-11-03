using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Instrumentation;

namespace Darp.CodeEditor.Samples;

internal static class Ui
{
    /// <summary>Switches the app between its dark and light theme variant; editors follow.</summary>
    public static void ToggleThemeVariant()
    {
        Application app = Application.Current ?? throw new InvalidOperationException("No application.");
        app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
    }

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

    /// <summary>
    /// Completes when the editor first shows text, with how many of the shown lines had no colors in that frame. For
    /// a highlighted language, 0 means colors on the first frame.
    /// </summary>
    public static Task<int> LinesWithoutColorsOnFirstFrameAsync(CodeEditor editor)
    {
        var completion = new TaskCompletionSource<int>();
        TextView textView = editor.TextArea.TextView;
        textView.VisualLinesChanged += Check;
        Check(null, EventArgs.Empty);
        return completion.Task;

        void Check(object? sender, EventArgs e)
        {
            if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
                return;
            textView.VisualLinesChanged -= Check;
            completion.TrySetResult(editor.VisibleLinesWithoutHighlighting);
        }
    }

    /// <summary>
    /// Puts the caret at the end of line 1 and the keyboard focus in the editor, activates the window, and writes
    /// where that is on screen, for the input latency probe (see <see cref="E2eTarget"/>). The probe never clicks.
    /// </summary>
    public static void WriteE2eTarget(CodeEditor editor, string path)
    {
        TopLevel topLevel = TopLevel.GetTopLevel(editor) ?? throw new InvalidOperationException("Editor not shown.");
        long window =
            topLevel.TryGetPlatformHandle()?.Handle.ToInt64()
            ?? throw new InvalidOperationException("The window has no native handle.");
        editor.TextArea.ClearSelection();
        editor.CaretOffset = editor.Document.GetLineByNumber(1).EndOffset;
        (topLevel as Window)?.Activate();
        editor.TextArea.Focus();
        TextView textView = editor.TextArea.TextView;
        var end = new TextViewPosition(1, editor.Document.GetLineByNumber(1).Length + 1);
        Point position = textView.GetVisualPosition(end, VisualYPosition.LineTop) - textView.ScrollOffset;
        Point inTopLevel =
            textView.TranslatePoint(position, topLevel) ?? throw new InvalidOperationException("Editor not shown.");
        PixelPoint screen = topLevel.PointToScreen(inTopLevel);
        double scale = topLevel.RenderScaling;
        E2eTarget.Write(
            path,
            window,
            screen.X,
            screen.Y,
            textView.DefaultLineHeight * scale,
            textView.WideSpaceWidth * scale
        );
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
    public async Task<double> WhenFirstAsync(string name)
    {
        await NextAsync(t => t.Name == name);
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
