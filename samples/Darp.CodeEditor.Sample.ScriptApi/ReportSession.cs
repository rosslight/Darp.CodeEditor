using Darp.CodeEditor.CSharpScript.Runner;
using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

namespace Sample.ScriptApi;

/// <summary>What report scripts may ask of the app: nothing yet, as they only print.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IReportApp;

/// <summary>What the app may ask of a report session: nothing yet, as it only runs scripts.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IReportSession;

/// <summary>The session object of report scripts: it creates each run's globals, in the runner.</summary>
public sealed class ReportSession
    : IReportSession,
        IScriptSessionObject<ReportSession, IReportApp>,
        IScriptGlobalsFactory<ReportGlobals>
{
    private ReportSession() { }

    /// <inheritdoc />
    static ReportSession IScriptSessionObject<ReportSession, IReportApp>.Create(IReportApp app) => new();

    /// <inheritdoc />
    ReportGlobals IScriptGlobalsFactory<ReportGlobals>.CreateGlobals(CancellationToken stop) => new(stop);
}
