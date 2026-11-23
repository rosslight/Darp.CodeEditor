using Darp.CodeEditor.CSharpScript.Runner;
using PolyType;
using StreamJsonRpc;
using Windows.Data.Json;

[assembly: ExportRpcContractProxies]

namespace ConsumerWindows;

/// <summary>The app's half of the bridge.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IWindowsApp
{
    /// <summary>Receives what a script read.</summary>
    Task ReportAsync(double value, CancellationToken cancellationToken);
}

/// <summary>The session's half of the bridge, which this script API doesn't use.</summary>
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IWindowsSession;

/// <summary>What scripts see.</summary>
public sealed class WindowsGlobals
{
    private readonly IWindowsApp _app;
    private readonly CancellationToken _stop;

    internal WindowsGlobals(IWindowsApp app, CancellationToken stop)
    {
        _app = app;
        _stop = stop;
    }

    /// <summary>Reads a number with the Windows Runtime's JSON parser, which needs no device.</summary>
    public static double ReadNumber(string json, string name) => JsonObject.Parse(json).GetNamedNumber(name);

    /// <summary>Tells the app the number.</summary>
    public Task ReportAsync(double value) => _app.ReportAsync(value, _stop);
}

/// <summary>The session object.</summary>
public sealed class WindowsSession
    : IWindowsSession,
        IScriptSessionObject<WindowsSession, IWindowsApp>,
        IScriptGlobalsFactory<WindowsGlobals>
{
    private readonly IWindowsApp _app;

    private WindowsSession(IWindowsApp app) => _app = app;

    static WindowsSession IScriptSessionObject<WindowsSession, IWindowsApp>.Create(IWindowsApp app) => new(app);

    WindowsGlobals IScriptGlobalsFactory<WindowsGlobals>.CreateGlobals(CancellationToken stop) => new(_app, stop);
}
