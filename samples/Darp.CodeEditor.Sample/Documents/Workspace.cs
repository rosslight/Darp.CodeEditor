using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;
using Sample.Localization;

namespace Sample.Documents;

/// <summary>
/// The documents the user has open, and the language servers that serve them. It owns both: closing a document
/// disposes it, and disposing the workspace, when the app exits, disposes the rest and stops the servers.
/// </summary>
public sealed partial class Workspace : ObservableObject, IDisposable
{
    private static readonly Uri s_examples = new("avares://Darp.CodeEditor.Sample/Documents/Examples/");

    /// <summary>Compares file paths as the system does: Windows ignores case.</summary>
    private static readonly StringComparer s_paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// One language server for each file type whose server is installed, for as long as the app runs. Each one starts
    /// with its first document.
    /// </summary>
    private readonly Dictionary<FileType, LanguageServer> _servers = [];

    private int _untitledCount;

    public Workspace()
    {
        foreach (FileType type in FileType.All)
        {
            if (type.ServerName is not { } name || FindOnPath(name) is not { } path)
                continue;
            _servers[type] = new LanguageServer(
                new LanguageServerOptions
                {
                    FileName = path,
                    Arguments = ["--stdio"],
                    Settings = ServerSettings.Get,
                    // Servers explain their failures there: into the app's log, with Avalonia's messages.
                    StandardError = line => Trace.WriteLine(line, name),
                }
            );
        }
    }

    public ObservableCollection<OpenDocument> Documents { get; } = [];

    /// <summary>The document the editor shows.</summary>
    [ObservableProperty]
    public partial OpenDocument? Current { get; set; }

    /// <summary>
    /// Opens the app's examples. They live in memory, as scripts that an app keeps in its own storage do, with a path
    /// next to the app that nothing reads or writes: servers associate schemas with documents by their file names,
    /// and see a document without a path only as <c>untitled:Untitled-1</c>, <c>-2</c> and so on.
    /// </summary>
    public void OpenExamples()
    {
        foreach (Uri example in AssetLoader.GetAssets(s_examples, baseUri: null).OrderBy(uri => uri.AbsolutePath))
        {
            string fileName = Path.GetFileName(example.AbsolutePath);
            using var reader = new StreamReader(AssetLoader.Open(example));
            Add(
                Path.GetFileNameWithoutExtension(fileName),
                FileType.ForFile(fileName),
                reader.ReadToEnd(),
                file: null,
                filePath: Path.Combine(AppContext.BaseDirectory, "Examples", fileName)
            );
        }
        Current = Documents.FirstOrDefault();
    }

    /// <summary>Creates an empty document in memory.</summary>
    public void New(FileType type)
    {
        string name = Strings.Current.Format(Strings.Current.Untitled, ++_untitledCount);
        Add(name, type, "", file: null, filePath: null);
    }

    /// <summary>
    /// Opens <paramref name="file"/>, or shows the document that has its path already. The workspace owns the file.
    /// </summary>
    /// <exception cref="IOException">
    /// The file can't be read, or an example has its path: the example's text is not the file's.
    /// </exception>
    public async Task OpenAsync(IStorageFile file)
    {
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
        // After the last await, so that no other open can take the path before the document has it.
        string? path = file.TryGetLocalPath();
        OpenDocument? open = path is not null
            ? WithPath(path)
            : Documents.FirstOrDefault(document => document.File?.Path == file.Path);
        if (open is not null)
        {
            file.Dispose();
            if (open.File is null)
                throw new IOException(Strings.Current.Format(Strings.Current.ExampleHasPath, open.Name));
            Current = open;
            return;
        }
        Add(file.Name, FileType.ForFile(file.Name), text, file, path);
    }

    /// <summary>
    /// The open document, other than <paramref name="except"/>, that has the file <paramref name="path"/>; null if
    /// there is none. A path belongs to one document at a time: a language server serves one document per file.
    /// </summary>
    public OpenDocument? WithPath(string path, OpenDocument? except = null) =>
        Documents.FirstOrDefault(open =>
            open != except && open.Document.FilePath is { } other && s_paths.Equals(other, path)
        );

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
        // The app exits, where only synchronous disposal runs: Dispose ends each server's process at once.
        foreach (LanguageServer server in _servers.Values)
            server.Dispose();
    }

    /// <summary>
    /// Adds a document and shows it. A file type whose server is installed gets its documents from that server, which
    /// serves them from then on; the others get documents without a server.
    /// </summary>
    private void Add(string name, FileType type, string text, IStorageFile? file, string? filePath)
    {
        LanguageServer? server = _servers.GetValueOrDefault(type);
        CodeDocument document;
        if (server is not null)
            document = server.CreateDocument(type.Language, text, filePath);
        else
        {
            document = CodeDocument.Create(type.Language, text);
            document.FilePath = filePath;
        }
        var open = new OpenDocument(name, type, document, server, file);
        Documents.Add(open);
        Current = open;
    }

    /// <summary>
    /// The executable <paramref name="name"/> in a folder on PATH, where npm installs servers; null if it is not
    /// installed. On Windows, npm installs a <c>.cmd</c> script, which starts only by its full name.
    /// </summary>
    private static string? FindOnPath(string name)
    {
        string[] fileNames = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd"] : [name];
        string[] folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries
        );
        return folders
            .SelectMany(folder => fileNames.Select(fileName => Path.Combine(folder, fileName)))
            .FirstOrDefault(File.Exists);
    }
}
