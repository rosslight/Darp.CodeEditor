using PolyType;
using StreamJsonRpc;

// NativeAOT apps cannot generate proxies at run time, so StreamJsonRpc generates them at build time.
[assembly: ExportRpcContractProxies]

namespace UseCases.ScriptApi;

// The bridge of the use cases' script API: what crosses between the app and a session. Two interfaces, one per
// direction, and the data they exchange. They are coarse and async, and copy their data; fast data crosses as arrays.
// [JsonRpcContract] and [GenerateShape] let NativeAOT apps call them without reflection.
//
// This project holds only the bridge, so it can be analysed for NativeAOT like the app. The session object and the
// globals run only in the runner and live in Darp.CodeEditor.UseCases.ScriptApi.Runner.
//
// The methods take a token without a default, so that runner-side code cannot forget to pass the run's stop token.

/// <summary>The app's half of the bridge: the app implements it, scripts call it.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IAppSide
{
    /// <summary>
    /// Script to app, a call: asks the app and waits for its answer, as long as the app needs, for example while the
    /// user thinks.
    /// </summary>
    Task<string> AskAsync(string question, CancellationToken cancellationToken);

    /// <summary>Script to app, a notification from the run <paramref name="runId"/>.</summary>
    Task NotifyAsync(string runId, string message, CancellationToken cancellationToken);

    /// <summary>Script to app, at high rates: a batch of values from the run <paramref name="runId"/>, in order.</summary>
    Task ReceiveAsync(string runId, double[] values, CancellationToken cancellationToken);
}

/// <summary>
/// The session's half of the bridge: the session object implements it in the runner, the app calls it. Failures in
/// the runner reach the app as CSharpScriptException.
/// </summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ISessionSide
{
    /// <summary>Setup before scripts run, such as connecting a device. Safe to call again.</summary>
    Task InitializeAsync(string name, CancellationToken cancellationToken);

    /// <summary>App to script, a call: answered by the session object itself, also while no script runs.</summary>
    Task<string> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// App to script, a callback that the session owns: calls the calculation a script set. It stays set after that
    /// script's run has ended. Fails while no script has set one.
    /// </summary>
    Task<double> CalculateAsync(double input, CancellationToken cancellationToken);

    /// <summary>App to script, at high rates: a batch of inputs through the calculation, in order.</summary>
    Task<double[]> CalculateManyAsync(double[] inputs, CancellationToken cancellationToken);

    /// <summary>
    /// App to script, a callback that a run owns: calls the handler a running script registered for
    /// <paramref name="topic"/>. It is gone when that run ends. Fails while no run answers the topic.
    /// </summary>
    Task TriggerAsync(string topic, CancellationToken cancellationToken);
}

/// <summary>What one run gets from the app when it starts.</summary>
/// <param name="RunId">The run's identity, which the run's notifications and values carry.</param>
/// <param name="Device">The device the run works with.</param>
[GenerateShape]
public sealed partial record RunInput(string RunId, string Device);
