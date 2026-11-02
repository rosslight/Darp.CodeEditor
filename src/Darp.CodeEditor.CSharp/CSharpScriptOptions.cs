using System.Reflection;

namespace Darp.CodeEditor.CSharp;

/// <summary>Describes the environment C# scripts run in.</summary>
/// <param name="GlobalsType">
/// The type whose public members scripts can use without qualification, or null for none.
/// </param>
/// <param name="References">
/// Assemblies scripts can use. Their direct dependencies are referenced too. The assembly of
/// <paramref name="GlobalsType"/> is always included.
/// </param>
/// <param name="Imports">Namespaces imported into every script, such as "System".</param>
public sealed record CSharpScriptOptions(
    Type? GlobalsType,
    IReadOnlyList<Assembly> References,
    IReadOnlyList<string> Imports
);
