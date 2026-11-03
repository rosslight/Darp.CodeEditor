using System.Diagnostics;
using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.CSharp.Execution;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.LanguageServices;
using ScriptApi;

// A host that uses only the packages: it completes a script through the C# language server, runs a script in the
// runner and calls the handler it returns, and reports which .NET runtime each helper runs on.
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var environment = new CSharpScriptEnvironment
{
    // NativeAOT apps ship the script API as a file next to the app; JIT apps have it there anyway.
    Globals = new CSharpScriptGlobals(
        Path.Combine(AppContext.BaseDirectory, "ScriptApi.dll"),
        typeof(Globals).FullName!
    ),
    Imports = ["System", "System.Threading", "System.Threading.Tasks", "ScriptApi"],
};

LspLanguageService service = CSharpLanguageServer.CreateService(
    environment,
    new CSharpLanguageServerOptions { ResponseTimeout = TimeSpan.FromMinutes(1), Log = Console.Error.WriteLine }
);
try
{
    await service.PrewarmAsync(timeout.Token);
    const string text = "var sum = Calculator.";
    service.OpenDocument(new DocumentSnapshot("script.csx", 1, text));
    CompletionList? completions = await service.GetCompletionsAsync(
        new DocumentVersion("script.csx", 1),
        text.Length,
        new CompletionTrigger(CompletionTriggerKind.Invoke),
        timeout.Token
    );
    Console.WriteLine($"completion: {string.Join(", ", completions?.Items.Select(i => i.DisplayText) ?? [])}");
    Console.WriteLine($"server runtime: {RuntimeDirectory(service.ServerProcessId)}");
}
finally
{
    await service.DisposeAsync();
}

await using CSharpScriptRunner runner = await CSharpScriptRunner.StartAsync(
    new CSharpScriptRunnerOptions
    {
        Environment = environment,
        Services = [CSharpScriptService.Create<ICalculator>(new Calculator())],
        Output = Console.Error.WriteLine,
    },
    timeout.Token
);
using IHandler handler = await runner.EvaluateAsync<IHandler>(
    """
    class Handler(ICalculator calculator) : IHandler
    {
        public Task<int> HandleAsync(int value, CancellationToken cancellationToken) =>
            calculator.AddAsync(value, 2, cancellationToken);
        public Task<string> GetRuntimeDirectoryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "");
        public void Dispose() { }
    }
    new Handler(Calculator)
    """,
    timeout.Token
);
Console.WriteLine($"handler: {await handler.HandleAsync(40, timeout.Token)}");
Console.WriteLine($"runner runtime: {await handler.GetRuntimeDirectoryAsync(timeout.Token)}");
return 0;

/// <summary>The directory of the .NET runtime a process runs on, where its CoreCLR library was loaded from.</summary>
static string RuntimeDirectory(int? processId)
{
    if (processId is not { } id)
        return "(no process)";
    using Process process = Process.GetProcessById(id);
    foreach (ProcessModule module in process.Modules)
    {
        string name = Path.GetFileNameWithoutExtension(module.ModuleName);
        if (name is "coreclr" or "libcoreclr")
            return Path.GetDirectoryName(module.FileName) ?? "";
    }
    return "(no CoreCLR)";
}

internal sealed class Calculator : ICalculator
{
    public Task<int> AddAsync(int a, int b, CancellationToken cancellationToken) => Task.FromResult(a + b);
}
