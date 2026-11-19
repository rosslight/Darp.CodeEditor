// Left out of the baseline's build while the target branch has no occurrences: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// One caret move by an arrow key in 5,000 lines of JSON that the fake language server serves, with the frame after it:
/// with <see cref="CodeEditorOptions.OccurrencesHighlight"/>, until a frame shows the server's occurrence of the key at
/// the caret, its request and answer included; without, the move and its frame alone. The editor's pause of 50 ms
/// before it asks ends at once, so that a call measures the work, not the wait. Each call moves the caret a line down
/// or back up.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class OccurrenceBenchmarks
{
    private LanguageServer? _server;
    private CodeDocument? _document;
    private Window? _window;
    private CodeEditor? _editor;
    private bool _down;

    [Params(true, false)]
    public bool OccurrencesHighlight { get; set; }

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
                TimeProvider = new WithoutPause(),
            }
        );
        Task start = _server.StartAsync();
        CompletionBenchmarks.RunJobsUntil(() => start.IsCompleted, "The language server did not start.");
        start.GetAwaiter().GetResult();
        CodeDocument document = _server.CreateDocument(CodeLanguage.Json, HeadlessEditor.Json(5000));
        _document = document;
        CompletionBenchmarks.RunJobsUntil(
            () => document.Features is not null,
            "The document did not open in the server."
        );
        var editor = new CodeEditor
        {
            Document = document,
            Options = CodeEditorOptions.Default with { OccurrencesHighlight = OccurrencesHighlight },
        };
        _editor = editor;
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        int caret = document.CurrentSnapshot.GetOffset(new TextPosition(2500, 4));
        editor.Selection = new TextSelection(caret);
        editor.ScrollTo(new TextRange(caret, 0));
        editor.Focus();
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void CaretMoveWithAServer()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        CodeEditor editor = _editor ?? throw new InvalidOperationException("Not set up.");
        CodeDocument document = _document ?? throw new InvalidOperationException("Not set up.");
        _down = !_down;
        (Key key, PhysicalKey physical) = _down ? (Key.Down, PhysicalKey.ArrowDown) : (Key.Up, PhysicalKey.ArrowUp);
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
        int line = _down ? 2501 : 2500;
        if (OccurrencesHighlight)
        {
            // The key's occurrence: its only one.
            int start = document.CurrentSnapshot.GetOffset(new TextPosition(line, 4));
            CompletionBenchmarks.RunJobsUntil(
                () =>
                    editor.Occurrences is [{ Kind: OccurrenceKind.Read } occurrence] && occurrence.Range.Start == start,
                "The occurrences did not show."
            );
        }
        HeadlessEditor.Render(window);
        if (editor.CaretPosition.Line != line)
            throw new InvalidOperationException("The key did not move the caret.");
    }

    [GlobalCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        _server?.Dispose();
    }

    /// <summary>The system's clock, but for the editor's pause of 50 ms before it asks for occurrences, which ends at once.</summary>
    private sealed class WithoutPause : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(
                callback,
                state,
                dueTime == TimeSpan.FromMilliseconds(50) ? TimeSpan.Zero : dueTime,
                period
            );
    }
}
#endif
