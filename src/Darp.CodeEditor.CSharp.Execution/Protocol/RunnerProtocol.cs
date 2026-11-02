using PolyType;
using StreamJsonRpc;

namespace Darp.CodeEditor.CSharp.Execution.Protocol;

/// <summary>What the host calls on the runner.</summary>
/// <remarks>
/// Every request the host sends has the session's call deadline, which is meant for script code. Compiling and releasing
/// run none, so they are notifications: the runner reports a compile result with <see cref="IRunnerHost.CompiledAsync"/>,
/// and the host gives that the compile timeout.
/// </remarks>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IRunnerService
{
    /// <summary>Loads the environment and attaches the host services. Answers with the runner's process ID.</summary>
    Task<int> StartAsync(RunnerStart start, CancellationToken cancellationToken);

    /// <summary>Compiles <paramref name="code"/>; the result follows with <see cref="IRunnerHost.CompiledAsync"/>.</summary>
    /// <param name="id">
    /// Chosen by the host; identifies the script in <see cref="RunAsync"/> and <see cref="Release"/>.
    /// </param>
    /// <param name="handler">The type the script must return.</param>
    void Compile(long id, string code, RunnerTypeName handler);

    /// <summary>Runs a compiled script once and returns its result as a marshaled object of the handler type.</summary>
    /// <remarks>
    /// The handler type is only known at run time, so the runner sends the result with that type as its declared type
    /// and the host reads it with <see cref="JsonRpc.InvokeWithCancellationAsync{TResult}(string, IReadOnlyList{object?}?, CancellationToken)"/>.
    /// <see cref="RunnerValue"/> never goes over the wire.
    /// </remarks>
    Task<RunnerValue> RunAsync(long id, CancellationToken cancellationToken);

    /// <summary>Forgets a script that will not run, and stops compiling it.</summary>
    void Release(long id);
}

/// <summary>What the runner calls on the host, besides the host services.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IRunnerHost
{
    /// <summary>Reports the result of <see cref="IRunnerService.Compile"/>.</summary>
    Task CompiledAsync(long id, CompileResult result);
}

/// <param name="Environment">The environment as the <c>initializationOptions</c> JSON of the language server.</param>
/// <param name="Services">The host services, by the full names of their interfaces.</param>
[GenerateShape]
internal sealed partial record RunnerStart(string Environment, IReadOnlyList<string> Services);

/// <summary>A type by its full name and the simple name of its assembly.</summary>
[GenerateShape]
internal sealed partial record RunnerTypeName(string FullName, string Assembly)
{
    public static RunnerTypeName Of(Type type) =>
        new(
            type.FullName ?? throw new ArgumentException($"The type {type} has no full name.", nameof(type)),
            type.Assembly.GetName().Name
                ?? throw new ArgumentException($"The assembly of {type} has no name.", nameof(type))
        );
}

/// <param name="Diagnostics">The compiler diagnostics, as the editor shows them.</param>
/// <param name="Error">Why the script could not be prepared although it compiled, such as an unknown handler type.</param>
[GenerateShape]
internal sealed partial record CompileResult(IReadOnlyList<ScriptDiagnostic> Diagnostics, string? Error = null)
{
    public bool Succeeded => Error is null && !Diagnostics.Any(d => d.Severity == ScriptDiagnosticSeverity.Error);
}

/// <summary>A result whose declared type is chosen at run time. The runner replaces it before sending.</summary>
[GenerateShape]
internal sealed partial class RunnerValue
{
    internal RunnerValue(object value, Type declaredType)
    {
        Value = value;
        DeclaredType = declaredType;
    }

    internal object Value { get; }

    internal Type DeclaredType { get; }
}

/// <summary>Names shared by the host and the runner.</summary>
internal static class RunnerNames
{
    /// <summary>The prefix of the RPC methods of a host service, by the full name of its interface.</summary>
    public static string ServicePrefix(string serviceFullName) => serviceFullName + "/";

    /// <summary>The argument that passes the host's process ID to the runner.</summary>
    public const string ParentProcessIdArgument = "--parent-pid";
}

[GenerateShapeFor<RunnerValue>]
internal sealed partial class ProtocolShapes;
