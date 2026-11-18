// Left out of the baseline's build while the target branch has no selection highlights: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Selecting a word of 5,000 lines of JSON that is on every line, on the UI thread, with the frame that follows: the
/// search for its other occurrences, at most 999, which VS Code's selection highlights also run on the UI thread. Calls
/// select "key" and "value" in turn, so that each one searches anew.
/// </summary>
[NotInBaseline]
[InvocationCount(20)]
[WarmupCount(2)]
[IterationCount(10)]
public class SelectionHighlightBenchmarks
{
    private Window? _window;
    private CodeEditor? _editor;
    private bool _key;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(5000)) };
        _window = HeadlessEditor.Show(_editor);
        HeadlessEditor.RenderUntilColored(_window, _editor, HeadlessEditor.JsonKey);
        _editor.Focus();
    }

    /// <summary>Selects "key" or "value" on the second line, <c>  "key2": "value 2",</c>.</summary>
    [Benchmark]
    public void SelectAWordOnEveryLine()
    {
        CodeEditor editor = _editor ?? throw new InvalidOperationException("Not set up.");
        _key = !_key;
        editor.Selection = _key ? new TextSelection(5, 8) : new TextSelection(13, 18);
        HeadlessEditor.Render(_window ?? throw new InvalidOperationException("Not set up."));
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();
}
#endif
