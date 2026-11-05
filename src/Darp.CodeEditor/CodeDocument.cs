using System.ComponentModel;

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
/// Create, read and dispose a document on any thread. Change it only on the UI thread, where the user types and editors
/// draw: setting <see cref="Text"/>, <see cref="FilePath"/> or <see cref="Indentation"/>, <see cref="Undo"/>,
/// <see cref="Redo"/> and <see cref="MarkUnmodified"/> throw <see cref="InvalidOperationException"/> on other
/// threads. Events are raised on the UI thread, also for documents created elsewhere. To read the text and its version
/// together, take <see cref="CurrentSnapshot"/>.
/// </para>
/// <para>
/// The constructor creates a document without a server. A server creates its own, with
/// <see cref="LanguageServer.CreateDocument"/> or the C# script server's <c>CreateDocument</c>; such a document is
/// open in that server from creation until <see cref="Dispose"/>, whether an editor shows it or not, and the server
/// starts with its first document. Neither creating nor disposing a document waits for the server. A document without
/// a server holds nothing that needs disposing.
/// </para>
/// <para>
/// A document remembers the caret, selection and first visible line of the editor that showed it last, and an editor
/// that shows it again starts there.
/// </para>
/// <para>
/// <see cref="PropertyChanged"/> reports <see cref="IsModified"/>, <see cref="CanUndo"/>, <see cref="CanRedo"/>,
/// <see cref="Indentation"/> and
/// <see cref="Diagnostics"/>, for bindings. It does not report <see cref="Text"/>: each read copies the whole text, so
/// a binding would copy it on every keystroke. Listen to <see cref="TextChanged"/> and read the text when needed.
/// </para>
/// </remarks>
public sealed class CodeDocument : INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Creates a document without a language server, with <paramref name="text"/> and an empty undo history.
    /// </summary>
    /// <param name="language">The language: highlighting and editing rules.</param>
    /// <param name="text">The initial text.</param>
    public CodeDocument(CodeLanguage language, string text = "") => throw new NotImplementedException();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every change of the text, by the user or by the app.</summary>
    public event EventHandler? TextChanged;

    /// <summary>The language: highlighting and editing rules.</summary>
    public CodeLanguage Language => throw new NotImplementedException();

    /// <summary>The server that created the document and provides its language features; null for none.</summary>
    public LanguageServer? Server => throw new NotImplementedException();

    /// <summary>
    /// The file the text belongs to, if any. Setting it, for example after "Save as", moves the document to the new
    /// path in its language server and keeps its text and undo history.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Another open document of the same language server has the new path.
    /// </exception>
    public string? FilePath
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>
    /// The whole text, as <c>CurrentSnapshot.GetText()</c>. Setting it replaces the text in one step that the user can
    /// undo.
    /// </summary>
    public string Text
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>The text as it is now, with its version.</summary>
    public TextSnapshot CurrentSnapshot => throw new NotImplementedException();

    /// <summary>
    /// How Tab and new lines indent, in every editor that shows the document. It starts as in VS Code: 2 spaces for
    /// YAML, 4 spaces otherwise. The document does not detect it from the text. Changing it leaves the existing text
    /// as it is.
    /// </summary>
    public Indentation Indentation
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>
    /// Whether the document moved away from its unmodified point in the undo history: creation, or the last
    /// <see cref="MarkUnmodified"/>. Undoing back to that point clears it; typing the same text again does not.
    /// </summary>
    public bool IsModified => throw new NotImplementedException();

    /// <summary>Whether <see cref="Undo"/> has a step to undo.</summary>
    public bool CanUndo => throw new NotImplementedException();

    /// <summary>Whether <see cref="Redo"/> has a step to redo.</summary>
    public bool CanRedo => throw new NotImplementedException();

    /// <summary>
    /// The language server's latest diagnostics, with ranges in the text the server analyzed. Servers report again
    /// shortly after each change; meanwhile the editor's squiggles move with the text, these records do not. Empty
    /// without a server.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => throw new NotImplementedException();

    /// <summary>
    /// Makes <paramref name="snapshot"/> the unmodified point, for example after saving its text.
    /// </summary>
    /// <returns>
    /// False, with no change, when the text changed since <paramref name="snapshot"/>: the saved text is no longer the
    /// document's, so the document stays modified.
    /// </returns>
    /// <exception cref="ArgumentException">The snapshot is of another document.</exception>
    public bool MarkUnmodified(TextSnapshot snapshot) => throw new NotImplementedException();

    /// <summary>Undoes the last step, as Ctrl+Z does.</summary>
    public void Undo() => throw new NotImplementedException();

    /// <summary>Redoes the last undone step, as Ctrl+Y does.</summary>
    public void Redo() => throw new NotImplementedException();

    /// <summary>
    /// Closes the document in its language server, without waiting for it. Editors that still show it become empty.
    /// </summary>
    public void Dispose() => throw new NotImplementedException();
}
