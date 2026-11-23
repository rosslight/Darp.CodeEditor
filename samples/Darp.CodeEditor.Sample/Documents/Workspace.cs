using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Sample.Localization;
using Sample.ScriptApi;

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

    /// <summary>The server of the app's C# scripts, which starts with the first of them.</summary>
    private readonly CSharpScriptServer _scripts = new(
        new CSharpScriptServerOptions { StandardError = line => Trace.WriteLine(line, "C# scripts") }
    );

    /// <summary>What the app's scripts see: its script API, without a <c>using</c>.</summary>
    private readonly CSharpScriptEnvironment<ReportGlobals> _scriptEnvironment = new()
    {
        Imports = ["Sample.ScriptApi"],
    };

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

    /// <summary>
    /// Opens the YAML example's JSON schema from its file next to the app, right after the example, and keeps the
    /// shown document. The YAML server reads schemas from files, so edits reach it once they are saved.
    /// </summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    public async Task OpenSchemaAsync(IStorageProvider storage)
    {
        if (await storage.TryGetFileFromPathAsync(ServerSettings.PipelineSchema) is not { } file)
            return;
        string text = await ReadAsync(file);
        OpenDocument schema = Create(file.Name, FileType.Json, text, file, ServerSettings.PipelineSchema);
        Documents.Insert(Documents.ToList().FindIndex(document => document.Name == "Pipeline") + 1, schema);
    }

    /// <summary>Has the YAML server read its schema again, if <paramref name="document"/> is that schema.</summary>
    public void NotifySaved(OpenDocument document)
    {
        if (document.Document.FilePath is { } path && s_paths.Equals(path, ServerSettings.PipelineSchema))
            _servers.GetValueOrDefault(FileType.Yaml)?.NotifySettingsChanged();
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
        string text = await ReadAsync(file);
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
        _scripts.Dispose();
    }

    /// <summary>Adds a document and shows it.</summary>
    private void Add(string name, FileType type, string text, IStorageFile? file, string? filePath)
    {
        OpenDocument open = Create(name, type, text, file, filePath);
        Documents.Add(open);
        Current = open;
    }

    /// <summary>
    /// Creates a document. A file type whose server is installed gets its documents from that server, which serves
    /// them from then on; the others get documents without a server.
    /// </summary>
    private OpenDocument Create(string name, FileType type, string text, IStorageFile? file, string? filePath)
    {
        LanguageServer? server = _servers.GetValueOrDefault(type);
        CodeDocument document;
        if (type == FileType.CSharpScript)
        {
            document = _scripts.CreateDocument(_scriptEnvironment, text);
            document.FilePath = filePath;
            return new OpenDocument(name, type, document, server, file) { ScriptServer = _scripts };
        }
        if (server is not null)
            document = server.CreateDocument(type.Language, text, filePath);
        else
        {
            document = CodeDocument.Create(type.Language, text);
            document.FilePath = filePath;
        }
        return new OpenDocument(name, type, document, server, file);
    }

    /// <summary>Reads the text of <paramref name="file"/>, and disposes the file if that fails.</summary>
    private static async Task<string> ReadAsync(IStorageFile file)
    {
        try
        {
            await using Stream stream = await file.OpenReadAsync();
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            file.Dispose();
            throw;
        }
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
