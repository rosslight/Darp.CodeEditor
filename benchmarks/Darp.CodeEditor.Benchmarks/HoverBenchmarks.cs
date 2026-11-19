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
/// The UI thread's cost of showing the largest part of a hover that the editor builds at a time, in its worst shape: its
/// content's controls built and laid out in a popup 320 pixels wide that scrolls, and the frame after it. The shape: a
/// code block of as many lines, runs of one color, and characters as a part takes (<see cref="HoverParts"/>), the runs
/// alternating between two colors. The frame alone takes about 5 ms of it.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class HoverBenchmarks
{
    private Window? _window;
    private PopupScroller? _content;
    private IReadOnlyList<HoverBlock> _blocks = [];
    private PopupLook? _look;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _content = new PopupScroller();
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
        int lines = HoverParts.MaxLines;
        int runsPerLine = (HoverParts.MaxRuns - (lines - 1)) / lines;
        int runLength = HoverParts.MaxCharacters / (lines * runsPerLine);
        var runs = new List<CodeRun>();
        for (int line = 0; line < lines; line++)
        {
            if (line > 0)
                runs.Add(new CodeRun("\n", null));
            for (int i = 0; i < runsPerLine; i++)
                runs.Add(new CodeRun(new string('x', runLength), colors[i % 2]));
        }
        _blocks = [new HoverCode(runs)];
        // The largest part: it is built at once.
        if (!HoverParts.Fits(HoverParts.Size(_blocks[0])))
            throw new InvalidOperationException("The hover is larger than a part.");
        _look = new PopupLook(new FontFamily("DejaVu Sans Mono"), 14, PopupColors.Default);
    }

    [Benchmark]
    public void ShowTheLargestPart()
    {
        PopupScroller content = _content ?? throw new InvalidOperationException("Not set up.");
        content.Child = HoverView.Create(_blocks, _look ?? throw new InvalidOperationException("Not set up."));
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();
}
#endif
