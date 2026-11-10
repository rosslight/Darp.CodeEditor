using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor;

/// <summary>
/// The text of one script or file, with its language, its undo history and, when a server created it, the server's
/// diagnostics. One or more <see cref="CodeEditor"/>s show it.
/// </summary>
/// <remarks>
/// <para>
/// The app owns a document and disposes it when the user closes it for good. Switching tabs or recreating views keeps
/// the document, and with it the undo history.
/// </para>
/// <para>
/// Create, read and dispose a document on any thread. It belongs to the app's main UI thread, because AvaloniaEdit's
/// editor works only there. Change it only there, where the user types and editors draw: setting <see cref="Text"/>,
/// <see cref="FilePath"/> or <see cref="Indentation"/>, <see cref="Undo"/>, <see cref="Redo"/> and
/// <see cref="MarkUnmodified"/> throw <see cref="InvalidOperationException"/> on other threads. Events are raised there
/// too, also for documents created elsewhere. To read the text and its version together, take <see cref="CurrentSnapshot"/>. Before Avalonia is set
/// up there is no UI thread yet: the document leaves Avalonia's dispatcher alone, checks no thread and raises its
/// events where the change happens.
/// </para>
/// <para>
/// <see cref="Create"/> creates a document without a server. A server creates its own, with
/// <see cref="LanguageServer.CreateDocument"/> or the C# script server's <c>CreateDocument</c>, which returns a typed
/// C# script document; such a document is open in that server from creation until <see cref="Dispose"/>, whether an
/// editor shows it or not, and the server starts with its first document. Apps don't derive from this type. Neither
/// creating nor disposing a document waits for the server. Dispose every document, also one without a server.
/// </para>
/// <para>
/// A document holds no view state: the caret, the selection and the scroll position belong to each editor.
/// </para>
/// <para>
/// <see cref="PropertyChanged"/> reports <see cref="IsModified"/>, <see cref="CanUndo"/>, <see cref="CanRedo"/>,
/// <see cref="Indentation"/> and <see cref="Diagnostics"/>, for bindings. It does not report <see cref="Text"/>: each
/// read copies the whole text, so a binding would copy it on every keystroke. Listen to <see cref="TextChanged"/> and read the text when needed.
/// </para>
/// </remarks>
public abstract class CodeDocument : INotifyPropertyChanged, IDisposable
{
    private readonly TextDocument _document;

    /// <summary>The app's main UI dispatcher; null before Avalonia is set up, until the document binds to it.</summary>
    private Dispatcher? _owner;

    /// <summary>Replaced on the owner's thread after each change, so that any thread can read it.</summary>
    private TextSnapshot _currentSnapshot;

    private Indentation _indentation;

    private string? _filePath;

    /// <summary>Set by <see cref="Dispose"/> on any thread, read by editors on the UI thread.</summary>
    private volatile bool _isDisposed;

    /// <summary>Set on the UI thread, or where the server's diagnostics arrive before Avalonia is set up; read anywhere.</summary>
    private DiagnosticsBatch _diagnostics;

    /// <summary>
    /// The batch whose markers follow the text, from when the UI thread took it; null until it takes one.
    /// </summary>
    private DiagnosticsBatch? _followed;

    /// <summary>Diagnostics on their way to the UI thread. Only the latest is applied; null when none is on its way.</summary>
    private DiagnosticsBatch? _pendingDiagnostics;

    private protected CodeDocument(CodeLanguage language, string text, string? filePath = null)
    {
        Language = language;
        _filePath = filePath;
        _indentation = language.Id == "yaml" ? Indentation.Spaces(2) : Indentation.Spaces(4);
        _document = new TextDocument(text);
        _currentSnapshot = new TextSnapshot(_document.CreateSnapshot(), 0);
        _diagnostics = new DiagnosticsBatch([], _currentSnapshot);
        // Application.Current is null before setup, so reading it ties no dispatcher to this thread.
        _owner = Application.Current?.Dispatcher;
        // AvaloniaEdit's document belongs to the thread that created it. Unbound, it belongs to none between changes.
        _document.SetOwnerThread(_owner?.Thread);
        _document.Changing += (_, e) => OnChanging(_document, e);
        _document.TextChanged += OnTextChanged;
        _document.Changed += OnBufferChanged;
        _document.UndoStack.PropertyChanged += OnUndoStackPropertyChanged;
    }

    /// <summary>
    /// Creates a document without a language server, with <paramref name="text"/> and an empty undo history.
    /// </summary>
    /// <param name="language">The language: highlighting and editing rules.</param>
    /// <param name="text">The initial text.</param>
    public static CodeDocument Create(CodeLanguage language, string text = "") =>
        new StandaloneDocument(language, text);

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every change of the text, by the user or by the app.</summary>
    public event EventHandler? TextChanged;

    /// <summary>The language: highlighting and editing rules.</summary>
    public CodeLanguage Language { get; }

    /// <summary>
    /// The file the text belongs to, if any. Setting it, for example after "Save as", moves the document to the new
    /// path in its language server and keeps its text and undo history.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another open document of the same language server has the new path.
    /// </exception>
    public string? FilePath
    {
        get => _filePath;
        set
        {
            VerifyAccess();
            if (value == _filePath)
                return;
            OnFilePathChanging(value);
            _filePath = value;
        }
    }

    /// <summary>
    /// The whole text, as <c>CurrentSnapshot.GetText()</c>. Setting it replaces the text in one step that the user can
    /// undo.
    /// </summary>
    public string Text
    {
        get => _currentSnapshot.GetText();
        set => Change(document => document.Text = value);
    }

    /// <summary>The text as it is now, with its version.</summary>
    public TextSnapshot CurrentSnapshot => _currentSnapshot;

    /// <summary>
    /// How Tab and new lines indent, in every editor that shows the document, and how wide tabs are drawn. It starts
    /// as in VS Code: 2 spaces for YAML, 4 spaces otherwise. The document does not detect it from the text. Changing it
    /// leaves the existing text as it is, and editors apply it at once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The size is not from 1 to 1,000 columns.</exception>
    public Indentation Indentation
    {
        get => _indentation;
        set
        {
            // AvaloniaEdit's limits: an editor would fail on the value later, and elsewhere.
            ArgumentOutOfRangeException.ThrowIfLessThan(value.Size, 1, nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Size, 1_000, nameof(value));
            VerifyAccess();
            if (value == _indentation)
                return;
            _indentation = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indentation)));
        }
    }

    /// <summary>
    /// Whether the document moved away from its unmodified point in the undo history: creation, or the last
    /// <see cref="MarkUnmodified"/>. Undoing back to that point clears it; typing the same text again does not.
    /// </summary>
    public bool IsModified => !_document.UndoStack.IsOriginalFile;

    /// <summary>Whether <see cref="Undo"/> has a step to undo.</summary>
    public bool CanUndo => _document.UndoStack.CanUndo;

    /// <summary>Whether <see cref="Redo"/> has a step to redo.</summary>
    public bool CanRedo => _document.UndoStack.CanRedo;

    /// <summary>
    /// The language server's latest diagnostics, each with the snapshot its range refers to. Servers report again
    /// shortly after each change; meanwhile the editor's squiggles move with the text, these records do not. To select
    /// one in the current text, map it: <c>MapToCurrent(diagnostic.Snapshot, diagnostic.Range)</c>. Empty without a
    /// server.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics.All;

    /// <summary>
    /// Replaces <paramref name="range"/> with <paramref name="text"/>, in one step that the user can undo; an empty
    /// range inserts. The range is clamped to the text. Editors keep their scroll position, and a caret behind the
    /// range moves with the text.
    /// </summary>
    public void Replace(TextRange range, string text)
    {
        (int start, int length) = _currentSnapshot.Clamp(range);
        Change(document => document.Replace(start, length, text));
    }

    /// <summary>
    /// Maps <paramref name="range"/> in the text of <paramref name="snapshot"/> to the current text, following the
    /// edits made since: for example the range of a diagnostic or of a compile error after the user typed above it. A
    /// range whose text was deleted shrinks to where it was.
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot is of another document.</exception>
    public TextRange MapToCurrent(TextSnapshot snapshot, TextRange range) =>
        new SnapshotEdits(snapshot, _currentSnapshot).Map(range);

    /// <summary>
    /// Makes <paramref name="snapshot"/> the unmodified point, for example after saving its text.
    /// </summary>
    /// <returns>
    /// False, with no change, when the text changed since <paramref name="snapshot"/>: the saved text is no longer the
    /// document's, so the document stays modified.
    /// </returns>
    /// <exception cref="ArgumentException">The snapshot is of another document.</exception>
    public bool MarkUnmodified(TextSnapshot snapshot)
    {
        VerifyAccess();
        Versions(snapshot);
        if (snapshot.Version != _currentSnapshot.Version)
            return false;
        Change(document => document.UndoStack.MarkAsOriginalFile());
        return true;
    }

    /// <summary>Undoes the last step, as Ctrl+Z does.</summary>
    public void Undo() => Change(document => document.UndoStack.Undo());

    /// <summary>Redoes the last undone step, as Ctrl+Y does.</summary>
    public void Redo() => Change(document => document.UndoStack.Redo());

    /// <summary>
    /// Closes the document in its language server, without waiting for it. Editors that still show it become empty.
    /// </summary>
    public void Dispose()
    {
        _isDisposed = true;
        OnDisposed();
        Disposed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// AvaloniaEdit's buffer, for the editors that show the document. Binds an unbound document to the app's main UI
    /// thread first: editors work only there.
    /// </summary>
    internal TextDocument Buffer
    {
        get
        {
            VerifyAccess();
            return _document;
        }
    }

    /// <summary>Whether <see cref="Dispose"/> was called. Editors show nothing of a disposed document.</summary>
    internal bool IsDisposed => _isDisposed;

    /// <summary>Raised by <see cref="Dispose"/>, on the thread that disposes, for the editors that show the document.</summary>
    internal event EventHandler? Disposed;

    /// <summary>
    /// Raised on the UI thread when a server started or stopped serving the document, for the editors that show it:
    /// what they asked the server before is outdated, and they ask again.
    /// </summary>
    internal event EventHandler? LanguageFeaturesChanged;

    /// <summary>
    /// The server's hover at <paramref name="position"/> of <paramref name="snapshot"/>, the current text, for an
    /// editor; null when there is none, and when <paramref name="cancellationToken"/> cancelled it. Never waits on the
    /// calling thread.
    /// </summary>
    internal virtual Task<HoverInfo?> HoverAsync(
        TextSnapshot snapshot,
        TextPosition position,
        CancellationToken cancellationToken
    ) => Task.FromResult<HoverInfo?>(null);

    /// <summary>
    /// The server's semantic tokens of the current text, for an editor that shows the lines from
    /// <paramref name="start"/> to <paramref name="end"/>: of those lines, or of all <paramref name="lineCount"/> lines
    /// for a server that answers only for the whole text. Null when there are none, and when
    /// <paramref name="cancellationToken"/> cancelled them. Never waits on the calling thread.
    /// </summary>
    internal virtual Task<SemanticTokenLines?> SemanticTokensAsync(
        TextPosition start,
        TextPosition end,
        int lineCount,
        CancellationToken cancellationToken
    ) => Task.FromResult<SemanticTokenLines?>(null);

    /// <summary>
    /// Raises <see cref="LanguageFeaturesChanged"/> on the UI thread, from any thread. Before Avalonia is set up there
    /// are no editors to tell.
    /// </summary>
    internal void OnLanguageFeaturesChanged()
    {
        // Application.Current is null before setup, so reading it ties no dispatcher to this thread.
        if (Application.Current?.Dispatcher is { } dispatcher)
            dispatcher.Post(() => LanguageFeaturesChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// The diagnostics that editors show, <see cref="DiagnosticsBatch.Markers"/> of <see cref="Diagnostics"/>, in the
    /// current text, for all of them: each moves with every change of the text until the next diagnostics replace
    /// them, and text typed at either edge stays outside it. On the UI thread.
    /// </summary>
    internal TextSegmentCollection<DiagnosticMarker> ShownDiagnostics
    {
        get
        {
            DiagnosticsBatch batch = _diagnostics;
            // Taken before Avalonia was set up: followed from the first time an editor needs it.
            if (_followed != batch)
                Follow(batch);
            return batch.Markers;
        }
    }

    /// <summary>
    /// Replaces <see cref="Diagnostics"/>, from any thread: on the app's main UI thread, or where it is called before
    /// Avalonia is set up. Of the diagnostics that arrive before the UI thread gets to them, only the latest is applied.
    /// Those that editors show are chosen, and mapped to the latest text, here on the calling thread, which is the
    /// server's: the UI thread follows only the edits made since.
    /// </summary>
    internal void SetDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        // Read after the UI thread made it current, with every edit before it.
        var batch = new DiagnosticsBatch(diagnostics, Volatile.Read(ref _currentSnapshot));
        // Application.Current is null before setup, so reading it ties no dispatcher to this thread.
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            ApplyDiagnostics(batch, follow: false);
            return;
        }
        if (Interlocked.Exchange(ref _pendingDiagnostics, batch) is null)
        {
            dispatcher.Post(() =>
            {
                if (Interlocked.Exchange(ref _pendingDiagnostics, null) is { } latest)
                    ApplyDiagnostics(latest, follow: true);
            });
        }
    }

    /// <summary>
    /// Called for each part of a change, before it applies, on the thread that changes the text:
    /// <paramref name="buffer"/> still holds the text before the change.
    /// </summary>
    private protected virtual void OnChanging(TextDocument buffer, DocumentChangeEventArgs change) { }

    /// <summary>
    /// Called after a change of the text, on the thread that changed it, once <paramref name="snapshot"/> is current and
    /// before <see cref="TextChanged"/>.
    /// </summary>
    private protected virtual void OnChanged(TextSnapshot snapshot) { }

    /// <summary>Called on the UI thread before <see cref="FilePath"/> changes to <paramref name="path"/>; throws to refuse.</summary>
    private protected virtual void OnFilePathChanging(string? path) { }

    /// <summary>Called by <see cref="Dispose"/>, on the thread that disposes, maybe more than once.</summary>
    private protected virtual void OnDisposed() { }

    /// <summary>
    /// The versions in AvaloniaEdit of <paramref name="snapshot"/> and of the current text, after checking that the
    /// snapshot is one of this document.
    /// </summary>
    private (ITextSourceVersion Snapshot, ITextSourceVersion Current) Versions(TextSnapshot snapshot) =>
        snapshot.SourceVersion is { } version
        && _currentSnapshot.SourceVersion is { } current
        && version.BelongsToSameDocumentAs(current)
            ? (version, current)
            : throw new ArgumentException("The snapshot is of another document.", nameof(snapshot));

    /// <summary>Changes AvaloniaEdit's document on the owner's thread, or on any thread while the document is unbound.</summary>
    private void Change(Action<TextDocument> change)
    {
        VerifyAccess();
        if (_owner is not null)
        {
            change(_document);
            return;
        }
        _document.SetOwnerThread(Thread.CurrentThread);
        try
        {
            change(_document);
        }
        finally
        {
            _document.SetOwnerThread(null);
        }
    }

    /// <summary>
    /// Throws unless the current thread may change the document. An unbound document binds to the app's main UI
    /// thread as soon as Avalonia is set up.
    /// </summary>
    private void VerifyAccess()
    {
        if (_owner is null && Application.Current?.Dispatcher is { } main)
        {
            _document.SetOwnerThread(main.Thread);
            _owner = main;
        }
        if (_owner is not null && !_owner.CheckAccess())
            throw new InvalidOperationException("Change the document only on the app's main UI thread.");
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        // With every edit before it, for threads that follow the edits up to it.
        Volatile.Write(
            ref _currentSnapshot,
            new TextSnapshot(_document.CreateSnapshot(), _currentSnapshot.Version + 1)
        );
        OnChanged(_currentSnapshot);
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes <paramref name="batch"/>; on the UI thread, also <paramref name="follow"/>s the text with it from now on,
    /// whether an editor shows the document or not.
    /// </summary>
    private void ApplyDiagnostics(DiagnosticsBatch batch, bool follow)
    {
        if (batch.All.Count == 0 && _diagnostics.All.Count == 0)
            return;
        _diagnostics = batch;
        if (follow)
            Follow(batch);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Diagnostics)));
    }

    /// <summary>
    /// Moves the markers of <paramref name="batch"/> over the edits made since the server's thread made them, usually
    /// none, and with each edit from now on; the batch before stays where it is. On the UI thread.
    /// </summary>
    private void Follow(DiagnosticsBatch batch)
    {
        TextDocument buffer = Buffer;
        if (batch.Snapshot.SourceVersion is { } version)
        {
            // AvaloniaEdit's document records each of its changes as it raises it.
            foreach (TextChangeEventArgs edit in version.GetChangesTo(buffer.Version))
                batch.Markers.UpdateOffsets((DocumentChangeEventArgs)edit);
        }
        _followed = batch;
    }

    /// <summary>A part of a change, as it applies: the markers in the current text follow it.</summary>
    private void OnBufferChanged(object? sender, DocumentChangeEventArgs e) => _followed?.Markers.UpdateOffsets(e);

    private void OnUndoStackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? property = e.PropertyName switch
        {
            nameof(UndoStack.IsOriginalFile) => nameof(IsModified),
            nameof(UndoStack.CanUndo) => nameof(CanUndo),
            nameof(UndoStack.CanRedo) => nameof(CanRedo),
            _ => null,
        };
        if (property is not null)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}

/// <summary>A batch of a document's diagnostics, and those of them that editors show.</summary>
internal sealed class DiagnosticsBatch
{
    /// <summary>
    /// How many of a document's diagnostics editors show at most: a server may send far more, and each one shown costs
    /// the UI thread work when it is drawn and when the pointer rests on it.
    /// </summary>
    public const int MaxShown = 10_000;

    /// <summary>
    /// Takes the batch, chooses those shown and puts them in the text of <paramref name="snapshot"/>, on the calling
    /// thread.
    /// </summary>
    /// <exception cref="ArgumentException">A diagnostic is about the text of another document.</exception>
    public DiagnosticsBatch(IReadOnlyList<Diagnostic> all, TextSnapshot snapshot)
    {
        All = all;
        Snapshot = snapshot;
        // Following the edits once for each snapshot that the diagnostics are about: usually, one for all.
        SnapshotEdits? edits = null;
        int rank = 0;
        for (var severity = DiagnosticSeverity.Error; severity >= DiagnosticSeverity.Hint; severity--)
        {
            foreach (Diagnostic diagnostic in all)
            {
                if (rank == MaxShown)
                    break;
                if (diagnostic.Severity != severity)
                    continue;
                if (edits is null || edits.Snapshot != diagnostic.Snapshot)
                    edits = new SnapshotEdits(diagnostic.Snapshot, snapshot);
                TextRange range = edits.Map(diagnostic.Range);
                Markers.Add(
                    new DiagnosticMarker(diagnostic, rank++) { StartOffset = range.Start, Length = range.Length }
                );
            }
        }
    }

    /// <summary>All of the document's diagnostics, in the server's order.</summary>
    public IReadOnlyList<Diagnostic> All { get; }

    /// <summary>The text that <see cref="Markers"/> are in until the document follows the text with them.</summary>
    public TextSnapshot Snapshot { get; }

    /// <summary>
    /// The diagnostics that editors show: at most <see cref="MaxShown"/>, the most severe first, then in the server's
    /// order, which is their rank in a hover. No document moves them with its text until one takes the batch.
    /// </summary>
    public TextSegmentCollection<DiagnosticMarker> Markers { get; } = new();
}

/// <summary>A document without a language server, from <see cref="CodeDocument.Create"/>.</summary>
internal sealed class StandaloneDocument(CodeLanguage language, string text) : CodeDocument(language, text);
