// Checks, in a process of its own, that the library leaves Avalonia's dispatcher alone before Avalonia is set up. In
// Avalonia 12 the first dispatcher becomes the UI thread's, so a pool thread that touched it would break the setup
// below. Exits with 0 when every check passes.
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Darp.CodeEditor;

if (IsCreated("s_yaml"))
    return Fail("YAML was created before it was used.");
_ = CodeLanguage.PlainText;
if (IsCreated("s_yaml"))
    return Fail("Using plain text created YAML.");

// The main thread waits without awaiting: it must stay the thread that sets up Avalonia.
CodeDocument document = Task.Run(async () =>
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
        return created;
    })
    .GetAwaiter()
    .GetResult();

if (Dispatcher.FromThread(Thread.CurrentThread) is not null)
    return Fail("A dispatcher exists before setup.");

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

Console.WriteLine("Passed.");
return 0;

// Whether a built-in language was created: the library keeps each in a Lazy field.
static bool IsCreated(string field) =>
    typeof(CodeLanguage).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
        is Lazy<CodeLanguage> lazy
        ? lazy.IsValueCreated
        : throw new MissingFieldException(nameof(CodeLanguage), field);

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
