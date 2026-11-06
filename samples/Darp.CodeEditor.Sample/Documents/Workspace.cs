using System.Collections.ObjectModel;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Sample.Localization;

namespace Sample.Documents;

/// <summary>
/// The documents the user has open. It owns them: closing one disposes it, and disposing the workspace, when the app
/// exits, disposes the rest.
/// </summary>
public sealed partial class Workspace : ObservableObject, IDisposable
{
    private static readonly Uri s_examples = new("avares://Darp.CodeEditor.Sample/Documents/Examples/");

    private int _untitledCount;

    public ObservableCollection<OpenDocument> Documents { get; } = [];

    /// <summary>The document the editor shows.</summary>
    [ObservableProperty]
    public partial OpenDocument? Current { get; set; }

    /// <summary>Opens the app's examples. They live in memory, as scripts that an app keeps in its own storage do.</summary>
    public void OpenExamples()
    {
        foreach (Uri example in AssetLoader.GetAssets(s_examples, baseUri: null).OrderBy(uri => uri.AbsolutePath))
        {
            string fileName = Path.GetFileName(example.AbsolutePath);
            using var reader = new StreamReader(AssetLoader.Open(example));
            Add(
                new OpenDocument(
                    Path.GetFileNameWithoutExtension(fileName),
                    FileType.ForFile(fileName),
                    reader.ReadToEnd(),
                    file: null
                )
            );
        }
        Current = Documents.FirstOrDefault();
    }

    /// <summary>Creates an empty document in memory.</summary>
    public void New(FileType type)
    {
        string name = Strings.Current.Format(Strings.Current.Untitled, ++_untitledCount);
        Add(new OpenDocument(name, type, "", file: null));
    }

    /// <summary>Opens <paramref name="file"/>, or shows it if it is open already. The workspace owns the file.</summary>
    public async Task OpenAsync(IStorageFile file)
    {
        if (Documents.FirstOrDefault(document => document.File?.Path == file.Path) is { } open)
        {
            file.Dispose();
            Current = open;
            return;
        }
        string text;
        try
        {
            await using Stream stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            text = await reader.ReadToEndAsync();
        }
        catch
        {
            file.Dispose();
            throw;
        }
        Add(new OpenDocument(file.Name, FileType.ForFile(file.Name), text, file));
    }

    /// <summary>Closes <paramref name="document"/> for good, and shows its neighbor.</summary>
    public void Close(OpenDocument document)
    {
        int index = Documents.IndexOf(document);
        Documents.RemoveAt(index);
        if (Current == document)
            Current = Documents.Count == 0 ? null : Documents[Math.Min(index, Documents.Count - 1)];
        document.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Current = null;
        foreach (OpenDocument document in Documents)
            document.Dispose();
        Documents.Clear();
    }

    private void Add(OpenDocument document)
    {
        Documents.Add(document);
        Current = document;
    }
}
