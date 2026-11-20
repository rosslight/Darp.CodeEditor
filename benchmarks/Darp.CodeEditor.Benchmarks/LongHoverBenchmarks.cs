// Left out of the baseline's build while the target branch has no hover built in parts: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The UI thread's cost of a long hover, shown in full: about 210,000 characters in 11,000 runs, 300 paragraphs of
/// styled text and a code block of 3,000 lines in two colors, in a popup of the hover's least limits, 750 by 250
/// pixels, that scrolls. Opening it builds and lays out its first part; scrolling it by a step renders the next frame;
/// building it in full lays out and renders all of it.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class LongHoverBenchmarks
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
            Width = 1000,
            Height = 800,
            Content = new Border
            {
                MaxWidth = 750,
                MaxHeight = 250,
                Padding = new Thickness(8, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = _content,
            },
        };
        _window.Show();
        _blocks = LongHover();
        _look = new PopupLook(
            new FontFamily("DejaVu Sans Mono"),
            14,
            PopupColors.Default,
            FontFamily.Default,
            14,
            PopupColors.Default.HoverForeground
        )
        {
            Selectable = true,
        };
    }

    /// <summary>The long hover's blocks: its paragraphs, then its code block.</summary>
    private static IReadOnlyList<HoverBlock> LongHover()
    {
        var blocks = new List<HoverBlock>();
        for (int i = 0; i < 300; i++)
        {
            blocks.Add(
                new HoverParagraph([
                    new HoverSpan($"Paragraph {i} explains the parameter "),
                    new HoverSpan("value", SpanStyle.Code),
                    new HoverSpan(" and what the call does with it, "),
                    new HoverSpan("in detail", SpanStyle.Bold),
                    new HoverSpan(
                        ", over several lines of prose that wrap at the popup's width, as documentation does. "
                    ),
                    new HoverSpan("See the example below for how it reads in code, and the notes after it."),
                ])
            );
        }
        IBrush[] colors = [new ImmutableSolidColorBrush(Colors.Red), new ImmutableSolidColorBrush(Colors.Blue)];
        var runs = new List<CodeRun>();
        for (int line = 0; line < 3_000; line++)
        {
            runs.Add(new CodeRun(line == 0 ? "result" : "\nresult", colors[0]));
            runs.Add(new CodeRun($" = compute({line}, ", colors[1]));
            runs.Add(new CodeRun("options)", colors[0]));
        }
        blocks.Add(new HoverCode(runs));
        return blocks;
    }

    [IterationSetup(Target = nameof(ScrollALongHover))]
    public void Build()
    {
        Show();
        HeadlessEditor.Render(Window);
        BuildInFull();
    }

    [IterationSetup(Target = nameof(OpenALongHover))]
    public void Clear()
    {
        Content.Child = null;
        HeadlessEditor.Render(Window);
    }

    /// <summary>
    /// The long hover's first part, from its blocks, built and laid out as it opens: the frame is left out, as the
    /// headless platform builds the parts of the frames after it in the same go.
    /// </summary>
    [Benchmark]
    public void OpenALongHover()
    {
        Show();
        Window.UpdateLayout();
    }

    /// <summary>A step of the wheel in the long hover, built in full, and the frame after it.</summary>
    [Benchmark]
    public void ScrollALongHover()
    {
        Content.ScrollBy(40);
        HeadlessEditor.Render(Window);
    }

    /// <summary>The long hover from its blocks until all of it is laid out and rendered, whatever the frames.</summary>
    [Benchmark]
    public void BuildALongHover()
    {
        Show();
        HeadlessEditor.Render(Window);
        BuildInFull();
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private Window Window => _window ?? throw new InvalidOperationException("Not set up.");

    private PopupScroller Content => _content ?? throw new InvalidOperationException("Not set up.");

    private void Show() =>
        Content.Child = HoverView.Create(_blocks, _look ?? throw new InvalidOperationException("Not set up."));

    /// <summary>Builds the parts that frames did not build yet, and renders the frame after them.</summary>
    private void BuildInFull()
    {
        var content = (HoverContent)(Content.Child ?? throw new InvalidOperationException("Not shown."));
        while (!content.IsComplete)
            content.AddNextPart();
        HeadlessEditor.Render(Window);
    }
}
#endif
