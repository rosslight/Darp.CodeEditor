// Proves the packages from a customer's point of view: editing and compiling a script against what the app marked,
// before Avalonia is set up, in a JIT or NativeAOT app. Exits with 0 when every check passes.
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ConsumerApi;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;

const string Valid = """
    var greeting = Hello() + FromBase();
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    var json = ConsumerCompatibleB.Json.Write();
    var target = ConsumerMultiTargeted.Target.Name;
    """;

var errors = new List<string>();
using var server = new CSharpScriptServer(new CSharpScriptServerOptions { StandardError = Console.Error.WriteLine });
var environment = new CSharpScriptEnvironment<ConsumerGlobals>();

// The main thread waits without awaiting: it must stay the thread that sets up Avalonia.
Task.Run(async () =>
    {
        // Editing: the server reports what a script can't name, and takes the correction.
        using CSharpScriptDocument<ConsumerGlobals> document = server.CreateDocument(environment, "Missing();");
        await Until(() => document.Diagnostics.Any(d => d.Code == "CS0103"));
        document.Text = Valid;
        await Until(() => document.Diagnostics.Count == 0);

        CSharpScriptCompilation<ConsumerGlobals> compiled = await document.CompileAsync();
        Check(compiled.Succeeded, $"The document did not compile: {Describe(compiled.Diagnostics)}");

        // A dependency of a marked reference binds signatures, as FromBase above, but scripts can't name it.
        CSharpScriptCompilation<ConsumerGlobals> hidden = await server.CompileAsync(
            environment,
            "var hidden = new ConsumerDependency.Base();"
        );
        Check(!hidden.Succeeded, "A script named an unmarked dependency.");

        CSharpScriptCompilation<ConsumerGlobals> empty = await server.CompileAsync(environment, "// nothing to run\n");
        Check(empty.Succeeded, $"An empty script did not compile: {Describe(empty.Diagnostics)}");
    })
    .GetAwaiter()
    .GetResult();

Check(Dispatcher.FromThread(Thread.CurrentThread) is null, "A dispatcher exists before setup.");
AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
Check(Dispatcher.UIThread.CheckAccess(), "The UI thread is not the thread that set up Avalonia.");

foreach (string error in errors)
    Console.Error.WriteLine(error);
Console.WriteLine(errors.Count == 0 ? "Passed." : "Failed.");
return errors.Count == 0 ? 0 : 1;

void Check(bool condition, string error)
{
    if (!condition)
        errors.Add(error);
}

static string Describe(IEnumerable<Diagnostic> diagnostics) =>
    string.Join("; ", diagnostics.Select(d => $"{d.Code} {d.Message}"));

static async Task Until(Func<bool> condition)
{
    for (int i = 0; i < 6000 && !condition(); i++)
        await Task.Delay(10);
    if (!condition())
        throw new TimeoutException("A condition did not hold within 60 s.");
}
