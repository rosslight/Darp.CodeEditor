using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    /// <summary>The servers' settings, with the schemas' text while they are open.</summary>
    private readonly ServerSettings _settings = new();

    /// <summary>
    /// Waits for a pause in the edits of a schema before the servers get it, as they validate every open document
    /// again with each new schema.
    /// </summary>
    private readonly DispatcherTimer _schemasChanged = new() { Interval = TimeSpan.FromMilliseconds(300) };

    private int _untitledCount;

    public Workspace()
    {
        _schemasChanged.Tick += (_, _) => SendSchemas();
        foreach (FileType type in FileType.All)
        {
            if (type.ServerName is not { } name || FindOnPath(name) is not { } path)
                continue;
            _servers[type] = new LanguageServer(
                new LanguageServerOptions
                {
                    FileName = path,
                    Arguments = ["--stdio"],
                    Settings = _settings.Get,
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
        string name = Strings.Default.FormatUntitled(++_untitledCount);
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
                throw new IOException(Strings.Default.FormatExampleHasPath(open.Name));
            Current = open;
            return;
        }
        Add(file.Name, FileType.ForFile(file.Name), text, file, path);
    }

    /// <summary>
    /// Opens the schema <paramref name="path"/>, one of <see cref="ServerSettings.Schemas"/>, or shows it if it is open.
    /// Its edits apply to the documents it validates as the server allows: VS Code's JSON server takes the text as the
    /// user types, the YAML server the file once saved.
    /// </summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    public async Task OpenSchemaAsync(string path, IStorageProvider storage)
    {
        if (WithPath(path) is { } open)
        {
            Current = open;
            return;
        }
        IStorageFile file = await storage.TryGetFileFromPathAsync(path) ?? throw new FileNotFoundException(null, path);
        await OpenAsync(file);
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
        document.Document.TextChanged -= OnTextChanged;
        document.PropertyChanged -= OnDocumentPropertyChanged;
        // A schema closed without saving goes back to its file.
        if (ServerSettings.IsSchema(document.Document.FilePath))
            _schemasChanged.Start();
        document.Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _schemasChanged.Stop();
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
        document.TextChanged += OnTextChanged;
        open.PropertyChanged += OnDocumentPropertyChanged;
        Documents.Add(open);
        Current = open;
        if (ServerSettings.IsSchema(filePath))
            _schemasChanged.Start();
    }

    /// <summary>An edit of a schema: the servers get its text after a pause.</summary>
    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is CodeDocument document && ServerSettings.IsSchema(document.FilePath))
        {
            // Again from the latest edit.
            _schemasChanged.Stop();
            _schemasChanged.Start();
        }
    }

    /// <summary>
    /// A schema saved, whose file the YAML server reads; or a document saved under another path, which may have been a
    /// schema or become one.
    /// </summary>
    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(OpenDocument.File)
            || e.PropertyName == nameof(OpenDocument.State)
                && sender is OpenDocument document
                && ServerSettings.IsSchema(document.Document.FilePath)
        )
            _schemasChanged.Start();
    }

    /// <summary>Gives the servers that validate with the schemas the schemas as they are now.</summary>
    private void SendSchemas()
    {
        _schemasChanged.Stop();
        _settings.Update(Documents);
        foreach (FileType type in (FileType[])[FileType.Json, FileType.Yaml])
            _servers.GetValueOrDefault(type)?.NotifySettingsChanged();
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
