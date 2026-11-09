// Checks, in a process of its own, that the library leaves Avalonia's dispatcher alone before Avalonia is set up. In
// Avalonia 12 the first dispatcher becomes the UI thread's, so a pool thread that touched it would break the setup
// below. Exits with 0 when every check passes. The arguments are the command line of the fake language server.
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Darp.CodeEditor;

if (args is not [var serverFileName, .. var serverArguments])
    return Fail("Pass the fake language server's command line.");
var server = new LanguageServer(new LanguageServerOptions { FileName = serverFileName, Arguments = serverArguments });

// Where each event came from: before setup, where the change happened; after it, the UI thread.
var events = new ConcurrentQueue<(string Name, Thread Thread)>();
server.PropertyChanged += (_, e) => events.Enqueue(($"server {e.PropertyName}", Thread.CurrentThread));

// The main thread waits without awaiting: it must stay the thread that sets up Avalonia.
(CodeDocument document, CodeDocument served) = Task.Run(async () =>
    {
        // Languages and themes load on the thread pool, with avares:// files read without Avalonia's services.
        if ((await CodeLanguage.Yaml.Loaded).Grammar is null)
            throw new InvalidOperationException("YAML did not load.");
        if (await CodeEditorColorTheme.DarkPlus.Loaded is null)
            throw new InvalidOperationException("Dark+ did not load.");

        CodeDocument created = CodeDocument.Create(CodeLanguage.Yaml, "key: value");
        TextSnapshot before = created.CurrentSnapshot;
        created.Replace(new TextRange(0, 3), "name");
        created.Undo();
        created.Redo();
        created.MarkUnmodified(created.CurrentSnapshot);
        _ = created.MapToCurrent(before, new TextRange(5, 5));
        _ = created.CurrentSnapshot.GetPosition(5);

        // A server and its document: the fake server echoes the text as a diagnostic.
        CodeDocument fromServer = server.CreateDocument(CodeLanguage.PlainText, "before");
        fromServer.PropertyChanged += (_, e) => events.Enqueue(($"document {e.PropertyName}", Thread.CurrentThread));
        await server.StartAsync();
        fromServer.Replace(new TextRange(6, 0), " setup");
        await Until(() => fromServer.Diagnostics is [{ Message: "before setup" }]);
        return (created, fromServer);
    })
    .GetAwaiter()
    .GetResult();

if (Dispatcher.FromThread(Thread.CurrentThread) is not null)
    return Fail("A dispatcher exists before setup.");
if (events.IsEmpty || events.Any(e => e.Thread == Thread.CurrentThread))
    return Fail("Before setup, the server and its document raised no events, or raised them on the main thread.");

AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
if (!Dispatcher.UIThread.CheckAccess())
    return Fail("The UI thread is not the thread that set up Avalonia.");

// Set up, the document binds to the UI thread: it changes there, and only there.
document.Replace(new TextRange(0, 0), "# ");
bool threwOffThread = Task.Run(() =>
    {
        try
        {
            document.Replace(new TextRange(0, 0), "# ");
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    })
    .GetAwaiter()
    .GetResult();
if (!threwOffThread)
    return Fail("The document changed on a pool thread after setup.");
if (document.Text != "# name: value")
    return Fail($"Unexpected text: {document.Text}");

// Set up, the server's events and its document's diagnostics come on the UI thread.
events.Clear();
served.Replace(new TextRange(0, 0), "after ");
if (!RunUiUntil(() => served.Diagnostics is [{ Message: "after before setup" }]))
    return Fail("The diagnostics after setup did not arrive.");
server.Dispose();
if (!RunUiUntil(() => events.Any(e => e.Name == "server State")))
    return Fail("The server's state change after setup was not raised.");
if (events.Any(e => e.Thread != Thread.CurrentThread))
    return Fail("After setup, an event of the server or its document was raised off the UI thread.");

Console.WriteLine("Passed.");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

// Polls a condition on the thread pool, before setup.
static async Task Until(Func<bool> condition)
{
    for (int i = 0; i < 3000 && !condition(); i++)
        await Task.Delay(10);
    if (!condition())
        throw new TimeoutException("A condition did not hold within 30 s.");
}

// Runs the UI thread's jobs until a condition holds, for at most 30 s.
static bool RunUiUntil(Func<bool> condition)
{
    for (int i = 0; i < 3000 && !condition(); i++)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(10);
    }
    return condition();
}
