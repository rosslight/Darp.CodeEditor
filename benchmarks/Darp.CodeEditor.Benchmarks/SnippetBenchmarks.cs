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
/// The UI thread's time for snippets at the limits, until a rendered frame shows them, in 5,000 lines of JSON, warm:
/// the most stops, a first stop and its mirrors, each mirror on a line of its own, which the line's indentation starts.
/// Committing one from the list renders nearly the most text a snippet may have, inserts it and starts its session; a
/// keystroke at the end of another's first stop has its mirrors copy nearly the most that one change may copy. Each
/// beside the same work without a snippet: the same text committed as plain text, and a keystroke after the session
/// ended, so that the difference is the snippet's own. Not in the CI category: no budget names this case.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(20)]
[IterationCount(40)]
[MedianColumn]
[MaxColumn]
public class SnippetBenchmarks
{
    /// <summary>The mirrors of the first stop, which with it and the final stop are the most stops.</summary>
    private const int Mirrors = Snippet.MaxStops - 2;

    /// <summary>The indentation of the line that the snippets go in, which each mirror's line starts with.</summary>
    private const int Indentation = 2;

    /// <summary>The first stop's length that comes nearest the most text a snippet may have, its parts counted.</summary>
    private const int CommitStop = (Snippet.MaxText - 4 * (Mirrors + 1)) / (Mirrors + 1) - Indentation - 1;

    /// <summary>The first stop's length that, grown by a keystroke, comes nearest the most that mirrors may copy.</summary>
    private const int KeystrokeStop = SnippetController.MaxMirrorText / (Mirrors + 1) - 1;

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
        var items = new JsonArray(
            Item("commit", SnippetText(CommitStop), 2),
            Item("keystroke", SnippetText(KeystrokeStop), 2),
            Item("plain", Rendered(CommitStop), null)
        );
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
        if (_editor?.Selection != new TextSelection(_start, _start + CommitStop))
            throw new InvalidOperationException("The snippet did not go in with its stops.");
    }

    [IterationSetup(Target = nameof(CommitTheSameTextAsPlainText))]
    public void OpenTheListOnThePlainText() => OpenTheList(2);

    /// <summary>The snippet's text, as an item in plain text.</summary>
    [Benchmark]
    public void CommitTheSameTextAsPlainText()
    {
        Commit();
        if (_document?.CurrentSnapshot.Length != _length + Rendered(CommitStop).Length)
            throw new InvalidOperationException("The text did not go in.");
    }

    [IterationSetup(Target = nameof(KeystrokeThatTheMirrorsCopy))]
    public void CommitBeforeTheKeystroke()
    {
        OpenTheList(1);
        Commit();
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        // At the end of the first stop, which its mirrors copy as it grows.
        (_editor ?? throw new InvalidOperationException("Not set up.")).Selection = new TextSelection(
            _start + KeystrokeStop
        );
        HeadlessEditor.Render(window);
    }

    /// <summary>A keystroke at the end of the first stop, which its mirrors copy, nearly as much as they may.</summary>
    [Benchmark]
    public void KeystrokeThatTheMirrorsCopy() => Type(Mirrors + 1);

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
    public void KeystrokeWithoutASession() => Type(1);

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

    /// <summary>A first stop of <paramref name="length"/> characters, then each of its mirrors on a line of its own.</summary>
    private static string SnippetText(int length) =>
        "${1:" + new string('p', length) + "}" + string.Concat(Enumerable.Repeat("\n$1", Mirrors));

    /// <summary>The text that <see cref="SnippetText"/> renders at the line's indentation.</summary>
    private static string Rendered(int length) =>
        new string('p', length)
        + string.Concat(Enumerable.Repeat("\n" + new string(' ', Indentation) + new string('p', length), Mirrors));

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
            () => CompletionBenchmarks.List(window) is { ItemCount: 3 },
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

    /// <summary>Types a character, until a frame shows it, checking that it changed <paramref name="copies"/> places.</summary>
    private void Type(int copies)
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        int before = document.CurrentSnapshot.Length;
        window.KeyTextInput("q");
        HeadlessEditor.Render(window);
        if (document.CurrentSnapshot.Length != before + copies)
            throw new InvalidOperationException("The keystroke did not change what it should.");
    }
}
#endif
