using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharp.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Darp.CodeEditor.CSharp.Scripting;

/// <summary>
/// The compiler inputs of a <see cref="CSharpScriptEnvironment"/>: reference files, parse options and compilation
/// options. Hosts create their <see cref="Microsoft.CodeAnalysis.Scripting.ScriptOptions"/> from it and the language
/// server creates its submission projects from it, so both compile scripts the same way.
/// </summary>
public sealed class ResolvedScriptEnvironment
{
    /// <summary>The script class name <c>CSharpScript</c> gives the first submission. It shows in diagnostics.</summary>
    private const string SubmissionClassName = "Submission#0";

    private static readonly string[] s_alwaysReferenced = ["System.Private.CoreLib", "System.Runtime"];

    private ResolvedScriptEnvironment(
        CSharpScriptEnvironment environment,
        ImmutableArray<string> referencePaths,
        LanguageVersion languageVersion
    )
    {
        Environment = environment;
        ReferencePaths = referencePaths;
        LanguageVersion = languageVersion;
        ParseOptions = new CSharpParseOptions(languageVersion, DocumentationMode.Parse, SourceCodeKind.Script);
        CompilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            scriptClassName: SubmissionClassName,
            usings: environment.Imports,
            optimizationLevel: OptimizationLevel.Debug,
            checkOverflow: environment.CheckOverflow,
            allowUnsafe: environment.AllowUnsafe,
            platform: Platform.AnyCpu,
            warningLevel: environment.WarningLevel,
            xmlReferenceResolver: null,
            sourceReferenceResolver: null,
            metadataReferenceResolver: null,
            assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
            nullableContextOptions: environment.Nullable switch
            {
                CSharpScriptNullable.Disable => NullableContextOptions.Disable,
                CSharpScriptNullable.Enable => NullableContextOptions.Enable,
                CSharpScriptNullable.Warnings => NullableContextOptions.Warnings,
                CSharpScriptNullable.Annotations => NullableContextOptions.Annotations,
                _ => throw new ArgumentOutOfRangeException(nameof(environment), environment.Nullable, null),
            }
        );
    }

    public CSharpScriptEnvironment Environment { get; }

    /// <summary>
    /// Absolute paths of every referenced assembly: the runtime assemblies, the references, the globals assembly and
    /// their direct dependencies. Sorted, without duplicates.
    /// </summary>
    public ImmutableArray<string> ReferencePaths { get; }

    public LanguageVersion LanguageVersion { get; }

    /// <summary>The parse options of scripts. Equal to what <c>ScriptOptions.WithLanguageVersion</c> produces.</summary>
    public CSharpParseOptions ParseOptions { get; }

    /// <summary>The compilation options of script submissions, as <c>CSharpScript</c> sets them.</summary>
    /// <remarks>
    /// <c>CSharpScript</c> also tells the binder to ignore duplicates of core library types in references. That flag
    /// is internal to Roslyn, so references that duplicate core library types can bind differently.
    /// </remarks>
    public CSharpCompilationOptions CompilationOptions { get; }

    /// <summary>Resolves the reference files and options of <paramref name="environment"/> in this process's runtime.</summary>
    /// <exception cref="ArgumentException">The environment names a file that does not exist or an unknown option value.</exception>
    public static ResolvedScriptEnvironment Resolve(CSharpScriptEnvironment environment)
    {
        if (!LanguageVersionFacts.TryParse(environment.LanguageVersion, out LanguageVersion languageVersion))
        {
            throw new ArgumentException(
                $"Unknown C# language version \"{environment.LanguageVersion}\".",
                nameof(environment)
            );
        }
        return new ResolvedScriptEnvironment(environment, ResolveReferences(environment), languageVersion);
    }

    /// <summary>Creates options for <c>CSharpScript</c> with the references, imports and options of the environment.</summary>
    /// <exception cref="NotSupportedException">
    /// The environment enables a nullable context, which <c>CSharpScript</c> has no public option for.
    /// </exception>
    public ScriptOptions CreateScriptOptions()
    {
        if (Environment.Nullable != CSharpScriptNullable.Disable)
        {
            throw new NotSupportedException(
                "CSharpScript has no public option for the nullable context. Use CSharpScriptNullable.Disable and "
                    + "#nullable enable in scripts."
            );
        }
        return ScriptOptions
            .Default.WithReferences(ReferencePaths.Select(path => MetadataReference.CreateFromFile(path)))
            .WithImports(Environment.Imports)
            .WithLanguageVersion(LanguageVersion)
            .WithAllowUnsafe(Environment.AllowUnsafe)
            .WithCheckOverflow(Environment.CheckOverflow)
            .WithWarningLevel(Environment.WarningLevel)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            // v1 has no directives: without resolvers, #r and #load are errors, as they are in the editor. The
            // setters are annotated as non-null, but null is the "no resolver" state CSharpCompilationOptions has.
            .WithMetadataResolver(null!)
            .WithSourceResolver(null!);
    }

    /// <summary>Creates a script in this environment.</summary>
    /// <param name="globalsType">
    /// The runtime type of the globals, loaded in the host. It must be the type the environment names.
    /// </param>
    public Script<object> CreateScript(string code, Type? globalsType, ScriptOptions options)
    {
        CSharpScriptGlobals? globals = Environment.Globals;
        if (globals is null != globalsType is null)
        {
            throw new ArgumentException(
                globals is null
                    ? "The environment has no globals type, but a globals type was passed."
                    : $"The environment's globals type is {globals.TypeName}, but no globals type was passed.",
                nameof(globalsType)
            );
        }
        if (
            globals is not null
            && globalsType is not null
            && (
                globalsType.FullName != globals.TypeName
                || globalsType.Assembly.GetName().Name != AssemblyName.GetAssemblyName(globals.AssemblyPath).Name
            )
        )
        {
            throw new ArgumentException(
                $"The globals type {globalsType.AssemblyQualifiedName} is not {globals.TypeName} from {globals.AssemblyPath}.",
                nameof(globalsType)
            );
        }
        return CSharpScript.Create(code, options, globalsType);
    }

    private static ImmutableArray<string> ResolveReferences(CSharpScriptEnvironment environment)
    {
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var listed = new List<string>();
        foreach (string name in s_alwaysReferenced.Concat(environment.FrameworkReferences))
        {
            string path = Path.Combine(runtimeDirectory, name + ".dll");
            if (!File.Exists(path))
            {
                throw new ArgumentException(
                    $"The .NET runtime has no assembly \"{name}\" ({path}).",
                    nameof(environment)
                );
            }
            listed.Add(path);
        }
        IEnumerable<string> files = environment.References;
        if (environment.Globals is { } globals)
            files = files.Append(globals.AssemblyPath);
        foreach (string path in files)
        {
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new ArgumentException(
                    $"The reference \"{path}\" is not an absolute path to a file.",
                    nameof(environment)
                );
            listed.Add(Path.GetFullPath(path));
        }

        var paths = new SortedSet<string>(listed, StringComparer.Ordinal);
        foreach (string path in listed)
        {
            foreach (string dependency in GetReferencedAssemblyNames(path))
            {
                string local = Path.Combine(Path.GetDirectoryName(path) ?? "", dependency + ".dll");
                string runtime = Path.Combine(runtimeDirectory, dependency + ".dll");
                if (File.Exists(local))
                    paths.Add(local);
                else if (File.Exists(runtime))
                    paths.Add(runtime);
            }
        }
        return [.. paths];
    }

    /// <summary>Reads the names of the assemblies <paramref name="path"/> references, without loading it.</summary>
    private static IEnumerable<string> GetReferencedAssemblyNames(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        MetadataReader metadata = reader.GetMetadataReader();
        var names = new List<string>(metadata.AssemblyReferences.Count);
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
            names.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        return names;
    }
}
