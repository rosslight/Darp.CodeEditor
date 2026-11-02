using System.Text.Json.Serialization;

namespace Darp.CodeEditor.CSharp.Hosting;

/// <summary>
/// Describes the environment C# scripts are compiled in: the globals type, references, imports and compiler options.
/// </summary>
/// <remarks>
/// A host compiles its scripts from this description and sends the same description to the C# script language
/// server, so what the editor reports matches what the host compiles. It holds only paths and names: no runtime
/// <see cref="Type"/> crosses processes.
/// </remarks>
public sealed record CSharpScriptEnvironment
{
    /// <summary>The type whose public members scripts use without qualification, or null for none.</summary>
    public CSharpScriptGlobals? Globals { get; init; }

    /// <summary>
    /// Absolute paths of assemblies scripts can use. Their direct dependencies are referenced too, from the same
    /// directory or the .NET runtime.
    /// </summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>
    /// Names of .NET runtime assemblies scripts can use, such as "System.Linq". They are resolved in the runtime of
    /// the process that compiles, so the description works for hosts that ship no runtime assemblies, such as
    /// NativeAOT apps. System.Private.CoreLib and System.Runtime are always referenced.
    /// </summary>
    public IReadOnlyList<string> FrameworkReferences { get; init; } = [];

    /// <summary>Namespaces imported into every script, such as "System".</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>The C# language version: "latest", "preview", "default" or a version such as "13.0".</summary>
    public string LanguageVersion { get; init; } = "latest";

    /// <summary>
    /// The nullable context of scripts. <c>CSharpScript</c> has no public option for it, so hosts that compile with
    /// <c>CSharpScript</c> can only use <see cref="CSharpScriptNullable.Disable"/>; scripts opt in with
    /// <c>#nullable enable</c>.
    /// </summary>
    public CSharpScriptNullable Nullable { get; init; } = CSharpScriptNullable.Disable;

    public bool AllowUnsafe { get; init; }

    public bool CheckOverflow { get; init; }

    /// <summary>The compiler warning level. Scripts default to 4.</summary>
    public int WarningLevel { get; init; } = 4;
}

/// <summary>The globals type, loaded by path and name from a managed contract assembly.</summary>
/// <param name="AssemblyPath">
/// The absolute path of the managed assembly that defines the type. NativeAOT hosts ship it as a file. It may be the
/// implementation assembly or a reference assembly built from it; either way its identity (name, version, culture
/// and public key token) must be the identity of the assembly the host loads the type from, because the compiler
/// finds the globals type by that identity.
/// </param>
/// <param name="TypeName">The full name of the type, such as "MyApp.ScriptGlobals".</param>
public sealed record CSharpScriptGlobals(string AssemblyPath, string TypeName);

[JsonConverter(typeof(JsonStringEnumConverter<CSharpScriptNullable>))]
public enum CSharpScriptNullable
{
    [JsonStringEnumMemberName("disable")]
    Disable,

    [JsonStringEnumMemberName("enable")]
    Enable,

    [JsonStringEnumMemberName("warnings")]
    Warnings,

    [JsonStringEnumMemberName("annotations")]
    Annotations,
}
