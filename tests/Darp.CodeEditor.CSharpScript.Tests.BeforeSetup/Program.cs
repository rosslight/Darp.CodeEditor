// Checks, in a process of its own, that compiling leaves Avalonia's dispatcher alone before Avalonia is set up: in
// Avalonia 12 the first dispatcher becomes the UI thread's, so a pool thread that touched it would break the setup
// below. Exits with 0 when every check passes.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Darp.CodeEditor.CSharpScript;
using ScriptTests.Api;

using var server = new CSharpScriptServer();
var environment = new CSharpScriptEnvironment<ScriptTestGlobals>();

// The main thread waits without awaiting: it must stay the thread that sets up Avalonia.
(bool fromText, bool fromDocument) = Task.Run(async () =>
    {
        CSharpScriptCompilation<ScriptTestGlobals> text = await server.CompileAsync(environment, "Add(1, 2)");
        using CSharpScriptDocument<ScriptTestGlobals> document = server.CreateDocument(environment, "Add(3, 4)");
        CSharpScriptCompilation<ScriptTestGlobals> compiled = await document.CompileAsync();
        return (text.Succeeded, compiled.Succeeded);
    })
    .GetAwaiter()
    .GetResult();

if (!fromText || !fromDocument)
    return Fail("A script did not compile before setup.");
if (Dispatcher.FromThread(Thread.CurrentThread) is not null)
    return Fail("A dispatcher exists before setup.");

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
if (!Dispatcher.UIThread.CheckAccess())
    return Fail("The UI thread is not the thread that set up Avalonia.");

Console.WriteLine("Passed.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
