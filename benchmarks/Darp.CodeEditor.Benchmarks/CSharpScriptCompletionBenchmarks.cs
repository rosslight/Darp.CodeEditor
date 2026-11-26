// Left out of the baseline's build while the target branch has no C# scripts: see NotInBaselineAttribute.
#if !BENCHMARK_BASELINE
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using BenchmarkDotNet.Attributes;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.CSharpScript;
using Perfolizer.Mathematics.OutlierDetection;
using ScriptTests.Api;

namespace Darp.CodeEditor.Benchmarks;

/// <summary>
/// From the editor receiving the input to a headless frame with selectable completion items from the real C# script
/// server. Uses the same script, digit edit and warmup as <see cref="CSharpScriptBenchmarks"/>.
/// </summary>
[NotInBaseline]
[InvocationCount(1)]
[WarmupCount(40)]
[IterationCount(30)]
[MedianColumn]
[P95Column]
[Outliers(OutlierMode.DontRemove)]
public class CSharpScriptCompletionBenchmarks
{
    private CSharpScriptServer? _server;
    private CSharpScriptDocument<ScriptTestGlobals>? _document;
    private CodeEditor? _editor;
    private Window? _window;
    private int _caret;
    private int _editedDigit;
    private int _edits;

    private CodeDocument Document => _document ?? throw new InvalidOperationException("Not set up.");
    private CodeEditor Editor => _editor ?? throw new InvalidOperationException("Not set up.");
    private Window Window => _window ?? throw new InvalidOperationException("Not set up.");

    // The convenience input methods run jobs and render before delivering the key.
    private InputElement FocusedInput =>
        Window.FocusManager?.GetFocusedElement() as InputElement
        ?? throw new InvalidOperationException("The editor does not have input focus.");

    [GlobalSetup(Target = nameof(TypePeriodUntilAFrameShowsTheMemberList))]
    public void SetUpMember() => SetUp(member: true);

    [GlobalSetup(Target = nameof(CtrlSpaceUntilAFrameShowsTheList))]
    public void SetUpWithoutReceiver() => SetUp(member: false);

    private void SetUp(bool member)
    {
        HeadlessEditor.SetUpApp();
        string text = ScriptText.OfLines(5000);
        int probe = text.IndexOf(ScriptText.MemberLine, StringComparison.Ordinal);
        _editedDigit = ScriptText.EditedDigitIn(text);
        _caret = probe + (member ? ScriptText.MemberLine.IndexOf('.') : "var probe = ".Length);
        // Typing the dot restores the exact script and caret used by the client-only benchmark.
        if (member)
            text = text.Remove(_caret, 1);
        _server = new CSharpScriptServer();
        _document = _server.CreateDocument(new CSharpScriptEnvironment<ScriptTestGlobals>(), text);
        CompletionBenchmarks.RunJobsUntil(() => Document.Features is not null, "The script did not open.");
        _editor = new CodeEditor { Document = _document };
        _window = HeadlessEditor.Show(_editor);
        _editor.Selection = new TextSelection(_caret);
        _editor.ScrollTo(new TextRange(_caret, 0));
        _editor.Focus();
        HeadlessEditor.Render(_window);
    }

    [IterationSetup]
    public void Edit()
    {
        Editor.Selection = new TextSelection(_caret);
        ScriptText.EditDigit(Document, _editedDigit, ++_edits);
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void TypePeriodUntilAFrameShowsTheMemberList()
    {
        FocusedInput.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "." });
        RenderList();
    }

    [Benchmark]
    [BenchmarkCategory("CI")]
    public void CtrlSpaceUntilAFrameShowsTheList()
    {
        var pressed = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Space,
            KeyModifiers = KeyModifiers.Control,
        };
        // As the keyboard device does before routing the key event.
        foreach (KeyBinding binding in Editor.KeyBindings)
        {
            binding.TryHandle(pressed);
            if (pressed.Handled)
                break;
        }
        if (!pressed.Handled)
            FocusedInput.RaiseEvent(pressed);
        RenderList();
    }

    private void RenderList()
    {
        Window window = Window;
        var wait = new DispatcherFrame();
        window.LayoutUpdated += OnLayout;
        try
        {
            using var timeout = DispatcherTimer.RunOnce(() => wait.Continue = false, TimeSpan.FromSeconds(10));
            Dispatcher.UIThread.PushFrame(wait);
        }
        finally
        {
            window.LayoutUpdated -= OnLayout;
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.GetLastRenderedFrame();
        CompletionListBox? list = CompletionBenchmarks.List(window);
        if (
            frame is null
            || list?.RowOf(list.SelectedIndex) is not { IsEffectivelyVisible: true, Item: not null } row
            || row.Bounds.Width <= 0
            || row.Bounds.Height <= 0
        )
            throw new InvalidOperationException("The frame does not show a selectable completion item.");

        void OnLayout(object? sender, EventArgs e)
        {
            if (CompletionBenchmarks.List(window)?.Items is { Count: > 0 })
                wait.Continue = false;
        }
    }

    [IterationCleanup(Target = nameof(TypePeriodUntilAFrameShowsTheMemberList))]
    public void CloseMemberList()
    {
        DismissList();
        Document.Replace(new TextRange(_caret, 1), "");
        HeadlessEditor.Render(Window);
    }

    [IterationCleanup(Target = nameof(CtrlSpaceUntilAFrameShowsTheList))]
    public void CloseList()
    {
        DismissList();
        HeadlessEditor.Render(Window);
    }

    private void DismissList()
    {
        Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
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
