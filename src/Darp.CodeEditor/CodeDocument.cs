using System.ComponentModel;

namespace Darp.CodeEditor;

/// <summary>
/// The text of one script or file, with its language, its undo history and, when the language has a server, the
/// server's diagnostics. One or more <see cref="CodeEditor"/>s show it.
/// </summary>
/// <remarks>
/// <para>
/// The app owns a document and disposes it when the user closes it for good. Switching tabs or recreating views keeps
/// the document, and with it the undo history. Use it on the UI thread; it raises its events there.
/// </para>
/// <para>
/// A document whose language has a server is open in that server from creation until <see cref="Dispose"/>, whether
/// an editor shows it or not, and the server starts with the first such document. <see cref="Diagnostics"/> follow
/// the text. A document without a server holds nothing that needs disposing.
/// </para>
/// <para>
/// <see cref="PropertyChanged"/> reports <see cref="IsModified"/>, <see cref="CanUndo"/>, <see cref="CanRedo"/> and
/// <see cref="Diagnostics"/>, for bindings. It does not report <see cref="Text"/>: each read copies the whole text, so
/// a binding would copy it on every keystroke. Listen to <see cref="TextChanged"/> and read the text when needed.
/// </para>
/// </remarks>
public sealed class CodeDocument : INotifyPropertyChanged, IDisposable
{
    /// <summary>Creates a document with <paramref name="text"/> and an empty undo history.</summary>
    /// <param name="language">The language: highlighting, editing rules and the language server, if any.</param>
    /// <param name="text">The initial text.</param>
    /// <param name="filePath">
    /// The file the text belongs to, if any. Language servers that look at nearby files or configuration use it. The
    /// document never reads or writes it.
    /// </param>
    public CodeDocument(CodeLanguage language, string text = "", string? filePath = null) =>
        throw new NotImplementedException();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after every change of the text, by the user or by the app.</summary>
    public event EventHandler? TextChanged;

    /// <summary>The language: highlighting, editing rules and the language server, if any.</summary>
    public CodeLanguage Language => throw new NotImplementedException();

    /// <summary>The file the text belongs to, as given at creation.</summary>
    public string? FilePath => throw new NotImplementedException();

    /// <summary>The whole text. Setting it replaces the text in one step that the user can undo.</summary>
    public string Text
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>Counts changes: every change of the text increases it.</summary>
    public int Version => throw new NotImplementedException();

    /// <summary>
    /// Whether the text differs from the text at creation or at the last <see cref="MarkUnmodified"/>. Undoing back to
    /// that text clears it again.
    /// </summary>
    public bool IsModified => throw new NotImplementedException();

    /// <summary>Whether <see cref="Undo"/> has a step to undo.</summary>
    public bool CanUndo => throw new NotImplementedException();

    /// <summary>Whether <see cref="Redo"/> has a step to redo.</summary>
    public bool CanRedo => throw new NotImplementedException();

    /// <summary>
    /// The language server's latest diagnostics, with ranges in the current text: they move with edits until the
    /// server reports again. Empty without a server.
    /// </summary>
    public IReadOnlyList<Diagnostic> Diagnostics => throw new NotImplementedException();

    /// <summary>Makes the current text the unmodified one, for example after saving it.</summary>
    public void MarkUnmodified() => throw new NotImplementedException();

    /// <summary>Undoes the last step, as Ctrl+Z does.</summary>
    public void Undo() => throw new NotImplementedException();

    /// <summary>Redoes the last undone step, as Ctrl+Y does.</summary>
    public void Redo() => throw new NotImplementedException();

    /// <summary>The line and column of <paramref name="offset"/> in the current text.</summary>
    public TextPosition GetPosition(int offset) => throw new NotImplementedException();

    /// <summary>The offset of <paramref name="position"/> in the current text, clamped to the end of its line.</summary>
    public int GetOffset(TextPosition position) => throw new NotImplementedException();

    /// <summary>Closes the document in its language server. Editors that still show it become empty.</summary>
    public void Dispose() => throw new NotImplementedException();
}
