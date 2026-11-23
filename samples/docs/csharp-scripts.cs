#:project ../../src/Darp.CodeEditor.CSharpScript.Runner/Darp.CodeEditor.CSharpScript.Runner.csproj
#:package StreamJsonRpc

using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using PolyType;
using StreamJsonRpc;

[assembly: ExportRpcContractProxies]

// The parts of a script API in one file, so that CI compiles them. A real app keeps them in the projects that the guide
// names and marks those with CSharpScriptReference; only then can its scripts run.
Console.WriteLine("Compile-only: see samples/Darp.CodeEditor.Sample for an app that runs scripts.");

// --8<-- [start:bridge]
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IAppSide
{
    Task<string> AskAsync(string question, CancellationToken cancellationToken);
}

[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ISessionSide
{
    Task<string> GetStatusAsync(CancellationToken cancellationToken);
}

[GenerateShape]
public sealed partial record RunInput(string Name);

// --8<-- [end:bridge]

// --8<-- [start:runner-side]
public sealed class Session
    : ISessionSide,
        IScriptSessionObject<Session, IAppSide>,
        IScriptGlobalsFactory<Globals, RunInput>
{
    private readonly IAppSide _app;

    private Session(IAppSide app) => _app = app;

    static Session IScriptSessionObject<Session, IAppSide>.Create(IAppSide app) => new(app);

    Globals IScriptGlobalsFactory<Globals, RunInput>.CreateGlobals(RunInput input, CancellationToken stop) =>
        new(_app, input, stop);

    public Task<string> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult("ready");
}

/// <summary>What scripts see: its public members can be used without qualification.</summary>
public sealed class Globals
{
    private readonly IAppSide _app;

    internal Globals(IAppSide app, RunInput input, CancellationToken stop)
    {
        _app = app;
        Input = input;
        Stop = stop;
    }

    /// <summary>What the app gave this run.</summary>
    public RunInput Input { get; }

    /// <summary>Cancelled when the app stops the run. Pass it to everything that waits.</summary>
    public CancellationToken Stop { get; }

    /// <summary>Asks the app, and waits for its answer.</summary>
    public Task<string> AskAsync(string question) => _app.AskAsync(question, Stop);
}

// --8<-- [end:runner-side]

public sealed class AppSide : IAppSide
{
    public Task<string> AskAsync(string question, CancellationToken cancellationToken) => Task.FromResult("yes");
}

public static class Examples
{
    public static CSharpScriptDocument<Globals> Edit(CodeEditor editor, CSharpScriptServer server)
    {
        // --8<-- [start:edit]
        var environment = new CSharpScriptEnvironment<Globals> { Imports = ["System.Text.Json"] };
        CSharpScriptDocument<Globals> document = server.CreateDocument(environment, """await AskAsync("Continue?");""");
        editor.Document = document;
        // --8<-- [end:edit]
        return document;
    }

    public static async Task RunAsync(CSharpScriptDocument<Globals> document, CancellationToken stop)
    {
        // --8<-- [start:run]
        CSharpScriptCompilation<Globals> compilation = await document.CompileAsync(stop);
        if (compilation.Script is not { } script)
            return; // compilation.Diagnostics says why

        await using CSharpScriptSession<ISessionSide, Session> session = await CSharpScriptSession.StartAsync<
            IAppSide,
            ISessionSide,
            Session
        >(new AppSide(), new CSharpScriptSessionOptions { Output = Console.WriteLine }, stop);

        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("first"), stop);
        await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
        // --8<-- [end:run]
    }

    public static async Task ReportAsync(
        CSharpScriptSession<ISessionSide, Session> session,
        CSharpScriptRun run,
        CancellationToken stop
    )
    {
        // --8<-- [start:failures]
        try
        {
            await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Console.WriteLine("Stopped.");
        }
        catch (CSharpScriptException failure)
        {
            // ScriptSnapshot is the compiled text that ScriptPosition points into.
            Console.WriteLine($"Line {failure.ScriptPosition?.Line}: {failure.ScriptExceptionType}: {failure.Message}");
        }
        catch (ConnectionLostException)
        {
            CSharpScriptSessionExit exit = await session.Exited;
            Console.WriteLine($"The session ended ({exit.Reason}):\n{exit.StandardError}");
        }
        // --8<-- [end:failures]
    }
}
