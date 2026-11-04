namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// What a kind of script sees: its globals, the assemblies it may use and the namespaces it gets without
/// <c>using</c>. The editor checks scripts against it, and the compiler and the runner use the same one.
/// </summary>
/// <remarks>
/// Scripts can use the assemblies of references that the app's project marks with <c>CSharpScriptReference="true"</c>,
/// plus .NET's own. Publishing copies the marked references' DLLs next to the app, NativeAOT apps included, because
/// the language server reads and the runner loads those files. A globals type must come from such an assembly.
/// </remarks>
public sealed record CSharpScriptEnvironment
{
    /// <summary>
    /// The type whose public members scripts use without qualification, as if the script were its method. The runner
    /// creates an instance for every run, passing the app's services to its constructor; see the runner package.
    /// Null: scripts have no globals.
    /// </summary>
    public Type? GlobalsType { get; init; }

    /// <summary>
    /// The names of the assemblies scripts may use, such as <c>System.Text.Json</c> or <c>MyApp.ScriptApi</c>. Null:
    /// every assembly marked with <c>CSharpScriptReference</c>, and all of .NET's.
    /// </summary>
    public IReadOnlyList<string>? References { get; init; }

    /// <summary>The namespaces every script uses without a <c>using</c> directive, such as <c>System.Linq</c>.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>The C# language version, such as <c>13</c> or <c>latest</c>. Default: <c>latest</c>.</summary>
    public string LanguageVersion { get; init; } = "latest";

    /// <summary>Whether nullable reference types are on, with their warnings. Default: off.</summary>
    public bool Nullable { get; init; }

    /// <summary>Whether scripts may use <c>unsafe</c> code. Default: no.</summary>
    public bool AllowUnsafe { get; init; }

    /// <summary>Whether integer arithmetic checks for overflow, as in a <c>checked</c> block. Default: no.</summary>
    public bool CheckOverflow { get; init; }
}
