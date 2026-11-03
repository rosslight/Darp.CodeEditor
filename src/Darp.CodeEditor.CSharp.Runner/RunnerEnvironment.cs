using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Darp.CodeEditor.CSharp.Execution;
using Darp.CodeEditor.CSharp.Execution.Protocol;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using PolyType;
using PolyType.Abstractions;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Runner;

/// <summary>A compiled script and the handler type it must return.</summary>
internal sealed record CompiledScript(Script<object> Script, Type HandlerType);

/// <summary>
/// The environment loaded into the runner: the compiler inputs, which <see cref="ResolvedScriptEnvironment"/> builds
/// exactly as for the editor, and the globals with the host services.
/// </summary>
internal sealed class RunnerEnvironment
{
    private static readonly MethodInfo s_resolveShape = typeof(TypeShapeResolver)
        .GetMethods()
        .Single(m => m.Name == nameof(TypeShapeResolver.ResolveDynamic) && m.GetGenericArguments().Length == 1);

    private readonly ResolvedScriptEnvironment _resolved;
    private readonly ScriptOptions _options;
    private readonly ShapeProviders _shapes;
    private readonly Type? _globalsType;
    private readonly ConstructorInfo? _globalsConstructor;
    private readonly object[] _services;

    private RunnerEnvironment(
        ResolvedScriptEnvironment resolved,
        ShapeProviders shapes,
        Type? globalsType,
        ConstructorInfo? globalsConstructor,
        object[] services
    )
    {
        _resolved = resolved;
        _options = resolved.CreateScriptOptions();
        _shapes = shapes;
        _globalsType = globalsType;
        _globalsConstructor = globalsConstructor;
        _services = services;
    }

    /// <param name="services">The full names of the service interfaces the host provides.</param>
    public static RunnerEnvironment Load(
        CSharpScriptEnvironment environment,
        IReadOnlyList<RunnerTypeName> services,
        JsonRpc rpc,
        ShapeProviders shapes
    )
    {
        ResolvedScriptEnvironment resolved = ResolvedScriptEnvironment.Resolve(environment);
        LoadReferencesOnDemand(resolved.ReferencePaths);
        if (environment.Globals is not { } globals)
            return new RunnerEnvironment(resolved, shapes, null, null, []);

        Type globalsType = Assembly
            .Load(AssemblyName.GetAssemblyName(globals.AssemblyPath))
            .GetType(globals.TypeName, throwOnError: true)!;
        ConstructorInfo[] constructors = globalsType.GetConstructors();
        if (constructors is not [ConstructorInfo constructor])
        {
            throw new ArgumentException(
                $"The globals type {globalsType} must have exactly one public constructor; it has {constructors.Length}."
            );
        }
        // Each constructor parameter is a host service; scripts call it through a proxy.
        var proxies = new List<object>();
        foreach (ParameterInfo parameter in constructor.GetParameters())
        {
            Type serviceType = parameter.ParameterType;
            RunnerTypeName? service = services.FirstOrDefault(s => s.FullName == serviceType.FullName);
            if (!serviceType.IsInterface || service is null)
            {
                throw new ArgumentException(
                    $"The globals constructor takes {serviceType} ({parameter.Name}), which the host does not provide "
                        + "as a service. Add it to CSharpScriptRunnerOptions.Services."
                );
            }
            AppTypes.Verify(serviceType, service);
            shapes.Add(GetShapes(serviceType));
            proxies.Add(
                rpc.Attach(
                    serviceType,
                    new JsonRpcProxyOptions
                    {
                        MethodNameTransform = CommonMethodNameTransforms.Prepend(
                            RunnerNames.ServicePrefix(serviceType.FullName ?? "")
                        ),
                    }
                )
            );
        }
        return new RunnerEnvironment(resolved, shapes, globalsType, constructor, [.. proxies]);
    }

    /// <summary>A new globals object for one script run, or null when the environment has no globals.</summary>
    /// <remarks>Exceptions of the constructor, such as a missing dependency, surface as they are.</remarks>
    public object? CreateGlobals() =>
        _globalsConstructor?.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, _services, culture: null);

    /// <summary>
    /// Compiles a script and reports the diagnostics the editor shows for it: every compiler diagnostic that is not
    /// hidden, positioned by LSP's line rules.
    /// </summary>
    public (CompileResult Result, CompiledScript? Script) Compile(
        string code,
        RunnerTypeName handler,
        CancellationToken cancellationToken
    )
    {
        Type handlerType = AppTypes.Load(handler);
        _shapes.Add(GetShapes(handlerType));

        Script<object> script = _resolved.CreateScript(code, _globalsType, _options);
        ImmutableArray<Diagnostic> diagnostics =
        [
            .. script
                .GetCompilation()
                .GetDiagnostics(cancellationToken)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden),
        ];
        if (!diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            // Emitting can fail even when binding did not; report those errors too.
            diagnostics = diagnostics.AddRange(
                script.Compile(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error)
            );
        }
        var lines = new LineStarts(code);
        var result = new CompileResult([.. diagnostics.Select(d => ToScriptDiagnostic(d, lines))]);
        return (result, result.Succeeded ? new CompiledScript(script, handlerType) : null);
    }

    /// <summary>Compiles and emits a small script, so the compiler's code is ready for the first real one.</summary>
    public void WarmUp()
    {
        try
        {
            _resolved.CreateScript("int warmUp = 1;", _globalsType, _options).Compile();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Warming up the compiler failed: {exception.Message}");
        }
    }

    /// <summary>The type shapes generated for the API assembly of <paramref name="type"/>.</summary>
    private static ITypeShapeProvider GetShapes(Type type)
    {
        var shape = (ITypeShape?)s_resolveShape.MakeGenericMethod(type).Invoke(null, null);
        return shape?.Provider
            ?? throw new ArgumentException($"{type} has no generated type shape. Annotate it with [GenerateShape].");
    }

    /// <summary>
    /// Lets scripts load the files they were compiled against, and what those use at run time from the same
    /// directories: the app ships its script API with its dependencies. Assemblies the runner ships itself, such as
    /// StreamJsonRpc, load from the runner.
    /// </summary>
    private static void LoadReferencesOnDemand(ImmutableArray<string> referencePaths)
    {
        string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        foreach (string path in referencePaths)
        {
            if (path.StartsWith(runtimeDirectory, StringComparison.Ordinal))
                continue;
            paths.TryAdd(Path.GetFileNameWithoutExtension(path), path);
            if (Path.GetDirectoryName(path) is { } directory && !directories.Contains(directory))
                directories.Add(directory);
        }
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is not { } simpleName)
                return null;
            if (paths.TryGetValue(simpleName, out string? path))
                return context.LoadFromAssemblyPath(path);
            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, simpleName + ".dll");
                if (File.Exists(candidate))
                    return context.LoadFromAssemblyPath(candidate);
            }
            return null;
        };
    }

    private static ScriptDiagnostic ToScriptDiagnostic(Diagnostic diagnostic, LineStarts lines)
    {
        // Diagnostics without a place in the script, such as a broken reference, go to its start, as in the editor.
        (int startLine, int startCharacter, int endLine, int endCharacter) = diagnostic.Location.IsInSource
            ? (
                lines.LineOf(diagnostic.Location.SourceSpan.Start),
                lines.CharacterOf(diagnostic.Location.SourceSpan.Start),
                lines.LineOf(diagnostic.Location.SourceSpan.End),
                lines.CharacterOf(diagnostic.Location.SourceSpan.End)
            )
            : (0, 0, 0, 0);
        return new ScriptDiagnostic(
            diagnostic.Id,
            diagnostic.Severity switch
            {
                DiagnosticSeverity.Error => ScriptDiagnosticSeverity.Error,
                DiagnosticSeverity.Warning => ScriptDiagnosticSeverity.Warning,
                _ => ScriptDiagnosticSeverity.Info,
            },
            diagnostic.GetMessage(),
            startLine,
            startCharacter,
            endLine,
            endCharacter
        );
    }

    /// <summary>Line starts by LSP's rules: lines end at \n, \r\n or \r, unlike Roslyn's, which also end at U+2028.</summary>
    private sealed class LineStarts
    {
        private readonly int[] _starts;

        public LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
                    starts.Add(i + 1);
            }
            _starts = [.. starts];
        }

        public int LineOf(int offset)
        {
            int index = Array.BinarySearch(_starts, offset);
            return index >= 0 ? index : ~index - 1;
        }

        public int CharacterOf(int offset) => offset - _starts[LineOf(offset)];
    }
}
