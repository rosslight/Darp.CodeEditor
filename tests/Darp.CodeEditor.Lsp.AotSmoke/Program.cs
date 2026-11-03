using System.Diagnostics;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Darp.CodeEditor.Lsp.Testing;

// Checks the library in a NativeAOT build. "server" runs the toy server on stdio. Without arguments, the program
// starts itself as the toy server and talks to it, then talks to luau-lsp if fetch-luau-lsp.sh downloaded it.
if (args is ["server"])
{
    var toy = new ToyServer(Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error.WriteLine);
    await using (toy.Server)
        return await toy.Server.RunAsync();
}

const string Text = "alpha error\nslow stale add(1, 2)\n";
var failures = new List<string>();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
CancellationToken cancellationToken = timeout.Token;

await CheckToyServerAsync();
await CheckExitWithoutShutdownAsync();
if (LuauLsp.FindExecutable() is { } luau)
    await CheckLuauLspAsync(luau);
else
    Console.WriteLine("skip luau-lsp is missing; run tests/Darp.CodeEditor.Lsp.Tests/fetch-luau-lsp.sh");

Console.WriteLine(failures.Count == 0 ? "All checks passed." : $"{failures.Count} checks failed.");
return failures.Count == 0 ? 0 : 1;

async Task CheckToyServerAsync()
{
    using Process process = StartToyServer();
    await using var client = new ToyClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "Hi");

    InitializeResult? initialized = await client.InitializeAsync(cancellationToken);
    Check(initialized?.Capabilities.TextDocumentSync?.Change == TextDocumentSyncKind.Incremental, "initialize");
    await client.OpenAsync(Text);
    PublishDiagnosticsParams diagnostics = await client.DiagnosticsAsync(1, cancellationToken);
    Check(diagnostics.Diagnostics is [{ Code: "TOY1" }], "didOpen publishes diagnostics");

    CompletionList? list = await client.CompletionAsync(new Position(0, 2), cancellationToken);
    Check(
        list is { IsIncomplete: true, Items: [{ TextEdit: { } edit }, _] } && edit.Insert != edit.Replace,
        "completion with insert and replace edits"
    );
    CompletionItem? resolved = list is { Items: [CompletionItem first, ..] }
        ? await client.Connection.SendRequestAsync(LspMethods.CompletionResolve, first, cancellationToken)
        : null;
    Check(
        resolved is { Detail: "resolved 1", Data: { } data }
            && data.GetRawText() == """{"id":1,"nested":[true,null]}""",
        "completionItem/resolve keeps data verbatim"
    );
    Hover? hover = await client.HoverAsync(new Position(0, 1), cancellationToken);
    Check(hover?.Contents.Value == "Hi, **alpha**", "hover with the setting from workspace/configuration");
    SignatureHelp? help = await client.SignatureHelpAsync(new Position(1, 18), cancellationToken);
    Check(
        help is { ActiveParameter: 1, Signatures: [{ Parameters: [{ Label: { Start: 4, End: 9 } }, _] }] },
        "signature help with label offsets"
    );
    SemanticTokens? tokens = await client.SemanticTokensAsync(
        new DocumentRange(new(1, 11), new(1, 20)),
        cancellationToken
    );
    Check(tokens?.Data is [1, 11, 3, 0, 0, 0, 4, 1, 1, 0, 0, 3, 1, 1, 0], "semanticTokens/range");

    using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
        Check(
            await CatchAsync(client.HoverAsync(new Position(1, 1), cancel.Token)) is OperationCanceledException,
            "cancel"
        );
    Task<Hover?> stale = client.HoverAsync(new Position(1, 7), cancellationToken);
    await client.ChangeAsync(2, new DocumentRange(default, default), "x");
    Check(await CatchAsync(stale) is JsonRpcException { Code: ErrorCodes.ContentModified }, "stale request");

    await client.ShutdownAsync(cancellationToken);
    await process.WaitForExitAsync(cancellationToken);
    Check(process.ExitCode == 0, "shutdown and exit end with exit code 0");
}

async Task CheckExitWithoutShutdownAsync()
{
    using Process process = StartToyServer();
    await using var client = new ToyClient(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, "Hi");
    await client.InitializeAsync(cancellationToken);
    await client.Connection.SendNotificationAsync(LspMethods.Exit, default);
    await process.WaitForExitAsync(cancellationToken);
    Check(process.ExitCode == 1, "exit without shutdown ends with exit code 1");
}

async Task CheckLuauLspAsync(string executable)
{
    DirectoryInfo root = Directory.CreateTempSubdirectory("luau-lsp-");
    try
    {
        string script = Path.Combine(root.FullName, "main.luau");
        await File.WriteAllTextAsync(script, LuauLsp.Script, cancellationToken);
        string uri = new Uri(script).AbsoluteUri;
        await using var luau = new LuauLsp(executable);
        JsonRpcConnection rpc = luau.Connection;

        InitializeResult? initialized = await rpc.SendRequestAsync(
            LspMethods.Initialize,
            LuauLsp.InitializeParams(new Uri(root.FullName).AbsoluteUri),
            cancellationToken
        );
        Check(initialized?.Capabilities.CompletionProvider is not null, "luau-lsp initialize");
        await rpc.SendNotificationAsync(LspMethods.Initialized, new InitializedParams());
        await rpc.SendNotificationAsync(
            LspMethods.DidOpen,
            new DidOpenTextDocumentParams(new TextDocumentItem(uri, "luau", 1, LuauLsp.Script))
        );
        CompletionList? completion = await rpc.SendRequestAsync(
            LspMethods.Completion,
            new CompletionParams(
                new TextDocumentIdentifier(uri),
                LuauLsp.MemberAccess,
                new CompletionContext(CompletionTriggerKind.TriggerCharacter, ".")
            ),
            cancellationToken
        );
        Check(completion?.Items.Any(i => i.Label == "format") == true, "luau-lsp completion");
        Hover? hover = await rpc.SendRequestAsync(
            LspMethods.Hover,
            new HoverParams(new TextDocumentIdentifier(uri), LuauLsp.Call),
            cancellationToken
        );
        Check(hover?.Contents.Value.Contains("greet", StringComparison.Ordinal) == true, "luau-lsp hover");
        await rpc.SendRequestAsync(LspMethods.Shutdown, default, cancellationToken);
        await rpc.SendNotificationAsync(LspMethods.Exit, default);
        Check(await luau.WaitForExitAsync(cancellationToken) == 0, "luau-lsp shutdown and exit");
        if (failures.Count > 0)
            Console.WriteLine(luau.Output);
    }
    finally
    {
        root.Delete(recursive: true);
    }
}

Process StartToyServer()
{
    var startInfo = new ProcessStartInfo(
        Environment.ProcessPath ?? throw new InvalidOperationException("No process path.")
    )
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    startInfo.ArgumentList.Add("server");
    return Process.Start(startInfo) ?? throw new InvalidOperationException("The toy server did not start.");
}

void Check(bool passed, string what)
{
    Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {what}");
    if (!passed)
        failures.Add(what);
}

static async Task<Exception?> CatchAsync(Task task)
{
    try
    {
        await task;
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}
