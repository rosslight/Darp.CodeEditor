// Left out of the baseline's build while the target branch has no C# scripts: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.ComponentModel;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;
using ScriptTests.Api;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The real C# script server on a script of 5,000 lines, warm, through the client's path: its process, the protocol and
/// reading the answer. Each iteration follows an edit, so Roslyn works on a new version of the script while its
/// diagnostics run, as when a user types. Completion's budget is 50 ms at the 95th percentile (design, section 1.2).
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(40)]
[IterationCount(30)]
[P95Column]
public class CSharpScriptBenchmarks
{
    private readonly ManualResetEventSlim _diagnosticsChanged = new();
    private CSharpScriptServer? _server;
    private CSharpScriptDocument<ScriptTestGlobals>? _document;
    private int _editedDigit;
    private int _memberCaret;
    private int _wordCaret;
    private int _firstVisibleLine;
    private int _edits;

    private CSharpScriptDocument<ScriptTestGlobals> Document =>
        _document ?? throw new InvalidOperationException("Not set up.");

    [GlobalSetup]
    public void SetUp()
    {
        string text = ScriptText.OfLines(5000);
        _server = new CSharpScriptServer();
        _document = _server.CreateDocument(new CSharpScriptEnvironment<ScriptTestGlobals>(), text);
        ScriptText.WaitUntilOpen(_document);
        _editedDigit = text.IndexOf(ScriptText.EditedLine, StringComparison.Ordinal) + ScriptText.EditedLine.Length - 2;
        _memberCaret =
            text.IndexOf(ScriptText.MemberLine, StringComparison.Ordinal)
            + ScriptText.MemberLine.Length
            - "Length;".Length;
        _wordCaret = text.IndexOf(ScriptText.MemberLine, StringComparison.Ordinal) + "var probe = ".Length;
        _firstVisibleLine = Document.CurrentSnapshot.GetPosition(_memberCaret).Line - 20;
        _document.PropertyChanged += OnDocumentChanged;
    }

    [IterationSetup(
        Targets = [
            nameof(CompletionOfAMemberList),
            nameof(CompletionWithoutAReceiver),
            nameof(CompletionOfATypedName),
            nameof(ColorsOfTheVisibleLines),
        ]
    )]
    public void Edit() =>
        Document.Replace(
            new TextRange(_editedDigit, 1),
            (++_edits % 10).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

    [Benchmark]
    [BenchmarkCategory("CI")]
    public int CompletionOfAMemberList()
    {
        int items = Complete(new TextRange(_memberCaret, 0));
        return items is >= 100 and < 500
            ? items
            : throw new InvalidOperationException($"The member list has {items} items.");
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public int CompletionWithoutAReceiver()
    {
        int items = Complete(new TextRange(_wordCaret, 0));
        return items >= 500 ? items : throw new InvalidOperationException($"The list has {items} items.");
    }

    /// <summary>
    /// As typing the first letters of a name asks, with the best of the types and extension methods from namespaces
    /// that the script does not import that match them.
    /// </summary>
    [Benchmark]
    public int CompletionOfATypedName()
    {
        int items = Complete(new TextRange(_wordCaret, 2));
        return items >= 500 ? items : throw new InvalidOperationException($"The list has {items} items.");
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void KeystrokeUntilDiagnostics()
    {
        Edit();
        TextSnapshot snapshot = Document.CurrentSnapshot;
        while (Document.Diagnostics is not [{ } first, ..] || first.Snapshot != snapshot)
        {
            if (!_diagnosticsChanged.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("The diagnostics did not arrive.");
            _diagnosticsChanged.Reset();
        }
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public int ColorsOfTheVisibleLines()
    {
        SemanticTokenLines tokens =
            Document
                .SemanticTokensAsync(
                    new TextPosition(_firstVisibleLine, 1),
                    new TextPosition(_firstVisibleLine + 40, 1),
                    5000,
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()
            ?? throw new InvalidOperationException("No tokens.");
        int count = tokens.Lines.Sum(line => line.Length);
        return count > 0 ? count : throw new InvalidOperationException("The visible lines have no tokens.");
    }

    [GlobalCleanup]
    public void Close()
    {
        _document?.Dispose();
        _server?.Dispose();
    }

    private int Complete(TextRange word) => ScriptText.Complete(Document, word);

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.Diagnostics))
            _diagnosticsChanged.Set();
    }
}

/// <summary>
/// "C# ready" from a cold start, in a new process each time: from creating the server until the first completion answers
/// in a script of 5,000 lines. Its budget is 1.5 s (design, section 1.2).
/// </summary>
[NotInBaseline]
[ColdStart]
public class CSharpScriptColdStartBenchmarks
{
    private readonly string _text = ScriptText.OfLines(5000);

    [Benchmark]
    [BenchmarkCategory("CI")]
    public int NewServerUntilTheFirstCompletion()
    {
        using var server = new CSharpScriptServer();
        using CSharpScriptDocument<ScriptTestGlobals> document = server.CreateDocument(
            new CSharpScriptEnvironment<ScriptTestGlobals>(),
            _text
        );
        ScriptText.WaitUntilOpen(document);
        int caret =
            _text.IndexOf(ScriptText.MemberLine, StringComparison.Ordinal)
            + ScriptText.MemberLine.Length
            - "Length;".Length;
        int items = ScriptText.Complete(document, new TextRange(caret, 0));
        return items is >= 100 and < 500
            ? items
            : throw new InvalidOperationException($"The member list has {items} items.");
    }
}

/// <summary>A script of ordinary code, with lines that the benchmarks work on in its middle.</summary>
internal static class ScriptText
{
    public const string EditedLine = "int edited = 0;";
    public const string MemberLine = "var probe = Greeting.Length;";

    /// <summary>A text of <paramref name="lines"/> lines, with one error, so that every version has diagnostics.</summary>
    public static string OfLines(int lines)
    {
        var text = new StringBuilder();
        const int LinesPerFunction = 10;
        for (int function = 0; function < lines / LinesPerFunction; function++)
        {
            if (function == lines / LinesPerFunction / 2)
                text.Append(EditedLine).Append('\n').Append(MemberLine).Append('\n');
            text.Append($"int Compute{function}(int value)\n{{\n")
                .Append("    var text = Greeting + value;\n")
                .Append("    var numbers = Enumerable.Range(0, value).Where(n => n % 2 == 0).ToList();\n")
                .Append("    int sum = Add(numbers.Count, text.Length);\n")
                .Append("    foreach (int number in numbers)\n")
                .Append("        sum = Add(sum, number);\n")
                .Append("    return sum;\n}\n\n");
        }
        return text.Append("undefinedName;\n").ToString();
    }

    /// <summary>Editors ask only once the document is open in the server's process.</summary>
    public static void WaitUntilOpen(CodeDocument document)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (document.Features is null)
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(60))
                throw new InvalidOperationException("The document did not open in the server.");
            Thread.Sleep(1);
        }
    }

    /// <summary>Asks for completion at the end of <paramref name="word"/> and returns how many items came.</summary>
    public static int Complete(CodeDocument document, TextRange word) =>
        (
            document
                .CompletionAsync(
                    document.CurrentSnapshot,
                    word,
                    new CompletionTrigger(CompletionTriggerKind.Invoked),
                    CancellationToken.None
                )
                .GetAwaiter()
                .GetResult()
            ?? throw new InvalidOperationException("No completion list.")
        )
            .Items
            .Count;
}

/// <summary>Adds the 95th percentile, which the completion budget is about.</summary>
[AttributeUsage(AttributeTargets.Class)]
internal sealed class P95ColumnAttribute() : ColumnConfigBaseAttribute(StatisticColumn.P95);
#endif
