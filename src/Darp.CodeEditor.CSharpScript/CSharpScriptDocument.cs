namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// A C# script document of one kind of script. Editors show it like any <see cref="CodeDocument"/>; compiling it
/// gives a script typed for its kind's session and input.
/// </summary>
/// <typeparam name="TSession">The session object's type that the script runs with.</typeparam>
/// <typeparam name="TInput">The input each run of the script gets.</typeparam>
public sealed class CSharpScriptDocument<TSession, TInput> : CodeDocument
{
    private CSharpScriptDocument() { }

    /// <summary>The environment the document was created in.</summary>
    public CSharpScriptEnvironment<TSession, TInput> Environment => throw new NotImplementedException();

    /// <summary>
    /// Compiles the document's <see cref="CodeDocument.CurrentSnapshot"/> at the time of the call, in its environment,
    /// from the same Roslyn snapshot that the editor's diagnostics come from. Later edits do not cancel the
    /// compilation, and disposing the document does not affect it. Safe to call from any thread.
    /// </summary>
    public Task<CSharpScriptCompilation<TSession, TInput>> CompileAsync(
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();
}
