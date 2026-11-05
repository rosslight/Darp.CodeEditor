using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>Mapping a diagnostic's range to the current text, after the user typed above it.</summary>
[MemoryDiagnoser]
public class MapToCurrentBenchmarks
{
    private static readonly string s_text = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"));

    private readonly CodeDocument _document;
    private readonly TextSnapshot _analyzed;
    private readonly TextRange _range = new(s_text.Length / 2, 5);

    public MapToCurrentBenchmarks()
    {
        // Without Avalonia set up, documents check no thread and claim AvaloniaEdit's buffer for each change.
        _document = CodeDocument.Create(CodeLanguage.PlainText, s_text);
        _analyzed = _document.CurrentSnapshot;
    }

    [Params(1, 100)]
    public int Edits { get; set; }

    /// <summary>The user types above the range.</summary>
    [GlobalSetup]
    public void TypeAbove()
    {
        for (int i = 0; i < Edits; i++)
            _document.Replace(new TextRange(i * 3, 0), "x");
    }

    [Benchmark]
    public TextRange MapToCurrent() => _document.MapToCurrent(_analyzed, _range);
}
