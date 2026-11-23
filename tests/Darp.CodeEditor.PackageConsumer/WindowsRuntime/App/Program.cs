// A script calls a Windows Runtime API through a marked reference for a versioned Windows target. Exits with 0 if it did.
using ConsumerWindows;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;

await using var server = new CSharpScriptServer();
CSharpScriptCompilation<WindowsGlobals> compiled = await server.CompileAsync(
    new CSharpScriptEnvironment<WindowsGlobals>(),
    """await ReportAsync(ReadNumber("{\"answer\":42}", "answer"));"""
);
if (compiled.Script is not { } script)
{
    Console.Error.WriteLine(string.Join("; ", compiled.Diagnostics.Select(d => $"{d.Code} {d.Message}")));
    return 1;
}

var app = new WindowsApp();
await using CSharpScriptSession<IWindowsSession, WindowsSession> session = await CSharpScriptSession.StartAsync<
    IWindowsApp,
    IWindowsSession,
    WindowsSession
>(app);
CSharpScriptRun run = await session.StartScriptAsync(script);
await run.Completion.WaitAsync(TimeSpan.FromSeconds(60));
if (app.Reported != 42)
{
    Console.Error.WriteLine($"The script reported {app.Reported}.");
    return 1;
}
Console.WriteLine("Passed.");
return 0;

sealed class WindowsApp : IWindowsApp
{
    public double Reported { get; private set; }

    public Task ReportAsync(double value, CancellationToken cancellationToken)
    {
        Reported = value;
        return Task.CompletedTask;
    }
}
