using System.ComponentModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>
/// A document the user has open: only in memory, as the app's examples and new documents start, or with a file. The
/// <see cref="Workspace"/> owns it.
/// </summary>
public sealed partial class OpenDocument : ObservableObject, IDisposable
{
    public OpenDocument(string name, FileType type, CodeDocument document, LanguageServer? server, IStorageFile? file)
    {
        Name = name;
        Type = type;
        Document = document;
        Server = server;
        File = file;
        Document.PropertyChanged += OnDocumentChanged;
    }

    /// <summary>The text, with its undo history. Editors show it.</summary>
    public CodeDocument Document { get; }

    public FileType Type { get; }

    /// <summary>
    /// The language server that serves the document, for the status bar; null if its type has none or it isn't
    /// installed. The <see cref="Workspace"/> owns it.
    /// </summary>
    public LanguageServer? Server { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; private set; }

    /// <summary>The file the document is saved in; null while it is only in memory.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial IStorageFile? File { get; private set; }

    /// <summary>What the title bar calls the document: its file's full path, or its name while it has no file.</summary>
    public string Title => File is { } file ? file.TryGetLocalPath() ?? file.Path.ToString() : Name;

    /// <summary>Whether the text is in a file, and whether the file holds the latest text.</summary>
    public DocumentState State =>
        File is null ? DocumentState.NoFile
        : Document.IsModified ? DocumentState.Unsaved
        : DocumentState.Saved;

    /// <summary>
    /// Where the editor was in the document when it switched to another one: the caret, the selection and the scroll
    /// position. The document itself holds no view state.
    /// </summary>
    public CodeEditorViewState? ViewState { get; set; }

    /// <summary>Saves the text in <paramref name="file"/>, which then becomes the document's file.</summary>
    public async Task SaveAsync(IStorageFile file)
    {
        // The snapshot is the text as it is now. Edits made while the file is written keep the document modified.
        TextSnapshot snapshot = Document.CurrentSnapshot;
        await using (Stream stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(snapshot.GetText());
        Document.MarkUnmodified(snapshot);

        if (file == File)
            return;
        File?.Dispose();
        File = file;
        Name = file.Name;
        Document.FilePath = file.TryGetLocalPath();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Document.Dispose();
        File?.Dispose();
    }

    private void OnDocumentChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeDocument.IsModified))
            OnPropertyChanged(nameof(State));
    }
}

/// <summary>Whether a document's text is in a file, and whether the file holds the latest text.</summary>
public enum DocumentState
{
    /// <summary>The text is only in memory.</summary>
    NoFile,

    /// <summary>The file holds an older text: there are unsaved changes.</summary>
    Unsaved,

    /// <summary>The file holds the text.</summary>
    Saved,
}
