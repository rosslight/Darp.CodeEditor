using System.ComponentModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;

namespace Sample.Documents;

/// <summary>
/// A document the user has open: in memory, as the app's examples and new documents are, or saved in a file. The
/// <see cref="Workspace"/> owns it.
/// </summary>
public sealed partial class OpenDocument : ObservableObject, IDisposable
{
    public OpenDocument(string name, FileType type, string text, IStorageFile? file)
    {
        Name = name;
        Type = type;
        File = file;
        Document = CodeDocument.Create(type.Language, text);
        Document.FilePath = file?.TryGetLocalPath();
        Document.PropertyChanged += OnDocumentChanged;
    }

    /// <summary>The text, with its undo history. Editors show it.</summary>
    public CodeDocument Document { get; }

    public FileType Type { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; private set; }

    /// <summary>The file the document is saved in; null while it is only in memory.</summary>
    [ObservableProperty]
    public partial IStorageFile? File { get; private set; }

    /// <summary>The name, with a dot while there are unsaved changes.</summary>
    public string Title => Document.IsModified ? $"{Name} ●" : Name;

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
            OnPropertyChanged(nameof(Title));
    }
}
