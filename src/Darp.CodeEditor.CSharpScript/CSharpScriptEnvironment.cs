namespace Darp.CodeEditor.CSharpScript;

/// <summary>Creates script environments, one per kind of script, from the kind's globals type.</summary>
public static class CSharpScriptEnvironment
{
    /// <summary>
    /// The environment of scripts with <typeparamref name="TGlobals"/>: they run in sessions whose object is a
    /// <typeparamref name="TSession"/>, with a <typeparamref name="TInput"/> per run. Set references, imports and
    /// compiler options with <c>with</c>.
    /// </summary>
    /// <typeparam name="TGlobals">The globals type, from an assembly marked <c>CSharpScriptReference</c>.</typeparam>
    /// <typeparam name="TSession">The session object's type that the globals take.</typeparam>
    /// <typeparam name="TInput">The run input that the globals take.</typeparam>
    public static CSharpScriptEnvironment<TSession, TInput> For<TGlobals, TSession, TInput>()
        where TGlobals : IScriptGlobals<TGlobals, TSession, TInput> => throw new NotImplementedException();

    /// <summary>
    /// The environment of scripts with <typeparamref name="TGlobals"/>, whose runs take no input: they run in sessions
    /// whose object is a <typeparamref name="TSession"/>.
    /// </summary>
    /// <typeparam name="TGlobals">The globals type, from an assembly marked <c>CSharpScriptReference</c>.</typeparam>
    /// <typeparam name="TSession">The session object's type that the globals take.</typeparam>
    public static CSharpScriptEnvironment<TSession, ValueTuple> For<TGlobals, TSession>()
        where TGlobals : IScriptGlobals<TGlobals, TSession> => throw new NotImplementedException();
}

/// <summary>
/// What a kind of script sees: its globals, the assemblies it may use and the namespaces it gets without
/// <c>using</c>. The editor checks scripts against it, and the compiler and the session use the same one.
/// </summary>
/// <remarks>
/// <para>
/// The type parameters carry what the globals need through documents and compilations to the session, so the compiler
/// rejects a script started in the wrong kind of session or with the wrong input. Create one with
/// <see cref="CSharpScriptEnvironment.For{TGlobals, TSession, TInput}"/>.
/// </para>
/// <para>
/// Scripts can use the assemblies of references that the app's project marks with <c>CSharpScriptReference="true"</c>,
/// plus .NET's own. The build writes a list of the marked assemblies, and publishing copies their DLLs next to the
/// app, NativeAOT apps included: the language server reads those files, and the runner loads them.
/// </para>
/// <para>
/// Documents and compilations keep a copy of the environment, so changing its lists afterwards has no effect.
/// </para>
/// </remarks>
/// <typeparam name="TSession">The session object's type that scripts of this kind run with.</typeparam>
/// <typeparam name="TInput">The input each run of this kind gets; <see cref="ValueTuple"/> for none.</typeparam>
public sealed record CSharpScriptEnvironment<TSession, TInput>
{
    internal CSharpScriptEnvironment() { }

    /// <summary>
    /// The globals type, which implements <see cref="IScriptGlobals{TSelf, TSession, TInput}"/>. The library finds its
    /// assembly's DLL by name in the build's list and reads the type from the file, so the app never reflects over
    /// the type's members; naming it compiles none of its code into a NativeAOT app.
    /// </summary>
    public Type GlobalsType => throw new NotImplementedException();

    /// <summary>
    /// The names of the assemblies scripts may use, such as <c>System.Text.Json</c> or <c>MyApp.ScriptApi</c>, besides
    /// .NET's and the globals type's, which they can always use. Null: every assembly marked with
    /// <c>CSharpScriptReference</c>. What these assemblies depend on loads at run time without becoming visible to
    /// scripts.
    /// </summary>
    public IReadOnlyList<string>? References { get; init; }

    /// <summary>The namespaces every script uses without a <c>using</c> directive, such as <c>System.Linq</c>.</summary>
    public IReadOnlyList<string> Imports { get; init; } = [];

    /// <summary>The C# language version, such as <c>13</c> or <c>latest</c>. Default: <c>latest</c>.</summary>
    public string LanguageVersion { get; init; } = "latest";

    /// <summary>Whether nullable reference types are on, with their warnings. Default: on.</summary>
    public bool Nullable { get; init; } = true;

    /// <summary>Whether scripts may use <c>unsafe</c> code. Default: no.</summary>
    public bool AllowUnsafe { get; init; }

    /// <summary>Whether integer arithmetic checks for overflow, as in a <c>checked</c> block. Default: no.</summary>
    public bool CheckOverflow { get; init; }
}
