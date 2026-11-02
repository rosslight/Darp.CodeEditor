using System.Diagnostics;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Markers;

namespace Darp.CodeEditor;

/// <summary>
/// Connects one editor to its language service. Lives on the UI thread.
/// </summary>
/// <remarks>
/// Every service call goes through <see cref="Task.Run{TResult}(Func{Task{TResult}}, CancellationToken)"/>, so a
/// service that does synchronous work before its first await still cannot block the UI thread. Rendering only reads
/// <see cref="SemanticColorizer"/> and <see cref="DiagnosticRenderer"/>, which are filled when results arrive.
/// </remarks>
internal sealed class LanguageFeatures
{
    private static readonly TimeSpan s_refreshDelay = TimeSpan.FromMilliseconds(250);

    private readonly CodeEditor _editor;
    private readonly SemanticColorizer _colorizer = new();
    private readonly DiagnosticRenderer _diagnostics = new();
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signatureHelp;
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background) { Interval = s_refreshDelay };
    private ILanguageService? _service;
    private CancellationTokenSource? _refreshCancellation;
    private TextDocument? _document;
    private int _version;

    public LanguageFeatures(CodeEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        _completion = new CompletionController(editor, this);
        _signatureHelp = new SignatureHelpController(editor, this);
        _ = new HoverController(editor, this);
        _refreshTimer.Tick += (_, _) => Refresh();
        editor.DocumentChanged += (_, _) => AttachDocument();
        AttachDocument();
    }

    public ILanguageService? Service
    {
        get => _service;
        set
        {
            _service = value;
            _completion.Cancel();
            _signatureHelp.Close();
            ClearResults();
            if (value is not null)
                Refresh();
        }
    }

    /// <summary>Creates a snapshot of the current text, and a checkpoint to map offsets of its results forward.</summary>
    public (DocumentSnapshot Snapshot, ITextSourceVersion Checkpoint) CreateSnapshot()
    {
        TextDocument document = _editor.Document;
        return (new DocumentSnapshot(_editor.DocumentId, _version, document.Text), document.Version);
    }

    public IEnumerable<DiagnosticInfo> DiagnosticsAt(int offset) => _diagnostics.At(offset);

    private void AttachDocument()
    {
        if (_document is not null)
            _document.Changed -= OnDocumentChanged;
        _document = _editor.Document;
        _document?.Changed += OnDocumentChanged;
        _version++;
        ClearResults();
        if (_service is not null)
            Refresh();
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _version++;
        // A keystroke cancels document-wide requests. Cached results stay and move with the text until new ones arrive.
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        if (_service is null)
            return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void ClearResults()
    {
        _refreshTimer.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _colorizer.Clear();
        _diagnostics.Clear();
        _editor.TextArea.TextView.Redraw();
    }

    private void Refresh()
    {
        _refreshTimer.Stop();
        if (_service is not { } service)
            return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation?.Cancel();
        _refreshCancellation = cancellation;
        (DocumentSnapshot snapshot, _) = CreateSnapshot();
        _ = RefreshClassificationsAsync(service, snapshot, cancellation.Token);
        _ = RefreshDiagnosticsAsync(service, snapshot, cancellation.Token);
    }

    private async Task RefreshClassificationsAsync(
        ILanguageService service,
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        long start = Stopwatch.GetTimestamp();
        var range = new TextRange(0, snapshot.Text.Length);
        IReadOnlyList<ClassifiedRange>? ranges = await RequestAsync(
            () => service.GetClassificationsAsync(snapshot, range, cancellationToken),
            cancellationToken
        );
        // Cancellation means the text changed after the snapshot; a newer refresh is scheduled.
        if (ranges is null || cancellationToken.IsCancellationRequested)
            return;
        _colorizer.Update(_editor.Document, ranges);
        _editor.TextArea.TextView.Redraw();
        _editor.ReportTiming("classification", Stopwatch.GetElapsedTime(start), ranges.Count);
    }

    private async Task RefreshDiagnosticsAsync(
        ILanguageService service,
        DocumentSnapshot snapshot,
        CancellationToken cancellationToken
    )
    {
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<DiagnosticInfo>? diagnostics = await RequestAsync(
            () => service.GetDiagnosticsAsync(snapshot, cancellationToken),
            cancellationToken
        );
        if (diagnostics is null || cancellationToken.IsCancellationRequested)
            return;
        _diagnostics.Update(_editor.Document, diagnostics);
        _editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
        _editor.ReportTiming("diagnostics", Stopwatch.GetElapsedTime(start), diagnostics.Count);
    }

    /// <summary>
    /// Runs a service request on the thread pool and returns its result on the UI thread. Returns null when the request
    /// was cancelled or failed; failures are reported through <see cref="CodeEditor.LanguageServiceFailed"/>.
    /// </summary>
    public async Task<T?> RequestAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
        where T : class?
    {
        try
        {
            return await Task.Run(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _editor.ReportFailure(exception);
            return null;
        }
    }
}
