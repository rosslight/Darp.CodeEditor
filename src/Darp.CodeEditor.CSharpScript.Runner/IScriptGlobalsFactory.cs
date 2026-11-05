namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// Lets a session run scripts with <typeparamref name="TGlobals"/>: the session object implements it once per kind of
/// script it runs, and creates the globals of each run.
/// </summary>
/// <remarks>
/// <para>
/// The session calls <see cref="CreateGlobals"/> in the runner when a run starts, as part of the run: a factory that
/// hangs can be stopped like the script, and one that throws ends the run's <see cref="CSharpScriptRun.Completion"/>.
/// It passes the globals what they use, such as the session object itself or only a part of it. The globals are an
/// ordinary class of the script API; an internal constructor keeps the app and scripts from creating them.
/// </para>
/// <para>
/// Globals last as long as their run. When the script's code has finished, the session cancels the stop token and
/// disposes the globals if they are <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, while the bridge is
/// still up: that is where they end what the run registered and send what it left queued. What must outlive a run
/// belongs to the session object. A callback that the session object keeps after the run must not use the run's
/// globals. The public members of the globals are what scripts see, so implement the disposal explicitly.
/// </para>
/// <para>
/// <c>StartScriptAsync</c> requires this interface, so the compiler rejects a script started in a session that can't
/// create its globals, or with the wrong input. Implement it explicitly: only the session calls it.
/// </para>
/// </remarks>
/// <typeparam name="TGlobals">The globals type of the kind of script.</typeparam>
/// <typeparam name="TInput">
/// The run's input, which the app passes when it starts a run. It is copied into the runner, so it must be a
/// <c>[GenerateShape]</c> type; <c>StartScriptAsync</c> checks that.
/// </typeparam>
public interface IScriptGlobalsFactory<TGlobals, TInput>
    where TGlobals : class
{
    /// <summary>Creates the globals of one run.</summary>
    /// <param name="input">The run's input.</param>
    /// <param name="stop">The run's stop token, cancelled when the app stops the run or when the run ends.</param>
    TGlobals CreateGlobals(TInput input, CancellationToken stop);
}

/// <summary>
/// Lets a session run scripts with <typeparamref name="TGlobals"/> whose runs take no input: the session object
/// implements it once per such kind of script, and creates the globals of each run.
/// </summary>
/// <inheritdoc cref="IScriptGlobalsFactory{TGlobals, TInput}"/>
public interface IScriptGlobalsFactory<TGlobals> : IScriptGlobalsFactory<TGlobals, ValueTuple>
    where TGlobals : class
{
    /// <summary>Creates the globals of one run.</summary>
    /// <param name="stop">The run's stop token, cancelled when the app stops the run or when the run ends.</param>
    TGlobals CreateGlobals(CancellationToken stop);

    TGlobals IScriptGlobalsFactory<TGlobals, ValueTuple>.CreateGlobals(ValueTuple input, CancellationToken stop) =>
        CreateGlobals(stop);
}
