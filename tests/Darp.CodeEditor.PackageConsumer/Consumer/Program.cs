// Proves the packages from a customer's point of view: editing, compiling and running a script against what the app
// marked, before Avalonia is set up, in a JIT or NativeAOT app. Exits with 0 when every check passes.
using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ConsumerApi;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;

const string Valid = """
    var greeting = Hello() + FromBase();
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    var json = ConsumerCompatibleB.Json.Write();
    var target = ConsumerMultiTargeted.Target.Name;
    """;

// Uses every kind of marked reference in the runner, and waits for the app.
const string Runs = """
    var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
    connection.Open();
    var command = connection.CreateCommand();
    command.CommandText = "select 40 + 2";
    string used = $"{command.ExecuteScalar()} {ConsumerCompatibleB.Json.Write()} {ConsumerMultiTargeted.Target.Name}";
    Console.WriteLine("printed by " + Input.Name);
    await AskAsync($"{Input.Name}: {used} {Hello()}{FromBase()}");
    """;

var errors = new List<string>();
using var server = new CSharpScriptServer(new CSharpScriptServerOptions { StandardError = Console.Error.WriteLine });
var environment = new CSharpScriptEnvironment<ConsumerGlobals>();

if (args is ["--hold-a-session"])
{
    HoldASession();
    return 0;
}

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

        await ProveSessionAsync();
        await ProveTheRunnerEndsWithTheAppAsync();
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

// Two overlapping runs, calls in both directions, a failure at its line, and the order of the end.
async Task ProveSessionAsync()
{
    var questions = new List<string>();
    var printed = new List<string>();
    var firstAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var app = new ConsumerApp(
        async (question, token) =>
        {
            lock (questions)
                questions.Add(question);
            if (question.StartsWith("first:", StringComparison.Ordinal))
            {
                firstAsked.TrySetResult();
                await secondEnded.Task.WaitAsync(token);
            }
            return "answer to " + question;
        }
    );
    CSharpScriptCompilation<ConsumerGlobals> compiled = await server.CompileAsync(environment, Runs);
    Check(compiled.Succeeded, $"The session's script did not compile: {Describe(compiled.Diagnostics)}");
    CSharpScriptAssembly<ConsumerGlobals> script = compiled.Script!;
    await using CSharpScriptSession<IConsumerSession, ConsumerSession> session = await CSharpScriptSession.StartAsync<
        IConsumerApp,
        IConsumerSession,
        ConsumerSession
    >(
        app,
        new CSharpScriptSessionOptions
        {
            Output = line =>
            {
                lock (printed)
                    printed.Add(line);
            },
        }
    );

    CSharpScriptRun first = await session.StartScriptAsync(script, new ConsumerInput("first"));
    await firstAsked.Task.WaitAsync(TimeSpan.FromSeconds(60));
    CSharpScriptRun second = await session.StartScriptAsync(script, new ConsumerInput("second"));
    await second.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    Check(!first.Completion.IsCompleted, "The first run did not overlap the second.");
    secondEnded.SetResult();
    await first.Completion.WaitAsync(TimeSpan.FromSeconds(60));
    const string Used = """42 {"from":"B"} net10.0 hello from the base""";
    Check(questions.Contains($"first: {Used}"), $"The first run asked: {string.Join(" | ", questions)}");
    Check(questions.Contains($"second: {Used}"), $"The second run asked: {string.Join(" | ", questions)}");

    string back = await session.Remote.AskBackAsync("back", CancellationToken.None);
    Check(back == "answer to back", $"The call from the app through the session gave '{back}'.");

    CSharpScriptCompilation<ConsumerGlobals> failing = await server.CompileAsync(
        environment,
        "var line = 1;\nthrow new FormatException(\"on line two\");"
    );
    CSharpScriptRun failed = await session.StartScriptAsync(failing.Script!, new ConsumerInput("failing"));
    try
    {
        await failed.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Check(false, "The failing script did not fail.");
    }
    catch (CSharpScriptException failure)
    {
        Check(failure.ScriptPosition?.Line == 2, $"The failure is at {failure.ScriptPosition}.");
        Check(failure.ScriptSnapshot == failing.Snapshot, "The failure points into another text.");
    }

    await session.DisposeAsync();
    CSharpScriptSessionExit exit = await session.Exited;
    Check(exit is { Reason: CSharpScriptSessionExitReason.Disposed, ExitCode: 0 }, $"The session ended {exit.Reason}.");
    await Until(() =>
    {
        lock (printed)
            return printed.Contains("printed by first") && printed.Contains("printed by second");
    });
}

// A second process of this app holds a session and is killed; its runner must end without any help.
async Task ProveTheRunnerEndsWithTheAppAsync()
{
    string self = Environment.ProcessPath!;
    bool onDotnet = Path.GetFileNameWithoutExtension(self) == "dotnet";
    var info = new ProcessStartInfo(
        self,
        onDotnet ? [Environment.GetCommandLineArgs()[0], "--hold-a-session"] : ["--hold-a-session"]
    )
    {
        RedirectStandardOutput = true,
    };
    using Process holder = Process.Start(info)!;
    string? line;
    while (
        (line = await holder.StandardOutput.ReadLineAsync()) is not null
        && !line.StartsWith("runner ", StringComparison.Ordinal)
    ) { }
    if (line is null)
    {
        Check(false, "The holder ended without a session.");
        return;
    }
    using Process runner = Process.GetProcessById(int.Parse(line["runner ".Length..]));
    holder.Kill();
    try
    {
        await runner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
    catch (TimeoutException)
    {
        runner.Kill();
        Check(false, "The runner outlived the app that was killed.");
    }
}

// On Windows the script makes its runner ignore the end of its input, so that only the Job Object can end it.
void HoldASession()
{
    Task.Run(async () =>
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var app = new ConsumerApp(
                async (_, token) =>
                {
                    ready.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                    return "";
                }
            );
            CSharpScriptCompilation<ConsumerGlobals> compiled = await server.CompileAsync(
                environment,
                """
                if (OperatingSystem.IsWindows())
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => Thread.Sleep(Timeout.Infinite);
                await AskAsync("holding");
                """
            );
            CSharpScriptSession<IConsumerSession, ConsumerSession> session = await CSharpScriptSession.StartAsync<
                IConsumerApp,
                IConsumerSession,
                ConsumerSession
            >(app);
            await session.StartScriptAsync(compiled.Script!, new ConsumerInput("holding"));
            await ready.Task;
            Console.WriteLine($"runner {await session.Remote.GetProcessIdAsync(CancellationToken.None)}");
            await Task.Delay(Timeout.Infinite);
        })
        .GetAwaiter()
        .GetResult();
}

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

sealed class ConsumerApp : IConsumerApp
{
    private readonly Func<string, CancellationToken, Task<string>> _answer;

    public ConsumerApp(Func<string, CancellationToken, Task<string>> answer) => _answer = answer;

    public Task<string> AskAsync(string question, CancellationToken cancellationToken) =>
        _answer(question, cancellationToken);
}
