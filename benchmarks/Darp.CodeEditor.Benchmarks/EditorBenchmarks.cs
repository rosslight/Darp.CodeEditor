// Left out of the baseline's build while the target branch has no editor: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The first colored frame of a new editor of 5,000 lines: from creating the editor until a rendered frame shows its
/// visible lines in syntax colors, without a language server. The budget is 100 ms (design, section 1.2).
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
public class FirstFrameBenchmarks
{
    private CodeDocument? _document;
    private Window? _window;

    [Params("json", "yaml")]
    public string Language { get; set; } = "json";

    /// <summary>The app, with its language and theme loaded at startup.</summary>
    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _ = Language == "json" ? CodeLanguage.Json.Loaded.Result : CodeLanguage.Yaml.Loaded.Result;
        _ = CodeEditorColorTheme.LightPlus.Loaded.Result;
    }

    [IterationSetup]
    public void CreateDocument() =>
        _document = CodeDocument.Create(
            Language == "json" ? CodeLanguage.Json : CodeLanguage.Yaml,
            Language == "json" ? HeadlessEditor.Json(5000) : HeadlessEditor.Yaml(5000)
        );

    [Benchmark]
    public void FirstColoredFrame()
    {
        var editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor);
    }

    [IterationCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>
/// One keystroke on the UI thread, with the layout and the frame that follow it: the cost the user feels while typing.
/// Each call types a character or deletes it again, so the text stays the same.
/// </summary>
[NotInBaseline]
public class TypingBenchmarks
{
    private Window? _window;
    private bool _typed;

    /// <summary>In the middle of 5,000 lines of JSON, at the start of a key.</summary>
    [GlobalSetup(Target = nameof(KeystrokeIn5000Lines))]
    public void ShowJson() => Show(HeadlessEditor.Json(5000), new TextPosition(2500, 4));

    /// <summary>
    /// On a line of 20,000 characters, a JSON array that runs out of the background's time limit, near its start:
    /// AvaloniaEdit edits only the first 3,000 characters of a longer line.
    /// </summary>
    [GlobalSetup(Target = nameof(KeystrokeOnA20000CharacterLine))]
    public void ShowLongLine() =>
        Show(
            "{\n  \"long\": [" + string.Concat(Enumerable.Repeat("1, ", 6_666)) + "1],\n  \"after\": 1\n}",
            new TextPosition(2, 100)
        );

    [Benchmark]
    public void KeystrokeIn5000Lines() => TypeOrDelete();

    [Benchmark]
    public void KeystrokeOnA20000CharacterLine() => TypeOrDelete();

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private void Show(string json, TextPosition caret)
    {
        HeadlessEditor.SetUpApp();
        CodeDocument document = CodeDocument.Create(CodeLanguage.Json, json);
        var editor = new CodeEditor { Document = document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor);
        editor.Selection = new TextSelection(document.CurrentSnapshot.GetOffset(caret));
        editor.Focus();
    }

    private void TypeOrDelete()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        _typed = !_typed;
        if (_typed)
            window.KeyTextInput("1");
        else
        {
            window.KeyPress(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
            window.KeyRelease(Key.Back, RawInputModifiers.None, PhysicalKey.Backspace, null);
        }
        window.CaptureRenderedFrame();
    }
}

/// <summary>
/// One page down through 5,000 lines of JSON, just opened, on the UI thread, with the layout and the frame that follow:
/// each page shows lines that are not tokenized yet. An iteration goes through the document once.
/// </summary>
[NotInBaseline]
[InvocationCount(Pages)]
public class ScrollingBenchmarks
{
    /// <summary>About 150 pages of 31 lines: the view shows 31 lines.</summary>
    private const int Pages = 150;

    private CodeDocument? _document;
    private Window? _window;

    [GlobalSetup]
    public void SetUp() => HeadlessEditor.SetUpApp();

    [IterationSetup]
    public void Open()
    {
        _document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(5000));
        var editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor);
        editor.Focus();
    }

    [Benchmark]
    public void PageDown()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        window.CaptureRenderedFrame();
    }

    [IterationCleanup]
    public void Close()
    {
        _window?.Close();
        _document?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}

/// <summary>Editors in windows of a headless app that renders with Skia, on the benchmark's thread.</summary>
internal static class HeadlessEditor
{
    /// <summary>
    /// Sets up the app on this thread, which becomes its UI thread: Fluent and the editor's styles, as an app has them.
    /// </summary>
    public static void SetUpApp() =>
        AppBuilder
            .Configure<EditorApp>()
            .UseSkia()
            .UseHarfBuzz()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

    public static Window Show(CodeEditor editor)
    {
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = editor,
        };
        window.Show();
        return window;
    }

    /// <summary>Renders frames until one shows the editor's visible lines in syntax colors; fails after 10 seconds.</summary>
    public static void RenderUntilColored(Window window, CodeEditor editor)
    {
        long start = Stopwatch.GetTimestamp();
        TextView textView = editor.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Single().TextArea.TextView;
        while (true)
        {
            window.CaptureRenderedFrame();
            if (TextColors(textView) > 1)
                return;
            if (Stopwatch.GetElapsedTime(start) > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("The editor shows no syntax colors.");
        }
    }

    public static string Json(int lines) =>
        "{\n"
        + string.Concat(Enumerable.Range(2, lines - 3).Select(i => $"  \"key{i}\": \"value {i}\",\n"))
        + "  \"end\": 0\n}";

    public static string Yaml(int lines) =>
        string.Concat(
            Enumerable.Range(1, lines).Select(i => i % 10 == 0 ? $"# section {i}\n" : $"key{i}: value {i} # note\n")
        );

    /// <summary>How many text colors the rendered lines use: the default one, and syntax colors.</summary>
    private static int TextColors(TextView textView) =>
        textView.VisualLinesValid
            ? textView
                .VisualLines.SelectMany(line => line.TextLines)
                .SelectMany(line => line.TextRuns)
                .Select(run => (run.Properties?.ForegroundBrush as ISolidColorBrush)?.Color)
                .Distinct()
                .Count()
            : 0;

    private sealed class EditorApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new CodeEditorStyles());
        }
    }
}
#endif
