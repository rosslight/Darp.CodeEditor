namespace Darp.CodeEditor.CSharpScript.Runner;

/// <summary>
/// The session object of a script API: the session side of the bridge, created once per session in the runner, and
/// the memory that the session's scripts share. The session creates it through <see cref="Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// The session object owns what must outlive a run, and the session disposes it at its end if it is
/// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>. Calls from the app and from overlapping scripts reach
/// it on any thread, so it must be safe for that.
/// </para>
/// <para>
/// It decides which kinds of scripts run in its session: it implements
/// <see cref="IScriptGlobalsFactory{TGlobals, TInput}"/> once per globals type, and creates the globals of each run.
/// </para>
/// <para>
/// Implement <see cref="Create"/> explicitly, with a private constructor, so that the app cannot create one in its own
/// process by mistake, and the factories and the disposal too: only the session calls them.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The session object's type itself.</typeparam>
/// <typeparam name="TAppSide">The app's half of the bridge, which the session object may call.</typeparam>
public interface IScriptSessionObject<TSelf, TAppSide>
    where TSelf : IScriptSessionObject<TSelf, TAppSide>
{
    /// <summary>Creates the session object, in the runner, when the session starts.</summary>
    /// <param name="app">A proxy of the app's half of the bridge.</param>
    static abstract TSelf Create(TAppSide app);
}
