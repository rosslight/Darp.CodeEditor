// Left out of the baseline's build while the target branch has no editing helpers: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using BenchmarkDotNet.Attributes;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// The editing helpers on the UI thread, with the layout and the frame that follow, in 5,000 lines of JSON: typing an
/// opening bracket, which tokenizes the line up to the caret, closes the pair and matches the brackets; Backspace
/// between the pair; and commenting all 5,000 lines, against a digit typed the same way. Each call starts from the same
/// text, set up outside the measurement, and its edit is checked after it: a helper that does nothing fails the run
/// instead of looking fast.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(2)]
[IterationCount(15)]
public class EditingBenchmarks
{
    private Window? _window;
    private CodeEditor? _editor;

    /// <summary>The end of a line in the middle, after a comma, where an opening bracket closes.</summary>
    private int _lineEnd;

    private string _text = "";

    [GlobalSetup]
    public void SetUp()
    {
        HeadlessEditor.SetUpApp();
        _text = HeadlessEditor.Json(5000);
        _lineEnd = TextSnapshot.FromString(_text).GetOffset(new TextPosition(2500, int.MaxValue));
        Expect(_text[_lineEnd - 1] == ',', "Not the expected line.");
        var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, _text) };
        _editor = editor;
        _window = HeadlessEditor.Show(editor);
        HeadlessEditor.RenderUntilColored(_window, editor, HeadlessEditor.JsonKey);
        editor.ScrollTo(new TextRange(_lineEnd, 0));
        editor.Focus();
    }

    [IterationSetup(Targets = [nameof(TypeAnOpeningBracket), nameof(TypeADigit)])]
    public void PutTheCaretAtTheLineEnd() => Select(new TextSelection(_lineEnd));

    /// <summary>For reference: a character that the helpers leave alone, set up and checked the same way.</summary>
    [Benchmark(Baseline = true)]
    public void TypeADigit()
    {
        Window window = Window;
        window.KeyTextInput("1");
        HeadlessEditor.Render(window);
    }

    [IterationCleanup(Targets = [nameof(TypeADigit)])]
    public void CheckTheDigitAndUndo()
    {
        Expect(Text(_lineEnd, 1) == "1" && Text(_lineEnd + 1, 1) != "]", "The digit did not go in alone.");
        Document.Undo();
    }

    /// <summary>Types "[", which brings its "]".</summary>
    [Benchmark]
    public void TypeAnOpeningBracket()
    {
        Window window = Window;
        window.KeyTextInput("[");
        HeadlessEditor.Render(window);
    }

    [IterationCleanup(Targets = [nameof(TypeAnOpeningBracket)])]
    public void CheckThePairAndUndo()
    {
        Expect(Text(_lineEnd, 2) == "[]" && Editor.Selection == new TextSelection(_lineEnd + 1), "No pair closed.");
        Document.Undo();
    }

    /// <summary>Types "[", which brings its "]", for Backspace to delete.</summary>
    [IterationSetup(Targets = [nameof(BackspaceAnEmptyPair)])]
    public void TypeAPair()
    {
        Select(new TextSelection(_lineEnd));
        Window.KeyTextInput("[");
        HeadlessEditor.Render(Window);
        Expect(Text(_lineEnd, 2) == "[]", "No pair closed.");
    }

    /// <summary>Deletes the pair that the editor closed, with Backspace.</summary>
    [Benchmark]
    public void BackspaceAnEmptyPair()
    {
        Window window = Window;
        Press(window, Key.Back, KeyModifiers.None);
        HeadlessEditor.Render(window);
    }

    [IterationCleanup(Targets = [nameof(BackspaceAnEmptyPair)])]
    public void CheckThePairIsGone() => Expect(Document.Text == _text, "Backspace did not delete the pair.");

    [IterationSetup(Targets = [nameof(Comment5000Lines)])]
    public void SelectAllLines() => Select(new TextSelection(0, Document.CurrentSnapshot.Length));

    /// <summary>Comments 5,000 lines with Ctrl+/, or Cmd+/ on macOS.</summary>
    [Benchmark]
    public void Comment5000Lines()
    {
        Window window = Window;
        Press(window, Key.Oem2, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        HeadlessEditor.Render(window);
    }

    [IterationCleanup(Targets = [nameof(Comment5000Lines)])]
    public void CheckTheCommentsAndUndo()
    {
        TextSnapshot text = Document.CurrentSnapshot;
        Expect(
            text.Length == _text.Length + 5000 * "// ".Length
                && text.GetText(new TextRange(text.GetOffset(new TextPosition(2999, 1)), 6)) == "//   \"",
            "The lines were not commented."
        );
        Document.Undo();
    }

    [GlobalCleanup]
    public void Close() => _window?.Close();

    private Window Window => _window ?? throw new InvalidOperationException("Not set up.");

    private CodeEditor Editor => _editor ?? throw new InvalidOperationException("Not set up.");

    private CodeDocument Document => Editor.Document ?? throw new InvalidOperationException("No document.");

    private string Text(int start, int length) => Document.CurrentSnapshot.GetText(new TextRange(start, length));

    /// <summary>Selects, and renders the frame after it, so that the measured call starts from a still editor.</summary>
    private void Select(TextSelection selection)
    {
        Editor.Selection = selection;
        HeadlessEditor.Render(Window);
    }

    private static void Expect(bool condition, string failure)
    {
        if (!condition)
            throw new InvalidOperationException(failure);
    }

    private static void Press(Window window, Key key, KeyModifiers modifiers)
    {
        window.KeyPress(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, (RawInputModifiers)modifiers, PhysicalKey.None, null);
    }
}
#endif
