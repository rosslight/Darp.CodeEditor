// Left out of the baseline's build while the target branch has no semantic tokens: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The UI thread's cost of the most semantic tokens a server can put on the lines in view: a token on every character
/// of lines as long as the shown part, of two types in two colors that take turns, so that every token is a run of
/// text of its own; taken and drawn in the frame after it. Each call takes the other colors, so that every line in
/// view draws again.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class DenseTokenBenchmarks
{
    private const int Lines = 60;

    private static readonly SemanticTokensLegend s_legend = new(["class", "variable"], []);

    private Window? _window;
    private SemanticHighlighter? _highlighter;
    private SemanticHighlighter.Answer[] _answers = [];
    private int _next;

    /// <summary>The length of each line, and the number of its tokens.</summary>
    [Params(300, VisibleText.LineLimit)]
    public int Width { get; set; }

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        string text = string.Join("\n", Enumerable.Repeat(new string('x', Width), Lines));
        var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, text) };
        Window window = HeadlessEditor.Show(editor);
        _window = window;
        HeadlessEditor.Render(window);
        SemanticHighlighter highlighter =
            editor.SemanticHighlighter ?? throw new InvalidOperationException("The editor shows no document.");
        _highlighter = highlighter;
        _answers = [Answer(0), Answer(1)];
        highlighter.Show(_answers[0]);
        // Drawn in the theme's colors, once they are loaded: a run for each colored token.
        TextView textView = editor.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single().TextArea.TextView;
        long start = Stopwatch.GetTimestamp();
        while (
            !textView.VisualLinesValid
            || textView.VisualLines[0].TextLines.Sum(row => row.TextRuns.Count) < SemanticHighlighter.MaxColoredTokens
        )
        {
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("The editor draws no semantic colors.");
            HeadlessEditor.Render(window);
        }
    }

    [Benchmark]
    public void TakeTokensOnEveryCharacterAndDraw()
    {
        _next ^= 1;
        (_highlighter ?? throw new InvalidOperationException("Not set up.")).Show(_answers[_next]);
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    /// <summary>A token on every character, the first of the type <paramref name="first"/>, the next of the other.</summary>
    private SemanticHighlighter.Answer Answer(int first)
    {
        int[] line = [.. Enumerable.Range(0, Width).SelectMany(column => (int[])[column, 1, (column + first) % 2, 0])];
        return SemanticHighlighter.Prepare(new SemanticTokenLines(s_legend, 0, [.. Enumerable.Repeat(line, Lines)]), 0);
    }
}
#endif
