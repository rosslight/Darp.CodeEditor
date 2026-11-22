// Left out of the baseline's build while the target branch has no snippets: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Completion;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The UI thread's time for a snippet at the limits, until a rendered frame shows it, in 5,000 lines of JSON, warm: the
/// most stops, each on a line of its own, which the line's indentation starts. Committing it from the list renders
/// nearly the most text a snippet may have, inserts it and starts its session; a keystroke goes in at the end of its
/// first stop. Each beside the same work without a snippet: the same text committed as plain text, and a keystroke after
/// the session ended, so that the difference is the snippet's own. Not in the CI category: no budget names this case.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(20)]
[IterationCount(40)]
[MedianColumn]
[MaxColumn]
public class SnippetBenchmarks
{
    /// <summary>The numbered stops, which with the final stop are the most stops.</summary>
    private const int Stops = Snippet.MaxStops - 1;

    /// <summary>The indentation of the line that the snippet goes in, which each stop's line starts with.</summary>
    private const int Indentation = 2;

    /// <summary>Each stop's length that comes nearest the longest snippet, as written: <c>${29:…}</c> and a line break.</summary>
    private const int StopLength = Snippet.MaxText / Stops - 7;

    private readonly ConcurrentQueue<string> _serverLines = new();
    private LanguageServer? _server;
    private Window? _window;
    private CodeEditor? _editor;
    private CodeDocument? _document;
    private CodeDocument? _answer;

    /// <summary>Where the snippets go in: after the indentation of a line in the middle of the text, in view.</summary>
    private int _start;

    /// <summary>The text's length before an iteration.</summary>
    private int _length;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        string fakeServer = Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Tests.FakeServer.dll");
        _server = new LanguageServer(
            new LanguageServerOptions
            {
                FileName = Environment.ProcessPath ?? "dotnet",
                Arguments = [fakeServer, "--no-diagnostics", "--semantic-tokens", "none"],
                StandardError = _serverLines.Enqueue,
            }
        );
        Task start = _server.StartAsync();
        CompletionBenchmarks.RunJobsUntil(() => start.IsCompleted, "The language server did not start.");
        start.GetAwaiter().GetResult();
        // In the list's order, by label.
        var items = new JsonArray(Item("commit", SnippetText(), 2), Item("plain", Rendered(), null));
        _answer = _server.CreateDocument(
            new CodeLanguage("fake-command"),
            $"completion {new JsonObject { ["items"] = items }.ToJsonString()}"
        );
        CompletionBenchmarks.RunJobsUntil(
            () => _serverLines.Any(line => line.StartsWith("set completion", StringComparison.Ordinal)),
            "The server did not take the completion."
        );
        _document = _server.CreateDocument(CodeLanguage.Json, HeadlessEditor.Json(5000));
        CodeDocument document = _document;
        CompletionBenchmarks.RunJobsUntil(
            () => document.Features is not null,
            "The document did not open in the server."
        );
        _editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(_editor);
        HeadlessEditor.RenderUntilColored(_window, _editor, HeadlessEditor.JsonKey);
        _start = _document.CurrentSnapshot.GetOffset(new TextPosition(2500, Indentation + 1));
        _editor.ScrollTo(new TextRange(_start, 0));
        HeadlessEditor.RenderUntilColored(_window, _editor, HeadlessEditor.JsonKey);
        _editor.Focus();
        _length = _document.CurrentSnapshot.Length;

        static JsonObject Item(string label, string text, int? format) =>
            new()
            {
                ["label"] = label,
                ["insertText"] = text,
                ["insertTextFormat"] = format,
            };
    }

    [IterationSetup(Target = nameof(CommitTheSnippet))]
    public void OpenTheListOnTheSnippet() => OpenTheList(0);

    /// <summary>The snippet of nearly the most text: rendered, inserted, and its first stop selected.</summary>
    [Benchmark]
    public void CommitTheSnippet()
    {
        Commit();
        if (_editor?.Selection != new TextSelection(_start, _start + StopLength))
            throw new InvalidOperationException("The snippet did not go in with its stops.");
    }

    [IterationSetup(Target = nameof(CommitTheSameTextAsPlainText))]
    public void OpenTheListOnThePlainText() => OpenTheList(1);

    /// <summary>The snippet's text, as an item in plain text.</summary>
    [Benchmark]
    public void CommitTheSameTextAsPlainText()
    {
        Commit();
        if (_document?.CurrentSnapshot.Length != _length + Rendered().Length)
            throw new InvalidOperationException("The text did not go in.");
    }

    [IterationSetup(Target = nameof(KeystrokeInAStop))]
    public void CommitBeforeTheKeystroke()
    {
        OpenTheList(0);
        Commit();
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        // At the end of the first stop, which grows.
        (_editor ?? throw new InvalidOperationException("Not set up.")).Selection = new TextSelection(
            _start + StopLength
        );
        HeadlessEditor.Render(window);
    }

    /// <summary>A keystroke at the end of the first stop, with every stop tracked.</summary>
    [Benchmark]
    public void KeystrokeInAStop() => Type();

    [IterationSetup(Target = nameof(KeystrokeWithoutASession))]
    public void CommitAndEndTheSession()
    {
        CommitBeforeTheKeystroke();
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        HeadlessEditor.Render(window);
    }

    /// <summary>The same keystroke once the session ended: it changes only its own line.</summary>
    [Benchmark]
    public void KeystrokeWithoutASession() => Type();

    /// <summary>Ends the session, and takes back what was typed and committed, so that each iteration starts alike.</summary>
    [IterationCleanup]
    public void UndoTheIteration()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        while (document.CurrentSnapshot.Length != _length)
            document.Undo();
        HeadlessEditor.Render(window);
    }

    [GlobalCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        _answer?.Dispose();
        _server?.Dispose();
    }

    /// <summary>Each stop, of <see cref="StopLength"/> characters, on a line of its own.</summary>
    private static string SnippetText() =>
        string.Join('\n', Enumerable.Range(1, Stops).Select(i => $"${{{i}:{new string('p', StopLength)}}}"));

    /// <summary>The text that <see cref="SnippetText"/> renders at the line's indentation.</summary>
    private static string Rendered() =>
        string.Join("\n" + new string(' ', Indentation), Enumerable.Repeat(new string('p', StopLength), Stops));

    /// <summary>Opens the list at where the snippets go in, and selects the item at <paramref name="index"/>.</summary>
    private void OpenTheList(int index)
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeEditor editor = _editor ?? throw new InvalidOperationException("Not set up.");
        editor.Selection = new TextSelection(_start);
        // Back in view: a commit of plain text scrolled to its end, and completion ends on a line out of view.
        editor.ScrollTo(new TextRange(_start, 0));
        HeadlessEditor.Render(window);
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        CompletionBenchmarks.RunJobsUntil(
            () => CompletionBenchmarks.List(window) is { ItemCount: 2 },
            "The list did not show."
        );
        for (int i = 0; i < index; i++)
        {
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        }
        HeadlessEditor.Render(window);
    }

    /// <summary>Enter, until a frame shows what went in.</summary>
    private void Commit()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        HeadlessEditor.Render(window);
    }

    /// <summary>Types a character, until a frame shows it.</summary>
    private void Type()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        int before = document.CurrentSnapshot.Length;
        window.KeyTextInput("q");
        HeadlessEditor.Render(window);
        if (document.CurrentSnapshot.Length != before + 1)
            throw new InvalidOperationException("The keystroke did not change what it should.");
    }
}
#endif
