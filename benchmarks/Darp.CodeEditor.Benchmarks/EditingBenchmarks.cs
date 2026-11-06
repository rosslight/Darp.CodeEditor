// Left out of the baseline's build while the target branch has no editing helpers: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The editing helpers on the UI thread, with the layout and the frame that follow, in 5,000 lines of JSON: a keystroke
/// that closes a pair, which tokenizes the line up to the caret and matches the brackets, and toggling the comments of
/// 1,000 lines.
/// </summary>
[NotInBaseline]
[WarmupCount(2)]
[IterationCount(10)]
public class EditingBenchmarks
{
    private Window? _window;
    private bool _done;

    /// <summary>At the end of a line in the middle, where an opening bracket closes.</summary>
    [GlobalSetup(Target = nameof(KeystrokeThatClosesAPair))]
    public void ShowAtALineEnd() => Show(new TextSelection(Offset(2500, int.MaxValue)));

    /// <summary>1,000 lines from the middle, selected.</summary>
    [GlobalSetup(Target = nameof(ToggleCommentsOf1000Lines))]
    public void SelectLines() => Show(new TextSelection(Offset(2000, 1), Offset(3000, 1)));

    /// <summary>Types "[", which brings its "]", or deletes the pair again with Backspace.</summary>
    [Benchmark]
    public void KeystrokeThatClosesAPair()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        _done = !_done;
        if (_done)
            window.KeyTextInput("[");
        else
            Press(window, Key.Back, KeyModifiers.None);
        HeadlessEditor.Render(window);
    }

    /// <summary>Comments the lines, or uncomments them again.</summary>
    [Benchmark]
    public void ToggleCommentsOf1000Lines()
    {
        Window window = _window ?? throw new InvalidOperationException("Not set up.");
        Press(window, Key.Oem2, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        HeadlessEditor.Render(window);
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private static int Offset(int line, int column) =>
        TextSnapshot.FromString(HeadlessEditor.Json(5000)).GetOffset(new TextPosition(line, column));

    private void Show(TextSelection selection)
    {
        HeadlessEditor.SetUpApp();
        var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, HeadlessEditor.Json(5000)) };
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        editor.Selection = selection;
        editor.ScrollTo(selection.Range);
        editor.Focus();
        HeadlessEditor.Render(_window);
    }

    private static void Press(Window window, Key key, KeyModifiers modifiers)
    {
        window.KeyPress(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
    }
}
#endif
