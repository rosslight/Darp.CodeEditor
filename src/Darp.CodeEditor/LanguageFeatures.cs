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
/// It opens the editor's document in the service and sends every edit, so the service can keep its copy in step. The
/// service's methods only queue work (see <see cref="ILanguageService"/>); results come back on the UI thread through
/// the awaits here. Semantic colors are requested for the visible lines and a margin around them, after edits, when
/// scrolling reaches lines outside the margin, and when the service invalidates its results. Diagnostics arrive when
/// the service publishes them and are shown only for the current version. Rendering reads only
/// <see cref="SemanticColorizer"/> and <see cref="DiagnosticRenderer"/>.
/// </remarks>
internal sealed class LanguageFeatures
{
    private static readonly TimeSpan s_refreshDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_scrollDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>The lines classified before the first layout tells which are visible.</summary>
    private const int LinesBeforeLayout = 100;

    private readonly CodeEditor _editor;
    private readonly SemanticColorizer _colorizer = new();
    private readonly DiagnosticRenderer _diagnostics = new();
    private readonly CompletionController _completion;
    private readonly SignatureHelpController _signatureHelp;
    private readonly DispatcherTimer _refreshTimer = new(DispatcherPriority.Background) { Interval = s_refreshDelay };
    private readonly DispatcherTimer _scrollTimer = new(DispatcherPriority.Background) { Interval = s_scrollDelay };
    private ILanguageService? _service;
    private CancellationTokenSource? _refreshCancellation;
    private TextDocument? _document;
    private string? _openDocumentId;
    private int _version;
    private long _changedAt;

    /// <summary>The version and range of the last classification request.</summary>
    private (int Version, TextRange Range)? _classified;

    public LanguageFeatures(CodeEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.LineTransformers.Add(_colorizer);
        editor.TextArea.TextView.BackgroundRenderers.Add(_diagnostics);
        _completion = new CompletionController(editor, this);
        _signatureHelp = new SignatureHelpController(editor, this);
        _ = new HoverController(editor, this);
        _refreshTimer.Tick += (_, _) => Refresh();
        _scrollTimer.Tick += (_, _) =>
        {
            _scrollTimer.Stop();
            if (NeedsClassification())
                Refresh();
        };
        editor.TextArea.TextView.VisualLinesChanged += (_, _) =>
        {
            if (NeedsClassification())
                _scrollTimer.Start();
        };
        editor.DocumentChanged += (_, _) => AttachDocument();
        AttachDocument();
    }

    public ILanguageService? Service
    {
        get => _service;
        set
        {
            if (value == _service)
                return;
            if (_service is { } previous)
            {
                previous.Invalidated -= OnInvalidated;
                previous.DiagnosticsChanged -= OnDiagnosticsChanged;
                CloseDocument();
            }
            _service = value;
            _completion.Cancel();
            _signatureHelp.Close();
            ClearResults();
            if (value is null)
                return;
            value.Invalidated += OnInvalidated;
            value.DiagnosticsChanged += OnDiagnosticsChanged;
            OpenDocument();
            Refresh();
        }
    }

    /// <summary>The current version of the document, and a checkpoint to map offsets of its results forward.</summary>
    public (DocumentVersion Document, ITextSourceVersion Checkpoint) CreateSnapshot() =>
        (new DocumentVersion(_openDocumentId ?? _editor.DocumentId, _version), _editor.Document.Version);

    public IEnumerable<DiagnosticInfo> DiagnosticsAt(int offset) => _diagnostics.At(offset);

    /// <summary>Opens the document again under its new id.</summary>
    public void OnDocumentIdChanged()
    {
        if (_openDocumentId is null)
            return;
        CloseDocument();
        ClearResults();
        OpenDocument();
        Refresh();
    }

    /// <summary>Calls the service and returns its result. Null when the request was cancelled or failed.</summary>
    /// <remarks>Failures are reported through <see cref="CodeEditor.LanguageServiceFailed"/>.</remarks>
    public async Task<T?> RequestAsync<T>(Func<Task<T>> request, CancellationToken cancellationToken)
        where T : class?
    {
        try
        {
            return await request();
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

    private void AttachDocument()
    {
        if (_document is not null)
            _document.Changed -= OnDocumentChanged;
        CloseDocument();
        _document = _editor.Document;
        _document?.Changed += OnDocumentChanged;
        _version++;
        ClearResults();
        if (_service is null)
            return;
        OpenDocument();
        Refresh();
    }

    private void OpenDocument()
    {
        if (_service is not { } service || _document is not { } document)
            return;
        _openDocumentId = _editor.DocumentId;
        _changedAt = Stopwatch.GetTimestamp();
        service.OpenDocument(new DocumentSnapshot(_openDocumentId, _version, document.Text));
    }

    private void CloseDocument()
    {
        if (_openDocumentId is { } id)
            _service?.CloseDocument(id);
        _openDocumentId = null;
    }

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _version++;
        _changedAt = Stopwatch.GetTimestamp();
        if (_service is { } service && _openDocumentId is { } id)
        {
            service.ChangeDocument(
                new DocumentChange(id, _version, new TextChange(e.Offset, e.RemovalLength, e.InsertedText.Text))
            );
        }
        // A keystroke cancels document-wide requests. Cached results stay and move with the text until new ones arrive.
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        if (_service is null)
            return;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void OnInvalidated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (sender == _service)
                Refresh();
        });

    private void OnDiagnosticsChanged(object? sender, DiagnosticsChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            // Diagnostics of an older version are dropped; the service publishes the current one's later.
            if (sender != _service || e.DocumentId != _openDocumentId || e.Version != _version)
                return;
            _diagnostics.Update(_editor.Document, e.Diagnostics);
            _editor.TextArea.TextView.InvalidateLayer(_diagnostics.Layer);
            _editor.ReportTiming("diagnostics", Stopwatch.GetElapsedTime(_changedAt), e.Diagnostics.Count);
        });

    private void ClearResults()
    {
        _refreshTimer.Stop();
        _scrollTimer.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation = null;
        _classified = null;
        _colorizer.Clear();
        _diagnostics.Clear();
        _editor.TextArea.TextView.Redraw();
    }

    private void Refresh()
    {
        _refreshTimer.Stop();
        if (_service is not { } service || _openDocumentId is null)
            return;
        var cancellation = new CancellationTokenSource();
        _refreshCancellation?.Cancel();
        _refreshCancellation = cancellation;
        _ = RefreshClassificationsAsync(service, cancellation.Token);
    }

    private async Task RefreshClassificationsAsync(ILanguageService service, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        (DocumentVersion document, _) = CreateSnapshot();
        TextRange range = GetClassificationRange();
        _classified = (document.Version, range);
        IReadOnlyList<ClassifiedRange>? ranges = await RequestAsync(
            () => service.GetClassificationsAsync(document, range, cancellationToken),
            cancellationToken
        );
        // Cancellation means the text changed after the request; a newer refresh is scheduled.
        if (ranges is null || cancellationToken.IsCancellationRequested || document.Version != _version)
            return;
        _colorizer.Update(_editor.Document, ranges);
        _editor.TextArea.TextView.Redraw();
        _editor.ReportTiming("classification", Stopwatch.GetElapsedTime(start), ranges.Count);
    }

    /// <summary>Whether scrolling showed lines outside the last classified range of the current version.</summary>
    private bool NeedsClassification() =>
        _service is not null
        && _openDocumentId is not null
        && _classified is { } classified
        && classified.Version == _version
        && GetVisibleLines() is { } visible
        && (visible.Start < classified.Range.Start || visible.End > classified.Range.End);

    /// <summary>The visible lines and as many lines above and below them, or the first lines before the first layout.</summary>
    private TextRange GetClassificationRange()
    {
        TextDocument document = _editor.Document;
        TextView textView = _editor.TextArea.TextView;
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
        {
            DocumentLine last = document.GetLineByNumber(Math.Min(document.LineCount, LinesBeforeLayout));
            return TextRange.FromBounds(0, last.EndOffset);
        }
        int first = textView.VisualLines[0].FirstDocumentLine.LineNumber;
        int final = textView.VisualLines[^1].LastDocumentLine.LineNumber;
        int margin = final - first + 1;
        return TextRange.FromBounds(
            document.GetLineByNumber(Math.Max(1, first - margin)).Offset,
            document.GetLineByNumber(Math.Min(document.LineCount, final + margin)).EndOffset
        );
    }

    private TextRange? GetVisibleLines()
    {
        TextView textView = _editor.TextArea.TextView;
        if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return null;
        return TextRange.FromBounds(
            textView.VisualLines[0].FirstDocumentLine.Offset,
            textView.VisualLines[^1].LastDocumentLine.EndOffset
        );
    }
}
