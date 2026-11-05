using System.ComponentModel;
using Avalonia;
using Avalonia.Threading;
using AvaloniaEdit.Document;

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

    private protected CodeDocument(CodeLanguage language, string text)
    {
        Language = language;
        _indentation = language.Id == "yaml" ? Indentation.Spaces(2) : Indentation.Spaces(4);
        _document = new TextDocument(text);
        _currentSnapshot = new TextSnapshot(_document.CreateSnapshot(), 0);
        // Application.Current is null before setup, so reading it ties no dispatcher to this thread.
        _owner = Application.Current?.Dispatcher;
        // AvaloniaEdit's document belongs to the thread that created it. Unbound, it belongs to none between changes.
        _document.SetOwnerThread(_owner?.Thread);
        _document.TextChanged += OnTextChanged;
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
    /// How Tab and new lines indent, in every editor that shows the document. It starts as in VS Code: 2 spaces for
    /// YAML, 4 spaces otherwise. The document does not detect it from the text. Changing it leaves the existing text
    /// as it is.
    /// </summary>
    public Indentation Indentation
    {
        get => _indentation;
        set
        {
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
    public IReadOnlyList<Diagnostic> Diagnostics => [];

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
    public TextRange MapToCurrent(TextSnapshot snapshot, TextRange range)
    {
        (ITextSourceVersion from, ITextSourceVersion to) = Versions(snapshot);
        (int start, int length) = snapshot.Clamp(range);
        // Text typed at either edge of the range stays outside it.
        int newStart = from.MoveOffsetTo(to, start, AnchorMovementType.AfterInsertion);
        int newEnd = from.MoveOffsetTo(to, start + length, AnchorMovementType.BeforeInsertion);
        return TextRange.FromBounds(newStart, Math.Max(newStart, newEnd));
    }

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
        _currentSnapshot = new TextSnapshot(_document.CreateSnapshot(), _currentSnapshot.Version + 1);
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

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

/// <summary>A document without a language server, from <see cref="CodeDocument.Create"/>.</summary>
internal sealed class StandaloneDocument(CodeLanguage language, string text) : CodeDocument(language, text);
