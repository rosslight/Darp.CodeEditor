// Left out of the baseline's build while the target branch has no hover: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The UI thread's cost of showing the largest hover that the editor shows, in its worst shape: its content's controls
/// built and laid out in a tooltip 320 pixels wide that scrolls, and the frame after it. The shape: a code block of as
/// many lines, runs of one color, and characters as a hover shows (<see cref="HoverBudget"/>), the runs alternating
/// between two colors. The frame alone takes about 5 ms of it.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class HoverBenchmarks
{
    private Window? _window;
    private ScrollViewer? _content;
    private IReadOnlyList<HoverBlock> _blocks = [];
    private CodeLook? _look;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _window = new Window
        {
            Width = 800,
            Height = 600,
            Content = new Border
            {
                MaxWidth = 320,
                Padding = new Thickness(8),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = _content,
            },
        };
        _window.Show();
        IBrush[] colors = [new ImmutableSolidColorBrush(Colors.Red), new ImmutableSolidColorBrush(Colors.Blue)];
        // Line breaks are runs too, so the lines share the runs left.
        int lines = HoverBudget.MaxLines;
        int runsPerLine = (HoverBudget.MaxRuns - (lines - 1)) / lines;
        int runLength = HoverBudget.MaxCharacters / (lines * runsPerLine);
        var runs = new List<CodeRun>();
        for (int line = 0; line < lines; line++)
        {
            if (line > 0)
                runs.Add(new CodeRun("\n", null));
            for (int i = 0; i < runsPerLine; i++)
                runs.Add(new CodeRun(new string('x', runLength), colors[i % 2]));
        }
        _blocks = [new HoverCode(runs)];
        // The largest that a hover shows: nothing of it is cut.
        if (new HoverBudget().Fit(_blocks) is not [HoverCode { Runs.Count: var count }] || count != runs.Count)
            throw new InvalidOperationException("The hover is larger than a hover shows.");
        _look = new CodeLook(
            new FontFamily("DejaVu Sans Mono"),
            14,
            new ImmutableSolidColorBrush(Colors.White),
            new ImmutableSolidColorBrush(Colors.Black)
        );
    }

    [Benchmark]
    public void ShowTheLargestHover()
    {
        ScrollViewer content = _content ?? throw new InvalidOperationException("Not set up.");
        content.Content = HoverView.Create(_blocks, _look ?? throw new InvalidOperationException("Not set up."));
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();
}
#endif
