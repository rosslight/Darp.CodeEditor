namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// A C# script document of one kind of script. Editors show it like any <see cref="CodeDocument"/>; compiling it
/// gives a script typed by its globals.
/// </summary>
/// <typeparam name="TGlobals">The globals type of the document's kind of script.</typeparam>
public sealed class CSharpScriptDocument<TGlobals> : CodeDocument
    where TGlobals : class
{
    private CSharpScriptDocument() { }

    /// <summary>The environment the document was created in.</summary>
    public CSharpScriptEnvironment<TGlobals> Environment => throw new NotImplementedException();

    /// <summary>
    /// Compiles the document's <see cref="CodeDocument.CurrentSnapshot"/> at the time of the call, in its environment,
    /// from the same Roslyn snapshot that the editor's diagnostics come from. Later edits do not cancel the
    /// compilation, and disposing the document does not affect it. Safe to call from any thread.
    /// </summary>
    public Task<CSharpScriptCompilation<TGlobals>> CompileAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
