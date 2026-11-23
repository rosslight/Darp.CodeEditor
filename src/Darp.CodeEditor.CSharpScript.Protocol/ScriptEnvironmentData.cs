using System.Text.Json.Serialization;

namespace Darp.CodeEditor.CSharpScript.Protocol;

/// <summary>
/// A script environment as the server receives it with each document, in the protocol's open-document data, so that a
/// restarted server opens the document in the same environment.
/// </summary>
/// <param name="GlobalsType">The globals type's full name.</param>
/// <param name="GlobalsAssembly">The path of the globals type's assembly.</param>
/// <param name="References">The paths of the other assemblies that scripts may name.</param>
/// <param name="Imports">The namespaces scripts use without a <c>using</c> directive, besides the implicit ones.</param>
/// <param name="LanguageVersion">The C# language version, as the compiler's option spells it.</param>
/// <param name="Nullable">Whether nullable reference types are on.</param>
internal sealed record ScriptEnvironmentData(
    string GlobalsType,
    string GlobalsAssembly,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Imports,
    string LanguageVersion,
    bool Nullable
);

/// <summary>The source-generated serialization of the script protocol, for NativeAOT apps.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(ScriptEnvironmentData))]
[JsonSerializable(typeof(ScriptCompileParams))]
[JsonSerializable(typeof(ScriptCompileResult))]
internal sealed partial class ScriptProtocolJson : JsonSerializerContext;
