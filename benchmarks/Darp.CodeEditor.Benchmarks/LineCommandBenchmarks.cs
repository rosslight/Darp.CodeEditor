// Left out of the baseline's build while the target branch moves no lines: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// Alt+Down on the UI thread, with the layout and the frame that follow, in the middle of 5,000 lines of JSON: the line
/// moves past the next one. Each call starts from the same text, set up outside the measurement, and is checked after
/// it. Not in the CI category: moving lines is no hot path, and no budget names it.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(15)]
public class LineCommandBenchmarks
{
    private Window? _window;
    private CodeEditor? _editor;
    private string _text = "";

    /// <summary>The caret in a line in the middle, after its indentation.</summary>
    private int _caret;

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _text = HeadlessEditor.Json(5000);
        _caret = TextSnapshot.FromString(_text).GetOffset(new TextPosition(2500, 3));
        var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, _text) };
        _editor = editor;
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        editor.ScrollTo(new TextRange(_caret, 0));
        editor.Focus();
    }

    /// <summary>Puts the caret on the line, and renders the frame after it, so that the move starts from a still editor.</summary>
    [IterationSetup]
    public void PutTheCaretOnTheLine()
    {
        Editor.Selection = new TextSelection(_caret);
        HeadlessEditor.Render(Window);
    }

    [Benchmark]
    public void MoveALineDown()
    {
        Window window = Window;
        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
        window.KeyRelease(Key.Down, RawInputModifiers.Alt, PhysicalKey.None, null);
        HeadlessEditor.Render(window);
    }

    /// <summary>A move that does nothing would look fast.</summary>
    [IterationCleanup]
    public void CheckTheMoveAndUndo()
    {
        CodeDocument document = Editor.Document ?? throw new InvalidOperationException("No document.");
        if (document.Text == _text || Editor.CaretPosition != new TextPosition(2501, 3))
            throw new InvalidOperationException("The line did not move down.");
        document.Undo();
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private Window Window => _window ?? throw new InvalidOperationException("Not set up.");

    private CodeEditor Editor => _editor ?? throw new InvalidOperationException("Not set up.");
}
#endif
