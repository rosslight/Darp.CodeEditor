using AvaloniaEdit.Document;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>Editing a 5,000-line document, and positions in its snapshots.</summary>
[MemoryDiagnoser]
public class DocumentBenchmarks
{
    private const int Lines = 5000;

    private static readonly string s_text = string.Concat(
        Enumerable.Range(0, Lines).Select(i => $"    \"key{i}\": \"value of line {i}\",\n")
    );

    private static readonly int s_middle = s_text.Length / 2;

    private readonly ITextSource _rope = new TextDocument(s_text).CreateSnapshot();
    private readonly TextSnapshot _warm;
    private CodeDocument _document;
    private bool _flip;

    public DocumentBenchmarks()
    {
        // Without Avalonia set up, documents check no thread and claim AvaloniaEdit's buffer for each change.
        _document = CodeDocument.Create(CodeLanguage.PlainText, s_text);
        _warm = new TextSnapshot(_rope, 1);
        _ = _warm.GetPosition(s_middle);
    }

    /// <summary>A new document per iteration, so that the undo history does not grow over the whole run.</summary>
    [IterationSetup(Target = nameof(EditAndSnapshot))]
    public void CreateDocument() => _document = CodeDocument.Create(CodeLanguage.PlainText, s_text);

    /// <summary>Typing one character in the middle: the edit, its undo step and its snapshot.</summary>
    [Benchmark]
    [InvocationCount(20_000)]
    public TextSnapshot EditAndSnapshot()
    {
        _flip = !_flip;
        _document.Replace(new TextRange(s_middle, 1), _flip ? "x" : "y");
        return _document.CurrentSnapshot;
    }

    /// <summary>The first position in a snapshot, which reads its line breaks.</summary>
    [Benchmark]
    public TextPosition GetPositionInNewSnapshot() => new TextSnapshot(_rope, 1).GetPosition(s_middle);

    /// <summary>A position in a snapshot that knows its lines.</summary>
    [Benchmark]
    public TextPosition GetPositionInWarmSnapshot() => _warm.GetPosition(s_middle);

    /// <summary>An offset in a snapshot that knows its lines.</summary>
    [Benchmark]
    public int GetOffsetInWarmSnapshot() => _warm.GetOffset(new TextPosition(Lines / 2, 10));
}
