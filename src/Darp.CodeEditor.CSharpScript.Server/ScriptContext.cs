using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using Darp.CodeEditor.CSharpScript.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Document = Microsoft.CodeAnalysis.Document;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// What one script environment compiles against, loaded once and shared by the documents in that environment: .NET,
/// the globals, the assemblies scripts may name, and, under a private alias, what those depend on.
/// </summary>
internal sealed class ScriptContext
{
    /// <summary>
    /// The alias of assemblies that scripts may not name: without an <c>extern alias</c> their types are out of reach,
    /// and the compiler still reads them for the signatures and base types of the assemblies scripts do see.
    /// </summary>
    private const string DependencyAlias = "script_dependency";

    /// <summary>The .NET SDK's implicit usings, which scripts always get.</summary>
    private static readonly string[] s_implicitImports =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    /// <summary>.NET's assemblies, from the runtime that runs the server, which is the one that runs the script.</summary>
    private static readonly string[] s_frameworkPaths = (
        AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? ""
    )
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => Path.GetDirectoryName(path) == Path.GetDirectoryName(typeof(object).Assembly.Location))
        .ToArray();

    private static readonly ImmutableArray<MetadataReference> s_framework =
    [
        .. s_frameworkPaths.Select(path => MetadataReference.CreateFromFile(path)),
    ];

    private static readonly AdhocWorkspace s_workspace = new();

    private readonly CSharpParseOptions _parseOptions;
    private readonly CSharpCompilationOptions _compilationOptions;
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly Type? _globals;
    private readonly string? _error;

    /// <summary>
    /// Loads <paramref name="environment"/>. What can't be loaded, as a file that changed since the app checked it,
    /// becomes an error on each of its documents rather than the end of the server.
    /// </summary>
    public ScriptContext(ScriptEnvironmentData environment)
    {
        if (!LanguageVersionFacts.TryParse(environment.LanguageVersion, out LanguageVersion version))
            _error = $"'{environment.LanguageVersion}' is not a C# language version.";
        _parseOptions = new CSharpParseOptions(version, kind: SourceCodeKind.Script);
        _compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            usings: [.. s_implicitImports, .. environment.Imports],
            nullableContextOptions: environment.Nullable
                ? NullableContextOptions.Enable
                : NullableContextOptions.Disable
        );
        try
        {
            _references = [.. s_framework, .. LoadReferences(environment)];
            _globals = LoadGlobals(environment);
        }
        // Whatever is wrong with the app's files is the script's error to show.
        catch (Exception e)
        {
            _references = _references.IsDefault ? s_framework : _references;
            _error = $"The script environment could not be loaded: {e.Message}";
        }
    }

    /// <summary>A new script in this environment, in a solution of its own.</summary>
    public ScriptSnapshot Open(string text, int version)
    {
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        SourceText source = SourceText.From(text, Encoding.UTF8);
        Solution solution = s_workspace
            .CurrentSolution.AddProject(
                ProjectInfo.Create(
                    projectId,
                    VersionStamp.Default,
                    "Script",
                    "Script",
                    LanguageNames.CSharp,
                    parseOptions: _parseOptions,
                    compilationOptions: _compilationOptions,
                    metadataReferences: _references,
                    isSubmission: true,
                    hostObjectType: _globals
                )
            )
            .AddDocument(documentId, "Script.csx", source, filePath: "Script.csx")
            .WithDocumentSourceCodeKind(documentId, SourceCodeKind.Script);
        Document document =
            solution.GetDocument(documentId) ?? throw new InvalidOperationException("The document was just added.");
        return new ScriptSnapshot(document, source, LineMap.Of(source, text), version, _error);
    }

    /// <summary>
    /// The assemblies scripts may name, with their XML docs, then what they depend on, found next to them, under the
    /// private alias.
    /// </summary>
    private static IEnumerable<MetadataReference> LoadReferences(ScriptEnvironmentData environment)
    {
        var loaded = new HashSet<string>(s_frameworkPaths.Select(Path.GetFileNameWithoutExtension).OfType<string>());
        var pending = new Queue<(string Path, bool IsVisible)>();
        foreach (string path in environment.References.Prepend(environment.GlobalsAssembly))
        {
            if (loaded.Add(Path.GetFileNameWithoutExtension(path)))
                pending.Enqueue((path, true));
        }
        while (pending.TryDequeue(out (string Path, bool IsVisible) next))
        {
            AssemblyMetadata metadata = AssemblyMetadata.CreateFromFile(next.Path);
            string xml = Path.ChangeExtension(next.Path, ".xml");
            yield return metadata.GetReference(
                File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null,
                next.IsVisible ? default : [DependencyAlias],
                filePath: next.Path
            );
            MetadataReader reader = metadata.GetModules()[0].GetMetadataReader();
            string directory = Path.GetDirectoryName(next.Path) ?? "";
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                string name = reader.GetString(reader.GetAssemblyReference(handle).Name);
                string dependency = Path.Combine(directory, name + ".dll");
                if (File.Exists(dependency) && loaded.Add(name))
                    pending.Enqueue((dependency, false));
            }
        }
    }

    /// <summary>
    /// Reads the globals type, which Roslyn takes as a <see cref="Type"/>. It loads apart from the server's own
    /// assemblies, and none of its code runs.
    /// </summary>
    private static Type LoadGlobals(ScriptEnvironmentData environment)
    {
        var context = new AssemblyLoadContext(environment.GlobalsType);
        string directory = Path.GetDirectoryName(environment.GlobalsAssembly) ?? "";
        context.Resolving += (loader, name) =>
            Path.Combine(directory, name.Name + ".dll") is var path && File.Exists(path)
                ? loader.LoadFromAssemblyPath(path)
                : null;
        return context
                .LoadFromAssemblyPath(environment.GlobalsAssembly)
                .GetType(environment.GlobalsType, throwOnError: true)
            ?? throw new InvalidOperationException($"The globals type '{environment.GlobalsType}' was not found.");
    }
}
