// Left out of the baseline's build while the target branch has no completion: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Completion;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Completion in 5,000 lines of JSON, warm, against the fake language server, which answers at once with 1,000 items,
/// as a C# server's list can be long: from Ctrl+Space until a rendered frame shows the list's items, which the budget
/// of 50 ms is about (design, section 1.2); and from typing a character that filters the open list until a rendered
/// frame shows the filtered list, which the filter reaches off the UI thread. The server's time to build its answer
/// counts, its time to think is nil. Each iteration is one call, so warming up takes many: the budget is for code that
/// the JIT compiler has optimized.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(40)]
[IterationCount(20)]
public class CompletionBenchmarks
{
    private const int Items = 1000;

    private LanguageServer? _server;
    private Window? _window;
    private CodeEditor? _editor;
    private CodeDocument? _document;

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
                Arguments =
                [
                    fakeServer,
                    "--completion-items",
                    $"{Items}",
                    "--no-diagnostics",
                    "--semantic-tokens",
                    "none",
                ],
            }
        );
        Task start = _server.StartAsync();
        RunJobsUntil(() => start.IsCompleted, "The language server did not start.");
        start.GetAwaiter().GetResult();
        _document = _server.CreateDocument(CodeLanguage.Json, HeadlessEditor.Json(5000));
        // Editors ask only once the document is open in the server, after the start's settings went out.
        CodeDocument document = _document;
        RunJobsUntil(() => document.Features is not null, "The document did not open in the server.");
        _editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(_editor);
        HeadlessEditor.RenderUntilColored(_window, _editor, HeadlessEditor.JsonKey);
        // In the middle of the text, in view, before a key: nothing typed yet, so all items show.
        int caret = _document.CurrentSnapshot.GetOffset(new TextPosition(2500, 3));
        _editor.Selection = new TextSelection(caret);
        _editor.ScrollTo(new TextRange(caret, 0));
        HeadlessEditor.RenderUntilColored(_window, _editor, HeadlessEditor.JsonKey);
        _editor.Focus();
        _length = _document.CurrentSnapshot.Length;
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void CtrlSpaceUntilAFrameShowsTheList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        RunJobsUntil(() => List(window) is { ItemCount: Items }, "The list did not show.");
        HeadlessEditor.Render(window);
        // A list that showed nothing would look fast.
        if (List(window)?.ContainerFromIndex(0) is not ListBoxItem { Content: ShownItem { Item.Label: "alpha0" } })
            throw new InvalidOperationException("The frame does not show the list's items.");
    }

    [IterationSetup(Target = nameof(TypeACharacterUntilAFrameShowsTheFilteredList))]
    public void OpenTheList() => CtrlSpaceUntilAFrameShowsTheList();

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void TypeACharacterUntilAFrameShowsTheFilteredList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyTextInput("a");
        // The items that start with "a": one in eight.
        RunJobsUntil(() => List(window) is { ItemCount: Items / 8 }, "The list was not filtered.");
        HeadlessEditor.Render(window);
        if (List(window)?.ContainerFromIndex(0) is not ListBoxItem { Content: ShownItem { Highlights.Length: 1 } })
            throw new InvalidOperationException("The frame does not show the filtered list.");
    }

    /// <summary>Closes the list, and deletes what was typed, so that each iteration starts alike.</summary>
    [IterationCleanup]
    public void CloseTheList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        if (document.CurrentSnapshot.Length != _length)
            document.Undo();
        HeadlessEditor.Render(window);
    }

    [GlobalCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        _server?.Dispose();
    }

    internal static ListBox? List(Window window) =>
        OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();

    /// <summary>Runs the UI thread's jobs until <paramref name="condition"/> holds; fails after 10 seconds.</summary>
    internal static void RunJobsUntil(Func<bool> condition, string failure)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException(failure);
        }
    }
}

/// <summary>
/// The UI thread's time for a keystroke in the worst case that the design's budgets consider: 10,000 items of 128
/// characters, such as long names a server sends, and a typed word of 64 characters that every label holds in order,
/// so that matching runs fully for each. Filtering runs off the UI thread, so the keystroke with its frame costs what a
/// keystroke without completion does, plus scheduling the filter. Not in the CI category: no budget names this case.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(10)]
[IterationCount(20)]
public class CompletionWorstCaseBenchmarks
{
    private const int Items = 10_000;

    /// <summary>What is typed before the measured keystroke: 63 of the 64 characters.</summary>
    private static readonly string s_typed = string.Concat(Enumerable.Repeat("ab", 32))[..63];

    private LanguageServer? _server;
    private Window? _window;
    private CodeDocument? _document;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        string fakeServer = Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Tests.FakeServer.dll");
        _server = new LanguageServer(
            new LanguageServerOptions
            {
                FileName = Environment.ProcessPath ?? "dotnet",
                Arguments =
                [
                    fakeServer,
                    "--completion-items",
                    $"{Items}",
                    "--completion-label-length",
                    "128",
                    "--no-diagnostics",
                    "--semantic-tokens",
                    "none",
                ],
            }
        );
        Task start = _server.StartAsync();
        CompletionBenchmarks.RunJobsUntil(() => start.IsCompleted, "The language server did not start.");
        start.GetAwaiter().GetResult();
        _document = _server.CreateDocument(CodeLanguage.PlainText, s_typed);
        // Editors ask only once the document is open in the server, after the start's settings went out.
        CodeDocument document = _document;
        CompletionBenchmarks.RunJobsUntil(
            () => document.Features is not null,
            "The document did not open in the server."
        );
        var editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.Render(_window);
        editor.Selection = new TextSelection(s_typed.Length);
        editor.Focus();
    }

    /// <summary>Opens the list for the 63 characters, and waits until it shows them filtered.</summary>
    [IterationSetup]
    public void OpenTheList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        window.KeyRelease(Key.Space, RawInputModifiers.Control, PhysicalKey.Space, null);
        CompletionBenchmarks.RunJobsUntil(
            () =>
                CompletionBenchmarks.List(window)?.ItemsSource is IReadOnlyList<ShownItem> { Count: > 0 } items
                && items[0].Highlights.Length == s_typed.Length,
            "The list was not filtered."
        );
        HeadlessEditor.Render(window);
    }

    [Benchmark]
    public void KeystrokeThatFiltersTheList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        window.KeyTextInput("b");
        HeadlessEditor.Render(window);
        // A keystroke that edits nothing would look fast.
        if (document.CurrentSnapshot.Length != s_typed.Length + 1)
            throw new InvalidOperationException("The keystroke did not edit the text.");
    }

    /// <summary>Closes the list, which stops its filter, and deletes the character typed.</summary>
    [IterationCleanup]
    public void CloseTheList()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        if (document.CurrentSnapshot.Length != s_typed.Length)
            document.Undo();
        HeadlessEditor.Render(window);
    }

    [GlobalCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        _server?.Dispose();
    }
}
#endif
