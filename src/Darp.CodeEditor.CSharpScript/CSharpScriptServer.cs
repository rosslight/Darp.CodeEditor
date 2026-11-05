using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// The C# script language server: one Roslyn process for all script environments of the app. It provides the
/// editor's C# features and compiles scripts for the runner.
/// </summary>
/// <remarks>
/// <para>
/// The process runs on the .NET runtime that ships with the app, so user machines need no SDK and no installed .NET.
/// It starts with the first document or compilation, or with <see cref="StartAsync"/>, is restarted after a crash or
/// when it stops answering, and stops on <see cref="DisposeAsync"/>, or at once on <see cref="Dispose"/>. It is a
/// child of the app: it ends when the app ends, even when the app is killed.
/// </para>
/// <para>
/// <see cref="PropertyChanged"/> reports <see cref="State"/> on the UI thread the server belongs to, for a status
/// indicator to bind: that of the thread that created it, if that thread has an Avalonia dispatcher, and otherwise the
/// app's main UI thread. Its documents get their diagnostics on their own UI thread. Before
/// Avalonia is set up, the server leaves Avalonia's dispatcher alone and raises it where the state changed, so
/// compiling also works before the app's UI runs. When the process fails, <see cref="State"/> shows the restart,
/// <see cref="StartAsync"/> throws why a process did not start, and the server's own messages arrive through
/// <see cref="CSharpScriptServerOptions.StandardError"/>.
/// </para>
/// <para>
/// Compiling runs beside the editor's requests: completion keeps answering while a script compiles. A compilation
/// waits while the server starts or restarts, like completion does.
/// </para>
/// <para>Safe to use from any thread.</para>
/// <para>
/// The server checks and loads an environment's references once, and reuses them for every document and compilation
/// in an environment with the same settings.
/// </para>
/// </remarks>
public sealed class CSharpScriptServer : INotifyPropertyChanged, IAsyncDisposable, IDisposable
{
    /// <summary>Describes the server. The process does not start yet.</summary>
    public CSharpScriptServer(CSharpScriptServerOptions? options = null) => throw new NotImplementedException();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the server process is in its life, for a status indicator.</summary>
    public LanguageServerState State => throw new NotImplementedException();

    /// <summary>
    /// Starts the process now rather than with the first document, so that C# features are ready sooner. Completes
    /// when the server is ready. After <see cref="LanguageServerState.Failed"/>, it starts a new process.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The process did not start or did not answer in time. The message says why, with the end of its standard error.
    /// </exception>
    public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>
    /// Creates a C# script document in <paramref name="environment"/>, open in this server until the document's
    /// <see cref="CodeDocument.Dispose"/>. Its language is <c>CodeLanguage.CSharp</c>.
    /// </summary>
    /// <remarks>
    /// The document keeps a copy of the environment: later changes to its lists have no effect.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// A reference or the globals type's assembly is not next to the app, because its project reference is not marked
    /// with <c>CSharpScriptReference</c>.
    /// </exception>
    public CSharpScriptDocument<TGlobals> CreateDocument<TGlobals>(
        CSharpScriptEnvironment<TGlobals> environment,
        string text = ""
    )
        where TGlobals : class => throw new NotImplementedException();

    /// <summary>
    /// Compiles <paramref name="code"/> in <paramref name="environment"/>, with no document or editor. Safe to call
    /// from any thread. Documents compile themselves, with their <c>CompileAsync</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The environment is invalid, as for <see cref="CreateDocument"/>.</exception>
    public Task<CSharpScriptCompilation<TGlobals>> CompileAsync<TGlobals>(
        CSharpScriptEnvironment<TGlobals> environment,
        string code,
        CancellationToken cancellationToken = default
    )
        where TGlobals : class => throw new NotImplementedException();

    /// <summary>Stops the process. Its documents keep their text and lose their language features.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();

    /// <summary>
    /// Kills the process at once, for app exits where only synchronous disposal runs. Its documents keep their text
    /// and lose their language features.
    /// </summary>
    public void Dispose() => throw new NotImplementedException();
}

/// <summary>Settings of a <see cref="CSharpScriptServer"/>.</summary>
public sealed record CSharpScriptServerOptions
{
    /// <summary>
    /// Receives each line that the server process writes to standard error, on any thread, for the app's log: Roslyn's
    /// failures end up there. Nothing else arrives here; <see cref="CSharpScriptServer.State"/> and
    /// <see cref="CSharpScriptServer.StartAsync"/> report what the server does.
    /// </summary>
    public Action<string>? StandardError { get; init; }

    /// <summary>
    /// The clock for the delay before a restart and for how long the server may take to answer before it counts as
    /// hung. Default: <see cref="TimeProvider.System"/>; tests pass their own.
    /// </summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>The result of compiling a script: its diagnostics and, without errors, the compiled script.</summary>
/// <typeparam name="TGlobals">The globals type of the script's kind.</typeparam>
public sealed class CSharpScriptCompilation<TGlobals>
    where TGlobals : class
{
    private CSharpScriptCompilation() { }

    /// <summary>Errors and warnings, each with <see cref="Snapshot"/>, the compiled text.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => throw new NotImplementedException();

    /// <summary>
    /// The compiled text: the document's snapshot, or one of the compiled string. Compare a document's with its
    /// <see cref="CodeDocument.CurrentSnapshot"/> to tell whether the user changed the script since.
    /// </summary>
    public TextSnapshot Snapshot => throw new NotImplementedException();

    /// <summary>Whether the script compiled without errors; <see cref="Script"/> is set then.</summary>
    [MemberNotNullWhen(true, nameof(Script))]
    public bool Succeeded => throw new NotImplementedException();

    /// <summary>The compiled script, ready to run; null when the script has errors.</summary>
    public CSharpScriptAssembly<TGlobals>? Script => throw new NotImplementedException();
}

/// <summary>
/// A compiled script: the assembly the server emitted for it, with its debug symbols, its identity and the compiled
/// text, and its environment, ready to run as often as needed in sessions whose object creates
/// <typeparamref name="TGlobals"/>. It needs no server: it stays runnable, and its failures still point into its text,
/// after the server restarts or is disposed.
/// </summary>
/// <remarks>
/// A script's return value is ignored: runs report only how they ended, and results go through the bridge.
/// </remarks>
/// <typeparam name="TGlobals">The globals type of the script's kind.</typeparam>
public sealed class CSharpScriptAssembly<TGlobals>
    where TGlobals : class
{
    private CSharpScriptAssembly() { }

    /// <summary>The environment the script was compiled for.</summary>
    public CSharpScriptEnvironment<TGlobals> Environment => throw new NotImplementedException();
}
