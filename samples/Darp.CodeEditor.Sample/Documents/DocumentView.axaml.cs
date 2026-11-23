using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using Sample.Localization;
using Sample.ScriptApi;
using StreamJsonRpc;

namespace Sample.Documents;

/// <summary>
/// The document page: one editor for every open document. Its public members are what the title bar and the status
/// bar need from the editor.
/// </summary>
public sealed partial class DocumentView : UserControl
{
    /// <summary>Defines the <see cref="CaretPosition"/> property.</summary>
    public static readonly DirectProperty<DocumentView, TextPosition> CaretPositionProperty =
        AvaloniaProperty.RegisterDirect<DocumentView, TextPosition>(nameof(CaretPosition), view => view.CaretPosition);

    private Workspace? _workspace;

    /// <summary>The document the editor shows.</summary>
    private OpenDocument? _shown;

    public DocumentView()
    {
        InitializeComponent();
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == CodeEditor.CaretPositionProperty)
                RaisePropertyChanged(CaretPositionProperty, default, CaretPosition);
        };
    }

    private void OnCut(object? sender, RoutedEventArgs e) => Editor.Cut();

    private void OnCopy(object? sender, RoutedEventArgs e) => Editor.Copy();

    private void OnPaste(object? sender, RoutedEventArgs e) => Editor.Paste();

    private void OnSelectAll(object? sender, RoutedEventArgs e) => Editor.SelectAll();

    /// <summary>The caret's line and column, for the status bar.</summary>
    public TextPosition CaretPosition => Editor.CaretPosition;

    /// <summary>
    /// Compiles the shown script and runs it in a session of its own, until it ends, the user stops it or its document
    /// closes. Each script keeps its run and output while the user looks at other documents.
    /// </summary>
    private async void OnRunScript(object? sender, RoutedEventArgs e)
    {
        if (_shown is not { Document: CSharpScriptDocument<ReportGlobals> document, ScriptStop: null } tab)
            return;
        using var stop = new CancellationTokenSource();
        // A queue per run: a session's last lines may arrive after its end, and must not join the next run's.
        Queue<string> lines = new();
        tab.ScriptStop = stop;
        tab.ScriptOutput = lines;
        ShowScriptState();
        void Output(string line) =>
            Dispatcher.UIThread.Post(() => AddOutput(tab, lines, line), DispatcherPriority.Background);
        try
        {
            CSharpScriptCompilation<ReportGlobals> compilation = await document.CompileAsync(stop.Token);
            if (compilation.Script is not { } script)
            {
                foreach (Diagnostic error in compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    Output($"{error.Start.Line},{error.Start.Column}: {error.Message}");
                return;
            }
            await using CSharpScriptSession<IReportSession, ReportSession> session =
                await CSharpScriptSession.StartAsync<IReportApp, IReportSession, ReportSession>(
                    new ReportApp(),
                    new CSharpScriptSessionOptions { Output = Output },
                    stop.Token
                );
            CSharpScriptRun run = await session.StartScriptAsync(script, stop.Token);
            await run.RunToEndAsync(stop.Token, killSessionAfter: TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            Output(Strings.Default.ScriptStopped);
        }
        catch (CSharpScriptException failure)
        {
            Output($"{failure.ScriptPosition?.Line}: {failure.ScriptExceptionType}: {failure.Message}");
        }
        catch (Exception failure) when (failure is ConnectionLostException or InvalidOperationException)
        {
            Output(failure.Message);
        }
        finally
        {
            tab.ScriptStop = null;
            ShowScriptState();
        }
    }

    private void OnStopScript(object? sender, RoutedEventArgs e) => _shown?.ScriptStop?.Cancel();

    private void AddOutput(OpenDocument tab, Queue<string> lines, string line)
    {
        const int maxLines = 200;
        if (lines.Count == maxLines)
            lines.Dequeue();
        lines.Enqueue(line);
        if (tab == _shown && lines == tab.ScriptOutput)
            ShowScriptState();
    }

    /// <summary>Shows the run and the output of the shown document, if it is a script.</summary>
    private void ShowScriptState()
    {
        ScriptControls.IsVisible = _shown?.Document is CSharpScriptDocument<ReportGlobals>;
        RunButton.IsEnabled = _shown?.ScriptStop is null;
        StopButton.IsEnabled = _shown?.ScriptStop is not null;
        ScriptOutput.Text = _shown is null ? "" : string.Join(Environment.NewLine, _shown.ScriptOutput);
        // Keeps the newest line in view.
        ScriptOutput.CaretIndex = ScriptOutput.Text.Length;
    }

    /// <summary>The sample's scripts ask nothing of the app.</summary>
    private sealed class ReportApp : IReportApp;

    public void Undo()
    {
        Editor.Document?.Undo();
        Editor.Focus();
    }

    public void Redo()
    {
        Editor.Document?.Redo();
        Editor.Focus();
    }

    public void Find() => Editor.OpenSearch();

    public void Replace() => Editor.OpenSearch(replace: true);

    public void InsertDateTime()
    {
        // Replaces the selection, as typing does, in one step that the user can undo.
        Editor.Document?.Replace(Editor.Selection.Range, DateTime.Now.ToString("g", Strings.Default.Culture));
        Editor.Focus();
    }

    /// <summary>Puts the caret at the start of <paramref name="line"/>, counted from 1, and scrolls it into view.</summary>
    public void GoToLine(int line)
    {
        if (Editor.Document is not { } document)
            return;
        int offset = document.CurrentSnapshot.GetOffset(new TextPosition(line, 1));
        Editor.Selection = new TextSelection(offset);
        Editor.ScrollTo(new TextRange(offset, 0));
        Editor.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_workspace is not null)
            _workspace.PropertyChanged -= OnWorkspaceChanged;
        _workspace = (DataContext as MainViewModel)?.Workspace;
        (DataContext as MainViewModel)?.Settings.Hotkeys.Use(Editor.KeyBindings);
        if (_workspace is not null)
            _workspace.PropertyChanged += OnWorkspaceChanged;
        Show(_workspace?.Current);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Workspace.Current))
            Show(_workspace?.Current);
    }

    /// <summary>
    /// Shows <paramref name="document"/> where the user left it. The caret, the selection and the scroll position
    /// belong to the editor, so the view saves them for the document it leaves and restores them for the one it shows.
    /// </summary>
    private void Show(OpenDocument? document)
    {
        if (document == _shown)
            return;
        if (_shown is not null)
            _shown.ViewState = Editor.SaveViewState();
        _shown = document;
        ShowScriptState();
        Editor.Document = document?.Document;
        if (document?.ViewState is { } state)
            Editor.RestoreViewState(state);
        Editor.Focus();
    }
}
