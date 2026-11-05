namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// What a kind of script sees: its globals, the assemblies it may use and the namespaces it gets without
/// <c>using</c>. The editor checks scripts against it, and the compiler and the session use the same one.
/// </summary>
/// <remarks>
/// <para>
/// A kind of script is its globals type: scripts use the globals' public members without qualification, as if the
/// script were one of its methods. The globals are an ordinary class of the script API, and the editor and the
/// compiler need nothing else of them. The type parameter travels through documents and compilations to the session,
/// whose object creates the globals of each run, so the compiler rejects a script started in a session that can't
/// create them, or with the wrong input.
/// </para>
/// <para>
/// The library finds the globals type's DLL by name in the build's list and reads the type from the file, so the app
/// never reflects over the type's members; naming it compiles none of its code into a NativeAOT app.
/// </para>
/// <para>
/// Scripts can use the assemblies of references that the app's project marks with <c>CSharpScriptReference="true"</c>,
/// plus .NET's own. The build writes a list of the marked assemblies, and publishing copies their DLLs next to the
/// app, NativeAOT apps included: the language server reads those files, and the runner loads them.
/// </para>
/// <para>
/// Documents and compilations keep a copy of the environment, so changing its lists afterwards has no effect. Two
/// environments of one globals type are equal when all their settings are, lists compared by content; the server
/// prepares an environment's references once and reuses them for equal ones.
/// </para>
/// </remarks>
/// <typeparam name="TGlobals">The globals type, from an assembly marked <c>CSharpScriptReference</c>.</typeparam>
public sealed record CSharpScriptEnvironment<TGlobals>
    where TGlobals : class
{
    /// <summary>
    /// Creates the environment of scripts with <typeparamref name="TGlobals"/>. Set references, imports and compiler
    /// options in the initializer.
    /// </summary>
    public CSharpScriptEnvironment() { }

    /// <summary>
    /// The names of the assemblies scripts may use, such as <c>System.Text.Json</c> or <c>MyApp.ScriptApi</c>, besides
    /// .NET's and the globals type's, which they can always use. Null: every assembly marked with
    /// <c>CSharpScriptReference</c>. What these assemblies depend on loads at run time without becoming visible to
    /// scripts.
    /// </summary>
    public IReadOnlyList<string>? References { get; init; }

    /// <summary>
    /// Whether scripts get the .NET SDK's implicit usings: <c>System</c>, <c>System.Collections.Generic</c>,
    /// <c>System.IO</c>, <c>System.Linq</c>, <c>System.Net.Http</c>, <c>System.Threading</c> and
    /// <c>System.Threading.Tasks</c>. Default: yes.
    /// </summary>
    public bool ImplicitImports { get; init; } = true;

    /// <summary>
    /// Further namespaces every script uses without a <c>using</c> directive, such as the script API's, besides the
    /// implicit ones.
    /// </summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>The C# language version, such as <c>13</c> or <c>latest</c>. Default: <c>latest</c>.</summary>
    public string LanguageVersion { get; init; } = "latest";

    /// <summary>Whether nullable reference types are on, with their warnings. Default: on.</summary>
    public bool Nullable { get; init; } = true;

    /// <summary>Whether integer arithmetic checks for overflow, as in a <c>checked</c> block. Default: no.</summary>
    public bool CheckOverflow { get; init; }

    /// <summary>Compares all settings, lists by content.</summary>
    public bool Equals(CSharpScriptEnvironment<TGlobals>? other) => throw new NotImplementedException();

    /// <inheritdoc />
    public override int GetHashCode() => throw new NotImplementedException();
}
