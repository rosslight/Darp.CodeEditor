// Left out of the baseline's build while the target branch has no squiggles: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The cost of the largest batch of diagnostics that editors take (<see cref="DiagnosticsBatch.MaxShown"/>), each step
/// in its worst shape: preparing it on the server's thread, taking it in on the UI thread, drawing it with every
/// squiggle on screen, apart, in a window of a 4K screen, and finding what a hover shows. A frame on the headless
/// platform also rasterizes it, which an app does on its render thread: the frame without diagnostics tells what the
/// squiggles add.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class DiagnosticBatchBenchmarks
{
    /// <summary>Lines that the view shows whole: 10,000 characters with a gap after each.</summary>
    private const int Lines = 50;

    private const int LineLength = 400;

    private CodeDocument? _document;
    private Window? _window;
    private TextView? _textView;
    private SquiggleLayer? _layer;
    private SquiggleLayer? _drawn;
    private IReadOnlyList<Diagnostic>[] _batches = [];
    private int _next;

    /// <summary>The offset that the pointer rests on, on line 31: past where the errors on it start.</summary>
    private readonly int _hovered = 30 * (LineLength + 1) + 10;

    /// <summary>
    /// Edits that mapping each diagnostic follows: on the server's thread, those made since the text that the
    /// diagnostics are about; on the UI thread, those made since the server's thread mapped them.
    /// </summary>
    [Params(0, 100)]
    public int EditsSince { get; set; }

    /// <summary>Two batches of one-character errors two characters apart, so that no two merge.</summary>
    [GlobalSetup(
        Targets = [nameof(PrepareTheLargestBatch), nameof(DrawTheLargestBatch), nameof(DrawAFrameWithTheLargestBatch)]
    )]
    public void SetUpApart()
    {
        TextSnapshot snapshot = Show();
        _batches = [Apart(snapshot, 0), Apart(snapshot, 1)];
        Edit();
        _document?.SetDiagnostics(_batches[0]);
        Dispatcher.UIThread.RunJobs();
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        TextView textView = _textView ?? throw new InvalidOperationException("Not set up.");
        // Drawn in the theme's colors, once they are loaded.
        _drawn = textView.Layers.OfType<SquiggleLayer>().Single();
        long start = Stopwatch.GetTimestamp();
        while (Draw(_drawn) == 0)
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("The editor draws no squiggles.");
            HeadlessEditor.Render(window);
        }
        HeadlessEditor.Render(window);
    }

    /// <summary>The document alone: it takes a batch in for its editors whether one shows it or not.</summary>
    [GlobalSetup(Target = nameof(TakeTheLargestBatch))]
    public void SetUpAlone()
    {
        HeadlessEditor.SetUpApp();
        _document = CodeDocument.Create(CodeLanguage.PlainText, Text());
        _batches = [Apart(_document.CurrentSnapshot, 0), Apart(_document.CurrentSnapshot, 1)];
    }

    [GlobalSetup(Target = nameof(DrawAFrameWithoutDiagnostics))]
    public void SetUpWithout() => Show();

    /// <summary>
    /// Errors that all hold the hovered character, each found later in the search coming earlier in a hover: every one
    /// beats those kept before it.
    /// </summary>
    [GlobalSetup(Target = nameof(FindWhatAHoverShowsOfTheLargestBatch))]
    public void SetUpOnOneCharacter()
    {
        TextSnapshot snapshot = Show();
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        document.SetDiagnostics([
            .. Enumerable
                .Range(0, DiagnosticsBatch.MaxShown)
                .Select(i => new Diagnostic(
                    snapshot,
                    TextRange.FromBounds(DiagnosticsBatch.MaxShown - i, _hovered + 1),
                    DiagnosticSeverity.Error,
                    null,
                    $"Error {i}"
                )),
        ]);
        Edit();
        Dispatcher.UIThread.RunJobs();
        _layer = new SquiggleLayer(
            (_window ?? throw new InvalidOperationException("Not set up."))
                .GetVisualDescendants()
                .OfType<AvaloniaEdit.TextEditor>()
                .Single()
                .TextArea,
            document
        );
    }

    /// <summary>On the server's thread: choosing the diagnostics shown, and mapping each one to the latest text.</summary>
    [Benchmark]
    public object PrepareTheLargestBatch() =>
        new DiagnosticsBatch(
            _batches[0],
            (_document ?? throw new InvalidOperationException("Not set up.")).CurrentSnapshot
        );

    /// <summary>
    /// A batch arrives from the server's thread, prepared, and waits for the UI thread, while the user edits.
    /// </summary>
    [IterationSetup(Target = nameof(TakeTheLargestBatch))]
    public void Arrive()
    {
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        IReadOnlyList<Diagnostic> batch = _batches[_next ^= 1];
        Task.Run(() => document.SetDiagnostics(batch)).Wait();
        Edit();
    }

    /// <summary>On the UI thread: keeping each diagnostic where it follows the text, for the document's editors.</summary>
    [Benchmark]
    public void TakeTheLargestBatch() => Dispatcher.UIThread.RunJobs();

    /// <summary>What the squiggles cost the UI thread in a frame: finding where they go, and their shapes.</summary>
    [Benchmark]
    public int DrawTheLargestBatch() => Draw(_drawn ?? throw new InvalidOperationException("Not set up."));

    [Benchmark]
    public void DrawAFrameWithTheLargestBatch() => DrawAFrame();

    [Benchmark]
    public void DrawAFrameWithoutDiagnostics() => DrawAFrame();

    [Benchmark]
    public int FindWhatAHoverShowsOfTheLargestBatch()
    {
        SquiggleLayer layer = _layer ?? throw new InvalidOperationException("Not set up.");
        return HoverBlocks.Of(layer.At(_hovered, HoverBudget.MaxLines), new HoverBudget()).Count;
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    /// <summary>A frame of the editor, its lines built and drawn again.</summary>
    private void DrawAFrame()
    {
        (_textView ?? throw new InvalidOperationException("Not set up.")).Redraw();
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    /// <summary>Draws <paramref name="layer"/> on its own; the shapes that it drew.</summary>
    private static int Draw(SquiggleLayer layer)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext context = drawing.Open())
            layer.Render(context);
        return drawing.Children.Count;
    }

    /// <summary>
    /// An editor of the lines, all of them in view, in a window of a 4K screen; its text's snapshot, which the
    /// diagnostics are about.
    /// </summary>
    private TextSnapshot Show()
    {
        HeadlessEditor.SetUpApp();
        var document = CodeDocument.Create(CodeLanguage.PlainText, Text());
        var editor = new CodeEditor { Document = document };
        _document = document;
        _window = new Window
        {
            Width = 3840,
            Height = 2160,
            Content = editor,
        };
        _window.Show();
        HeadlessEditor.Render(_window);
        TextView textView = editor.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single().TextArea.TextView;
        _textView = textView;
        if (
            textView.DocumentHeight > textView.Bounds.Height
            || LineLength * textView.WideSpaceWidth > textView.Bounds.Width
        )
        {
            throw new InvalidOperationException("The view does not show all of the text.");
        }
        return document.CurrentSnapshot;
    }

    private static string Text() => string.Concat(Enumerable.Repeat(new string('x', LineLength) + "\n", Lines));

    /// <summary><see cref="EditsSince"/> edits at the end of the text.</summary>
    private void Edit()
    {
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        for (int i = 0; i < EditsSince; i++)
            document.Replace(new TextRange(document.CurrentSnapshot.Length, 0), "y");
    }

    private static IReadOnlyList<Diagnostic> Apart(TextSnapshot snapshot, int first) =>
        [
            .. Enumerable
                .Range(0, Lines)
                .SelectMany(line =>
                    Enumerable.Range(0, LineLength / 2).Select(i => line * (LineLength + 1) + first + 2 * i)
                )
                .Take(DiagnosticsBatch.MaxShown)
                .Select(offset => new Diagnostic(
                    snapshot,
                    new TextRange(offset, 1),
                    DiagnosticSeverity.Error,
                    null,
                    "An error."
                )),
        ];
}
#endif
