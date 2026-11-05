namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// The globals of a kind of script: what its scripts use without qualification, as if the script were their method.
/// The session creates one instance per run, in its process, through <see cref="Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// The type parameters let the compiler check what the globals need: a session object of type
/// <typeparamref name="TSession"/>, and a run input of type <typeparamref name="TInput"/>. An environment made for
/// these globals only compiles scripts that run in such a session, with such an input.
/// </para>
/// <para>
/// Globals last as long as their run. When the script's code has finished, the session cancels the stop token and
/// disposes the globals if they are <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>, while the bridge is
/// still up: that is where they end what the run registered and send what it left queued. What must outlive a run
/// belongs to the session object.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The globals type itself.</typeparam>
/// <typeparam name="TSession">The session object's type, which the globals use.</typeparam>
/// <typeparam name="TInput">The run's input, which the app passes when it starts a run.</typeparam>
public interface IScriptGlobals<TSelf, TSession, TInput>
    where TSelf : IScriptGlobals<TSelf, TSession, TInput>
{
    /// <summary>Creates the globals of one run.</summary>
    /// <param name="session">The session object, shared by the session's runs.</param>
    /// <param name="input">The run's input.</param>
    /// <param name="stop">The run's stop token, cancelled when the app stops the run or when the run ends.</param>
    static abstract TSelf Create(TSession session, TInput input, CancellationToken stop);
}

/// <summary>The globals of a kind of script whose runs take no input.</summary>
/// <inheritdoc cref="IScriptGlobals{TSelf, TSession, TInput}"/>
public interface IScriptGlobals<TSelf, TSession> : IScriptGlobals<TSelf, TSession, ValueTuple>
    where TSelf : IScriptGlobals<TSelf, TSession>
{
    /// <summary>Creates the globals of one run.</summary>
    /// <param name="session">The session object, shared by the session's runs.</param>
    /// <param name="stop">The run's stop token, cancelled when the app stops the run or when the run ends.</param>
    static abstract TSelf Create(TSession session, CancellationToken stop);

    static TSelf IScriptGlobals<TSelf, TSession, ValueTuple>.Create(
        TSession session,
        ValueTuple input,
        CancellationToken stop
    ) => TSelf.Create(session, stop);
}
