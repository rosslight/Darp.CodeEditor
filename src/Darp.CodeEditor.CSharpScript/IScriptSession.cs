namespace Darp.CodeEditor.CSharpScript;

/// <summary>
/// The session object of a script API: the session side of the bridge, created once per session in the runner, and
/// the memory that the session's scripts share. The session creates it through <see cref="Create"/>.
/// </summary>
/// <remarks>
/// The session object owns what must outlive a run, and the session disposes it at its end if it is
/// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>. Calls from the app and from overlapping scripts reach
/// it on any thread, so it must be safe for that. A private constructor keeps the app from creating one in its own
/// process by mistake.
/// </remarks>
/// <typeparam name="TSelf">The session object's type itself.</typeparam>
/// <typeparam name="TAppSide">The app's half of the bridge, which the session object may call.</typeparam>
public interface IScriptSession<TSelf, TAppSide>
    where TSelf : IScriptSession<TSelf, TAppSide>
{
    /// <summary>Creates the session object, in the runner, when the session starts.</summary>
    /// <param name="app">A proxy of the app's half of the bridge.</param>
    static abstract TSelf Create(TAppSide app);
}
