using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// The C# script language server: one Roslyn process for all script environments of the app. It provides the
/// editor's C# features and compiles scripts for the runner.
/// </summary>
/// <remarks>
/// <para>
/// The process runs on the .NET runtime that ships with the app, so user machines need no SDK and no installed .NET.
/// It starts with the first document or compilation, or with <see cref="LanguageServer"/>'s
/// <see cref="Darp.CodeEditor.LanguageServer.StartAsync"/>, and stops on <see cref="DisposeAsync"/>.
/// </para>
/// <para>
/// Compiling runs beside the editor's requests: completion keeps answering while a script compiles. A compilation
/// waits while the server starts or restarts, like completion does.
/// </para>
/// <para>
/// Safe to use from any thread, except <see cref="CreateDocument"/> and
/// <see cref="CompileAsync(CodeDocument, CancellationToken)"/>, which work on documents.
/// </para>
/// <para>
/// The server checks and loads an environment's references once, and reuses them for every document and compilation
/// in an environment with the same settings.
/// </para>
/// </remarks>
public sealed class CSharpScriptServer : IAsyncDisposable
{
    /// <summary>Describes the server. The process does not start yet.</summary>
    public CSharpScriptServer(CSharpScriptServerOptions? options = null) => throw new NotImplementedException();

    /// <summary>The server process: its state, and starting it early.</summary>
    /// <remarks>
    /// Its <see cref="Darp.CodeEditor.LanguageServer.CreateDocument"/> takes only <c>CodeLanguage.CSharp</c> and
    /// throws <see cref="ArgumentException"/> for other languages. Its documents are in the default environment, as if
    /// created with <c>CreateDocument(new CSharpScriptEnvironment(), text)</c>.
    /// </remarks>
    public LanguageServer LanguageServer => throw new NotImplementedException();

    /// <summary>
    /// Creates a C# script document in <paramref name="environment"/>, open in this server until the document's
    /// <see cref="CodeDocument.Dispose"/>. Its language is <c>CodeLanguage.CSharp</c>. Call it on the UI thread.
    /// </summary>
    /// <remarks>
    /// The document keeps a copy of the environment: later changes to its lists have no effect.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A reference or the globals type's assembly is not next to the app, because its project reference is not marked
    /// with <c>CSharpScriptReference</c>; or the globals type has no public constructor.
    /// </exception>
    public CodeDocument CreateDocument(CSharpScriptEnvironment environment, string text = "") =>
        throw new NotImplementedException();

    /// <summary>
    /// Compiles <paramref name="document"/>'s current version, in the environment it was created in, from the same
    /// snapshot of that version that the editor's diagnostics come from.
    /// </summary>
    /// <remarks>
    /// Call it on the UI thread: it takes the document's version before it returns. Later edits do not cancel the
    /// compilation, and disposing the document does not affect it.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// This server did not create the document, through <see cref="CreateDocument"/> or its
    /// <see cref="LanguageServer"/>.
    /// </exception>
    public Task<CSharpScriptCompilation> CompileAsync(
        CodeDocument document,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    /// <summary>
    /// Compiles <paramref name="code"/> in <paramref name="environment"/>, with no document or editor. Safe to call
    /// from any thread.
    /// </summary>
    /// <exception cref="ArgumentException">The environment is invalid, as for <see cref="CreateDocument"/>.</exception>
    public Task<CSharpScriptCompilation> CompileAsync(
        string code,
        CSharpScriptEnvironment environment,
        CancellationToken cancellationToken = default
    ) => throw new NotImplementedException();

    /// <summary>Stops the process. Its documents keep their text and lose their language features.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptServer"/>.</summary>
public sealed record CSharpScriptServerOptions
{
    /// <summary>Receives the server's messages: starts, restarts, failures and its standard error.</summary>
    public ILogger? Logger { get; init; }
}

/// <summary>The result of compiling a script: its diagnostics and, without errors, the compiled script.</summary>
public sealed class CSharpScriptCompilation
{
    private CSharpScriptCompilation() { }

    /// <summary>Errors and warnings, with ranges in the compiled text.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => throw new NotImplementedException();

    /// <summary>
    /// The <see cref="CodeDocument.Version"/> that was compiled; null for compiled strings. Compare it with the
    /// document's current version to tell whether the user changed the script since.
    /// </summary>
    public int? DocumentVersion => throw new NotImplementedException();

    /// <summary>Whether the script compiled without errors; <see cref="Script"/> is set then.</summary>
    [MemberNotNullWhen(true, nameof(Script))]
    public bool Succeeded => throw new NotImplementedException();

    /// <summary>The compiled script, ready to run; null when the script has errors.</summary>
    public CompiledScript? Script => throw new NotImplementedException();
}

/// <summary>
/// A script compiled for its environment, ready to run in a runner as often as needed. It needs no server: it stays
/// runnable after the server restarts or is disposed.
/// </summary>
public sealed class CompiledScript
{
    private CompiledScript() { }

    /// <summary>The environment the script was compiled for.</summary>
    public CSharpScriptEnvironment Environment => throw new NotImplementedException();
}
