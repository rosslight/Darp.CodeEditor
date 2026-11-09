// Left out of the baseline's build while the target branch has no semantic tokens: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Threading;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The UI thread's cost of a server's semantic tokens in 5,000 lines of JSON with a warning under every key: taking
/// the tokens of the whole text, as a server without ranges sends them, and the frame after it, with its squiggles.
/// Each call takes other tokens than the last, so that every line in view draws again.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class SemanticTokenBenchmarks
{
    private const int Lines = 5000;

    private Window? _window;
    private SemanticHighlighter? _highlighter;
    private SemanticTokenLines[] _tokens = [];
    private int _next;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        CodeDocument document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(Lines));
        var editor = new CodeEditor { Document = document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        // The keys of the lines between the braces: "  \"key{i}\": …".
        TextSnapshot snapshot = document.CurrentSnapshot;
        document.SetDiagnostics([
            .. Enumerable
                .Range(2, Lines - 3)
                .Select(i => new Diagnostic(
                    snapshot,
                    new TextRange(snapshot.GetOffset(new TextPosition(i, 3)), $"\"key{i}\"".Length),
                    DiagnosticSeverity.Warning,
                    null,
                    "A warning."
                )),
        ]);
        Dispatcher.UIThread.RunJobs();
        _highlighter = editor.SemanticHighlighter;
        _tokens = [Tokens(type: 0), Tokens(type: 1)];
    }

    [Benchmark]
    public void TakeTokensOfTheWholeTextAndDraw()
    {
        SemanticHighlighter highlighter = _highlighter ?? throw new InvalidOperationException("Not set up.");
        highlighter.Show(_tokens[_next]);
        _next ^= 1;
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    /// <summary>A token of <paramref name="type"/> on each key, and one on each value, as a server sends them.</summary>
    private static SemanticTokenLines Tokens(int type)
    {
        var legend = new SemanticTokensLegend(["property", "variable"], ["readonly"]);
        var lines = new int[Lines][];
        for (int i = 0; i < Lines; i++)
        {
            int key = $"\"key{i + 1}\"".Length;
            lines[i] = i is 0 or Lines - 1 ? [] : [2, key, type, 0, 2 + key + 2, $"\"value {i + 1}\"".Length, 1, 1];
        }
        return new SemanticTokenLines(legend, 0, lines);
    }
}
#endif
