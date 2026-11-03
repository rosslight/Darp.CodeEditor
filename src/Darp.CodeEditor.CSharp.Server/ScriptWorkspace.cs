using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Roslyn's workspace for one script environment. Every document is its own script submission.</summary>
internal sealed class ScriptWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly ImmutableArray<MetadataReference> _references;
    private readonly MetadataLoadContext? _globalsContext;
    private readonly Type? _globalsType;

    private ScriptWorkspace(ResolvedScriptEnvironment environment)
    {
        Environment = environment;
        _workspace = new AdhocWorkspace(MefHostServices.DefaultHost);
        _references = [.. environment.ReferencePaths.Select(CreateReference)];
        if (environment.Environment.Globals is { } globals)
            (_globalsContext, _globalsType) = LoadGlobalsType(environment, globals);
    }

    public ResolvedScriptEnvironment Environment { get; }

    /// <summary>Composes Roslyn's services, reads the references and loads the globals type.</summary>
    /// <exception cref="ArgumentException">The globals type cannot be loaded.</exception>
    public static ScriptWorkspace Load(ResolvedScriptEnvironment environment) => new(environment);

    /// <summary>Creates an empty script in its own project and solution; scripts never see each other.</summary>
    /// <param name="name">Names the project and document, such as the document's URI.</param>
    public Document CreateDocument(string name)
    {
        var projectId = ProjectId.CreateNewId(name);
        var documentId = DocumentId.CreateNewId(projectId, name);
        ProjectInfo project = ProjectInfo
            .Create(
                projectId,
                VersionStamp.Default,
                name,
                // The assembly name must be valid; a URI is not.
                assemblyName: "Script",
                LanguageNames.CSharp,
                compilationOptions: Environment.CompilationOptions,
                parseOptions: Environment.ParseOptions,
                metadataReferences: _references,
                isSubmission: true,
                hostObjectType: _globalsType
            )
            .WithDocuments([
                DocumentInfo.Create(
                    documentId,
                    name,
                    sourceCodeKind: SourceCodeKind.Script,
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(""), VersionStamp.Default))
                ),
            ]);
        return _workspace.CurrentSolution.AddProject(project).GetDocument(documentId)
            ?? throw new InvalidOperationException("The new project has no document.");
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _globalsContext?.Dispose();
    }

    /// <summary>
    /// Loads the globals type as metadata only. Roslyn needs a <see cref="Type"/> for the submission's host object;
    /// a <see cref="MetadataLoadContext"/> provides one without loading the host's code into this process or running
    /// any of it, and works for reference assemblies too.
    /// </summary>
    private static (MetadataLoadContext Context, Type Type) LoadGlobalsType(
        ResolvedScriptEnvironment environment,
        CSharpScriptGlobals globals
    )
    {
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var resolver = new PathAssemblyResolver(
            environment.ReferencePaths.Concat(Directory.EnumerateFiles(runtimeDirectory, "*.dll"))
        );
        var context = new MetadataLoadContext(resolver, "System.Private.CoreLib");
        try
        {
            Type type =
                context.LoadFromAssemblyPath(globals.AssemblyPath).GetType(globals.TypeName)
                ?? throw new ArgumentException($"{globals.AssemblyPath} has no type {globals.TypeName}.");
            if (!type.IsClass || type.ContainsGenericParameters)
                throw new ArgumentException($"The globals type {globals.TypeName} must be a non-generic class.");
            return (context, type);
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private static MetadataReference CreateReference(string path)
    {
        string documentation = Path.ChangeExtension(path, ".xml");
        return MetadataReference.CreateFromFile(
            path,
            documentation: File.Exists(documentation) ? XmlDocumentationProvider.CreateFromFile(documentation) : null
        );
    }
}
