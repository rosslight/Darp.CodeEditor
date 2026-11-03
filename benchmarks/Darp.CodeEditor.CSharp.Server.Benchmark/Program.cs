using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.Demo;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.LspTesting;
using Darp.CodeEditor.Samples;

// Measures the C# script language server against the in-process service on the prototype's sample script:
// process start to first completion, completion latency while typing one request at a time, the same under
// overlapping requests in two documents, memory and payload sizes.
//
//   dotnet run --project benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark -c Release [-- --runs 5]
//
// Publishes the server framework-dependent and with ReadyToRun into measurements/csharp-server/ and writes
// report.md and results.json there.
if (args is ["--measure-in-process", string first])
{
    await InProcess.MeasureAsync(first == "member");
    return 0;
}

int runs = args.SkipWhile(a => a != "--runs").Skip(1).Select(int.Parse).FirstOrDefault(5);
string repository = Benchmark.FindRepository();
string output = Path.Combine(repository, "measurements", "csharp-server");
Directory.CreateDirectory(output);
string loadBefore = Benchmark.LoadAverage();

Console.WriteLine("Publishing the server...");
Benchmark.Published framework = Benchmark.Publish(repository, Path.Combine(output, "publish-fdd"), []);
Benchmark.Published readyToRun = Benchmark.Publish(
    repository,
    Path.Combine(output, "publish-r2r"),
    ["-r", RuntimeInformation.RuntimeIdentifier, "--self-contained", "false", "-p:PublishReadyToRun=true"]
);

// By default the server filters lists of more than 1,000 items by the typed text and keeps the 1,000 best matches.
Variant[] variants =
[
    new("server", framework, []),
    new("server R2R", readyToRun, []),
    new("R2R, unfiltered", readyToRun, ["--completion-filter-above", "1000000"]),
    new("R2R, filtered, no max", readyToRun, ["--completion-limit", "1000000"]),
    new("R2R, 300 max", readyToRun, ["--completion-limit", "300"]),
];

var results = new List<RunResult>();
for (int run = 1; run <= runs; run++)
{
    foreach (bool member in new[] { true, false })
    {
        Console.WriteLine($"Run {run}/{runs}, first completion: {(member ? "member" : "word")} list");
        results.Add(await InProcess.RunChildAsync(member));
        foreach (Variant variant in variants)
            results.Add(await ServerRun.MeasureAsync(variant, member));
    }
}

string report = Report.Create(results, variants, runs, loadBefore, Benchmark.LoadAverage());
await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report);
await File.WriteAllTextAsync(
    Path.Combine(output, "results.json"),
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true })
);
Console.WriteLine(report);
return 0;

/// <summary>A published server and the arguments it runs with.</summary>
internal sealed record Variant(string Name, Benchmark.Published Published, string[] ServerArguments);

/// <param name="FirstMs">Process start to the first non-empty completion list.</param>
/// <param name="WarmMemberMs">Member lists while typing one request at a time, after the first rounds.</param>
/// <param name="WarmRefineMs">
/// Requests for an incomplete word list again while the word is typed; only servers that filter send them.
/// </param>
internal sealed record RunResult(
    string Name,
    bool FirstIsMember,
    double FirstMs,
    int FirstItems,
    double InitializeMs,
    List<double> WarmMemberMs,
    List<double> WarmWordMs,
    List<double> WarmRefineMs,
    int MemberItems,
    int WordItems,
    int MemberBytes,
    int WordBytes,
    OverlapResult Overlap,
    OverlapResult Stress,
    double RssMiB,
    double PeakRssMiB
);

/// <param name="FreshCompletionMs">
/// Completion requests the client did not cancel and that were answered with a result: request to parsed response.
/// </param>
/// <param name="FreshTokensMs">Semantic token requests the client did not cancel, answered with a result.</param>
/// <param name="Kept">How the requests the client did not cancel were answered.</param>
/// <param name="Superseded">How the requests the client cancelled, because newer ones replaced them, were answered.</param>
/// <param name="RecoveryMs">
/// The last keystroke to the answer of a fresh word list asked for right after it, behind any backlog.
/// </param>
/// <param name="RecoveryItems">The items of that word list.</param>
/// <param name="DrainMs">The last keystroke to the answer of the last outstanding request.</param>
internal sealed record OverlapResult(
    List<double> FreshCompletionMs,
    List<double> FreshTokensMs,
    int Requests,
    AnswerCounts Kept,
    AnswerCounts Superseded,
    double RecoveryMs,
    int RecoveryItems,
    double DrainMs
);

/// <param name="Bytes">The size of the response message; 0 in-process.</param>
internal sealed record Outcome(Answer Answer, int Items, bool Incomplete, int Bytes, double Ms)
{
    /// <summary>This outcome, for a request whose result is measured.</summary>
    /// <exception cref="InvalidOperationException">The request was answered with an error.</exception>
    public Outcome WithResult(string what) =>
        Answer == Answer.Result ? this : throw new InvalidOperationException($"{what} was answered with {Answer}.");
}

/// <summary>How a request was answered.</summary>
internal enum Answer
{
    /// <summary>A result, which may be null.</summary>
    Result,

    /// <summary><c>RequestCancelled</c>, or cancelled in-process.</summary>
    Cancelled,

    /// <summary><c>ContentModified</c>: the server found the result stale.</summary>
    ContentModified,

    /// <summary>Any other error.</summary>
    Failed,
}

/// <summary>How many requests were answered in each way.</summary>
internal sealed record AnswerCounts(int Results, int Cancelled, int ContentModified, int Failed)
{
    public static AnswerCounts Zero { get; } = new(0, 0, 0, 0);

    public static AnswerCounts Of(IEnumerable<Answer> answers)
    {
        Answer[] all = [.. answers];
        return new AnswerCounts(
            all.Count(a => a == Answer.Result),
            all.Count(a => a == Answer.Cancelled),
            all.Count(a => a == Answer.ContentModified),
            all.Count(a => a == Answer.Failed)
        );
    }

    public static AnswerCounts operator +(AnswerCounts a, AnswerCounts b) =>
        new(a.Results + b.Results, a.Cancelled + b.Cancelled, a.ContentModified + b.ContentModified, a.Failed + b.Failed);

    public override string ToString() => $"{Results} / {Cancelled} / {ContentModified} / {Failed}";
}

/// <summary>A request in flight.</summary>
internal sealed class Request
{
    private readonly Func<Task> _cancel;

    public Request(Task<Outcome> result, Func<Task> cancel)
    {
        Result = result;
        _cancel = cancel;
    }

    public Task<Outcome> Result { get; }

    /// <summary>The client cancelled the request because a newer one replaced it.</summary>
    public bool Superseded { get; private set; }

    public Task CancelAsync()
    {
        Superseded = true;
        return _cancel();
    }
}

internal enum CompletionKind
{
    /// <summary>The first letter of a word was typed: the client invokes completion.</summary>
    Word,

    /// <summary>"." was typed.</summary>
    Member,

    /// <summary>The list is incomplete and the user typed another letter of the word.</summary>
    Refine,
}

/// <summary>The language features the benchmark uses, from the server or the in-process service.</summary>
internal interface IScriptService
{
    Task OpenAsync(string document, string text);

    /// <summary>Appends <paramref name="typed"/> to the document.</summary>
    Task TypeAsync(string document, string typed);

    /// <summary>Requests completion at the end of the document.</summary>
    Request Complete(string document, CompletionKind kind);

    /// <summary>Requests semantic tokens for the 40 lines before the end of the document.</summary>
    Request Tokens(string document);

    /// <summary>Requests signature help at the end of the document.</summary>
    Request SignatureHelp(string document);
}

/// <summary>The typing both services go through.</summary>
internal static class Typing
{
    public const string MainDocument = "main";
    public const int Rounds = 20;
    public const int SkippedRounds = 2;
    public const string SecondDocument = "second";
    public const int OverlapKeystrokes = 150;
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(40);

    /// <summary>The cadence of the stress phase: faster than the server answers, so requests pile up.</summary>
    public static readonly TimeSpan StressDelay = TimeSpan.FromMilliseconds(10);

    /// <summary>What the overlap typists type, one character per keystroke, over and over.</summary>
    private const string Statement = "\nDevice.ReadTemperature();";

    /// <summary>The script with a line that ends where the first completion is requested.</summary>
    public static string Initial(bool member) => SampleDocuments.CSharpScript + (member ? "\nDevice." : "\nD");

    /// <summary>
    /// One request at a time, 40 ms between chunks: <c>\nD</c> (word list), each letter of <c>evice</c> (asking
    /// again while the list is incomplete), <c>.</c> (member list) and <c>ReadTemperature();</c>.
    /// </summary>
    public static async Task<SequentialResult> RunSequentialAsync(IScriptService service, bool firstWasMember)
    {
        var member = new List<double>();
        var word = new List<double>();
        var refine = new List<double>();
        Outcome? lastMember = null;
        Outcome? lastWord = null;
        await TypeAsync(firstWasMember ? "ReadTemperature();" : "evice.ReadTemperature();");
        for (int round = 0; round < Rounds; round++)
        {
            bool measured = round >= SkippedRounds;
            await TypeAsync("\nD");
            lastWord = (await service.Complete(MainDocument, CompletionKind.Word).Result).WithResult("A word list");
            if (measured)
                word.Add(lastWord.Ms);
            bool incomplete = lastWord.Incomplete;
            foreach (char letter in "evice")
            {
                await TypeAsync(letter.ToString());
                if (!incomplete)
                    continue;
                Outcome again = (await service.Complete(MainDocument, CompletionKind.Refine).Result).WithResult(
                    "A word list asked for again"
                );
                incomplete = again.Incomplete;
                if (measured)
                    refine.Add(again.Ms);
            }
            await TypeAsync(".");
            lastMember = (await service.Complete(MainDocument, CompletionKind.Member).Result).WithResult(
                "A member list"
            );
            if (measured)
                member.Add(lastMember.Ms);
            await TypeAsync("ReadTemperature();");
        }
        return new SequentialResult(
            member,
            word,
            refine,
            lastMember ?? throw new InvalidOperationException("No rounds."),
            lastWord ?? throw new InvalidOperationException("No rounds.")
        );

        async Task TypeAsync(string typed)
        {
            await Task.Delay(Delay);
            await service.TypeAsync(MainDocument, typed);
        }
    }

    /// <summary>
    /// Types at a fixed cadence without waiting for answers, as an editor does: a keystroke every
    /// <paramref name="cadence"/> in the main document and every <paramref name="secondEvery"/>th in the second one.
    /// Each keystroke asks for semantic tokens; word starts, dots and letters of incomplete lists ask for completion;
    /// "(" asks for signature help. A newer request of a kind cancels the older one of the same document.
    /// </summary>
    public static async Task<OverlapResult> RunOverlapAsync(IScriptService service, TimeSpan cadence, int secondEvery)
    {
        var typists = new Dictionary<string, Typist> { [MainDocument] = new(), [SecondDocument] = new() };
        var tracked = new List<(string Kind, Request Request)>();

        long start = Stopwatch.GetTimestamp();
        for (int tick = 0; tick < OverlapKeystrokes; tick++)
        {
            TimeSpan wait = TimeSpan.FromTicks(cadence.Ticks * tick) - Stopwatch.GetElapsedTime(start);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
            await KeystrokeAsync(MainDocument);
            if (tick % secondEvery == 0)
                await KeystrokeAsync(SecondDocument);
        }
        long burstEnd = Stopwatch.GetTimestamp();
        // End the statement the typist is in, then start a word on a new line.
        await service.TypeAsync(MainDocument, ";\nD");
        Outcome recovery = (await service.Complete(MainDocument, CompletionKind.Word).Result).WithResult(
            "The word list after typing"
        );
        double recoveryMs = Stopwatch.GetElapsedTime(burstEnd).TotalMilliseconds;
        await Task.WhenAll(tracked.Select(t => t.Request.Result));
        double drain = Stopwatch.GetElapsedTime(burstEnd).TotalMilliseconds;

        List<(string Kind, Outcome Outcome, bool Superseded)> outcomes =
        [
            .. tracked.Select(t => (t.Kind, t.Request.Result.Result, t.Request.Superseded)),
        ];
        return new OverlapResult(
            [.. Fresh("completion")],
            [.. Fresh("tokens")],
            outcomes.Count,
            AnswerCounts.Of(outcomes.Where(o => !o.Superseded).Select(o => o.Outcome.Answer)),
            AnswerCounts.Of(outcomes.Where(o => o.Superseded).Select(o => o.Outcome.Answer)),
            recoveryMs,
            recovery.Items,
            drain
        );

        IEnumerable<double> Fresh(string kind) =>
            outcomes
                .Where(o => o.Kind == kind && !o.Superseded && o.Outcome.Answer == Answer.Result)
                .Select(o => o.Outcome.Ms);

        async Task KeystrokeAsync(string document)
        {
            Typist typist = typists[document];
            char previous = Statement[(typist.Index + Statement.Length - 1) % Statement.Length];
            char typed = Statement[typist.Index++ % Statement.Length];
            await service.TypeAsync(document, typed.ToString());

            CompletionKind? completion =
                typed == 'D' && previous == '\n' ? CompletionKind.Word
                : typed == '.' ? CompletionKind.Member
                : char.IsLetter(typed) && typist.ListIncomplete ? CompletionKind.Refine
                : null;
            if (!char.IsLetter(typed))
                typist.ListIncomplete = false;
            if (completion is { } kind)
            {
                Request request = Start(typist, "completion", () => service.Complete(document, kind));
                _ = request.Result.ContinueWith(
                    t =>
                    {
                        // The newest list decides whether letters ask again.
                        if (typist.Latest("completion") == request)
                            typist.ListIncomplete = t.Result.Incomplete;
                    },
                    TaskScheduler.Default
                );
            }
            if (typed == '(')
                Start(typist, "signature", () => service.SignatureHelp(document));
            Start(typist, "tokens", () => service.Tokens(document));
        }

        Request Start(Typist typist, string kind, Func<Request> send)
        {
            if (typist.Latest(kind) is { } older && !older.Result.IsCompleted)
                _ = older.CancelAsync();
            Request request = send();
            typist.Requests[kind] = request;
            tracked.Add((kind, request));
            return request;
        }
    }

    private sealed class Typist
    {
        public int Index { get; set; }

        public bool ListIncomplete { get; set; }

        public Dictionary<string, Request> Requests { get; } = [];

        public Request? Latest(string kind) => Requests.GetValueOrDefault(kind);
    }
}

internal sealed record SequentialResult(
    List<double> Member,
    List<double> Word,
    List<double> Refine,
    Outcome LastMember,
    Outcome LastWord
);

internal static class ServerRun
{
    public static async Task<RunResult> MeasureAsync(Variant variant, bool member)
    {
        await using LspClient client = LspClient.Start(variant.Published.ServerDll, variant.ServerArguments);
        await client.RequestAsync(
            "initialize",
            new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = null,
                ["capabilities"] = LspScriptService.Capabilities(),
                ["initializationOptions"] = CSharpScriptInitializationOptions.Create(Benchmark.Environment),
            }
        );
        double initializeMs = Stopwatch.GetElapsedTime(client.StartTimestamp).TotalMilliseconds;
        await client.NotifyAsync("initialized", new JsonObject());

        var service = new LspScriptService(client);
        await service.OpenAsync(Typing.MainDocument, Typing.Initial(member));
        Outcome first = await service
            .Complete(Typing.MainDocument, member ? CompletionKind.Member : CompletionKind.Word)
            .Result;
        double firstMs = Stopwatch.GetElapsedTime(client.StartTimestamp).TotalMilliseconds;
        if (first.Items == 0)
            throw new InvalidOperationException($"No first completion list.\n{client.StandardError}");

        SequentialResult sequential = await Typing.RunSequentialAsync(service, member);
        await service.OpenAsync(Typing.SecondDocument, SampleDocuments.CSharpScript);
        OverlapResult overlap = await Typing.RunOverlapAsync(service, Typing.Delay, secondEvery: 3);
        OverlapResult stress = await Typing.RunOverlapAsync(service, Typing.StressDelay, secondEvery: 1);

        await Task.Delay(TimeSpan.FromSeconds(1));
        client.Process.Refresh();
        double rss = client.Process.WorkingSet64 / 1048576.0;
        double peak = client.Process.PeakWorkingSet64 / 1048576.0;
        await client.ShutdownAsync();
        return new RunResult(
            variant.Name,
            member,
            firstMs,
            first.Items,
            initializeMs,
            sequential.Member,
            sequential.Word,
            sequential.Refine,
            sequential.LastMember.Items,
            sequential.LastWord.Items,
            sequential.LastMember.Bytes,
            sequential.LastWord.Bytes,
            overlap,
            stress,
            rss,
            peak
        );
    }
}

/// <summary>The server over LSP, with what VS Code's client declares for the features used.</summary>
internal sealed class LspScriptService : IScriptService
{
    private readonly LspClient _client;
    private readonly Dictionary<string, (string Text, int Version)> _documents = [];

    public LspScriptService(LspClient client) => _client = client;

    public static JsonNode Capabilities() =>
        JsonNode.Parse(
            """
            {
              "textDocument": {
                "completion": {
                  "completionItem": {
                    "snippetSupport": true,
                    "commitCharactersSupport": true,
                    "documentationFormat": ["markdown", "plaintext"],
                    "labelDetailsSupport": true,
                    "resolveSupport": { "properties": ["documentation", "detail", "additionalTextEdits"] }
                  },
                  "completionList": { "itemDefaults": ["commitCharacters", "editRange", "insertTextFormat", "data"] }
                },
                "hover": { "contentFormat": ["markdown", "plaintext"] }
              }
            }
            """
        ) ?? throw new InvalidOperationException();

    public Task OpenAsync(string document, string text)
    {
        _documents[document] = (text, 1);
        return _client.NotifyAsync(
            "textDocument/didOpen",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = Uri(document),
                    ["languageId"] = "csharp",
                    ["version"] = 1,
                    ["text"] = text,
                },
            }
        );
    }

    public Task TypeAsync(string document, string typed)
    {
        (string text, int version) = _documents[document];
        JsonObject end = Position(text, text.Length);
        _documents[document] = (text + typed, version + 1);
        return _client.NotifyAsync(
            "textDocument/didChange",
            new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = Uri(document), ["version"] = version + 1 },
                ["contentChanges"] = new JsonArray(
                    new JsonObject
                    {
                        ["range"] = new JsonObject { ["start"] = end, ["end"] = end.DeepClone() },
                        ["text"] = typed,
                    }
                ),
            }
        );
    }

    public Request Complete(string document, CompletionKind kind) =>
        Send(
            "textDocument/completion",
            document,
            kind switch
            {
                CompletionKind.Word => new JsonObject { ["triggerKind"] = 1 },
                CompletionKind.Member => new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = "." },
                _ => new JsonObject { ["triggerKind"] = 3 },
            }
        );

    public Request Tokens(string document)
    {
        string text = _documents[document].Text;
        int line = text.AsSpan().Count('\n');
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(document) },
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = Math.Max(0, line - 40), ["character"] = 0 },
                ["end"] = Position(text, text.Length),
            },
        };
        return Track("textDocument/semanticTokens/range", parameters);
    }

    public Request SignatureHelp(string document) => Send("textDocument/signatureHelp", document, null);

    private Request Send(string method, string document, JsonObject? context)
    {
        string text = _documents[document].Text;
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri(document) },
            ["position"] = Position(text, text.Length),
        };
        if (context is not null)
            parameters["context"] = context;
        return Track(method, parameters);
    }

    private Request Track(string method, JsonObject parameters)
    {
        long start = Stopwatch.GetTimestamp();
        PendingRequest pending = _client.Send(method, parameters);
        return new Request(
            pending.Response.ContinueWith(
                t =>
                {
                    LspResponse response = t.Result;
                    JsonArray? items = response.Result?["items"]?.AsArray();
                    return new Outcome(
                        response.Error?.Code switch
                        {
                            null => Answer.Result,
                            -32800 => Answer.Cancelled,
                            -32801 => Answer.ContentModified,
                            _ => Answer.Failed,
                        },
                        items?.Count ?? 0,
                        response.Result?["isIncomplete"]?.GetValue<bool>() ?? false,
                        response.ContentLength,
                        Stopwatch.GetElapsedTime(start).TotalMilliseconds
                    );
                },
                TaskScheduler.Default
            ),
            () => _client.CancelAsync(pending.Id)
        );
    }

    private static string Uri(string document) => $"file:///benchmark/{document}.csx";

    private static JsonObject Position(string text, int offset)
    {
        int line = text.AsSpan(0, offset).Count('\n');
        int character = offset - (text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1);
        return new JsonObject { ["line"] = line, ["character"] = character };
    }
}

/// <summary>The prototype's in-process service; cancelling a request cancels its token.</summary>
internal sealed class InProcessScriptService : IScriptService
{
    private readonly CSharpScriptLanguageService _service;
    private readonly Dictionary<string, (string Text, int Version)> _documents = [];

    public InProcessScriptService(CSharpScriptLanguageService service) => _service = service;

    public Task OpenAsync(string document, string text)
    {
        _documents[document] = (text, 1);
        return Task.CompletedTask;
    }

    public Task TypeAsync(string document, string typed)
    {
        (string text, int version) = _documents[document];
        _documents[document] = (text + typed, version + 1);
        return Task.CompletedTask;
    }

    public Request Complete(string document, CompletionKind kind) =>
        Run(
            document,
            async (snapshot, token) =>
            {
                CompletionList? list = await _service.GetCompletionsAsync(
                    snapshot,
                    snapshot.Text.Length,
                    kind switch
                    {
                        // The editor asks on the first letter of a word.
                        CompletionKind.Word => new CompletionTrigger(CompletionTriggerKind.Insertion, 'D'),
                        CompletionKind.Member => new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.'),
                        _ => new CompletionTrigger(CompletionTriggerKind.Invoke),
                    },
                    token
                );
                return list?.Items.Count ?? 0;
            }
        );

    public Request Tokens(string document) =>
        Run(
            document,
            async (snapshot, token) =>
            {
                int end = snapshot.Text.Length;
                int start = end;
                for (int lines = 0; lines < 40 && start > 0; start--)
                {
                    if (snapshot.Text[start - 1] == '\n')
                        lines++;
                }
                return (
                    await _service.GetClassificationsAsync(snapshot, TextRange.FromBounds(start, end), token)
                ).Count;
            }
        );

    public Request SignatureHelp(string document) =>
        Run(
            document,
            async (snapshot, token) =>
                (await _service.GetSignatureHelpAsync(snapshot, snapshot.Text.Length, token))?.Signatures.Count ?? 0
        );

    private Request Run(string document, Func<DocumentSnapshot, CancellationToken, Task<int>> request)
    {
        (string text, int version) = _documents[document];
        var snapshot = new DocumentSnapshot(document, version, text);
        var cancellation = new CancellationTokenSource();
        long start = Stopwatch.GetTimestamp();
        Task<Outcome> result = Task.Run(async () =>
        {
            try
            {
                int items = await request(snapshot, cancellation.Token);
                return new Outcome(Answer.Result, items, false, 0, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                return new Outcome(Answer.Cancelled, 0, false, 0, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        });
        return new Request(
            result,
            () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }
        );
    }
}

/// <summary>The in-process service, measured in a child process the same way as the server.</summary>
internal static class InProcess
{
    public static async Task<RunResult> RunChildAsync(bool member)
    {
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(typeof(InProcess).Assembly.Location);
        startInfo.ArgumentList.Add("--measure-in-process");
        startInfo.ArgumentList.Add(member ? "member" : "word");
        long start = Stopwatch.GetTimestamp();
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("No child process.");
        // The child writes one line at its first completion list, then its results.
        string first =
            await process.StandardOutput.ReadLineAsync() ?? throw new InvalidOperationException("No output.");
        double firstMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        string json = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        RunResult result = JsonSerializer.Deserialize<RunResult>(json) ?? throw new InvalidOperationException(json);
        return result with { FirstMs = firstMs, FirstItems = int.Parse(first, CultureInfo.InvariantCulture) };
    }

    public static async Task MeasureAsync(bool member)
    {
        using CSharpScriptLanguageService languageService = await CSharpScriptLanguageService.CreateAsync(
            new CSharpScriptOptions(
                typeof(ScriptGlobals),
                [typeof(Enumerable).Assembly, typeof(List<>).Assembly, typeof(StringBuilder).Assembly],
                ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"]
            )
        );
        var service = new InProcessScriptService(languageService);
        await service.OpenAsync(Typing.MainDocument, Typing.Initial(member));
        Outcome first = await service
            .Complete(Typing.MainDocument, member ? CompletionKind.Member : CompletionKind.Word)
            .Result;
        Console.WriteLine(first.Items.ToString(CultureInfo.InvariantCulture));
        Console.Out.Flush();

        SequentialResult sequential = await Typing.RunSequentialAsync(service, member);
        await service.OpenAsync(Typing.SecondDocument, SampleDocuments.CSharpScript);
        OverlapResult overlap = await Typing.RunOverlapAsync(service, Typing.Delay, secondEvery: 3);
        OverlapResult stress = await Typing.RunOverlapAsync(service, Typing.StressDelay, secondEvery: 1);

        await Task.Delay(TimeSpan.FromSeconds(1));
        using var self = Process.GetCurrentProcess();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new RunResult(
                    "in-process",
                    member,
                    0,
                    0,
                    0,
                    sequential.Member,
                    sequential.Word,
                    sequential.Refine,
                    sequential.LastMember.Items,
                    sequential.LastWord.Items,
                    0,
                    0,
                    overlap,
                    stress,
                    self.WorkingSet64 / 1048576.0,
                    self.PeakWorkingSet64 / 1048576.0
                )
            )
        );
    }
}

internal static class Benchmark
{
    /// <summary>The demo's script environment, as the host would describe it to the server.</summary>
    public static CSharpScriptEnvironment Environment { get; } =
        new()
        {
            Globals = new CSharpScriptGlobals(
                typeof(ScriptGlobals).Assembly.Location,
                typeof(ScriptGlobals).FullName ?? ""
            ),
            // The demo references the assemblies of Enumerable, List<T> and StringBuilder; the latter two are the core library.
            FrameworkReferences = ["System.Linq"],
            Imports = ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"],
        };

    /// <param name="Directory">The publish directory.</param>
    /// <param name="Bytes">The size of all published files.</param>
    /// <param name="ZipBytes">The size of the directory as a zip archive, a download estimate.</param>
    public sealed record Published(string Directory, string ServerDll, long Bytes, long ZipBytes);

    public static string FindRepository()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeEditor.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Run the benchmark from the repository's build output.");
    }

    /// <summary>The 1, 5 and 15 minute load averages, where the OS reports them.</summary>
    public static string LoadAverage() =>
        File.Exists("/proc/loadavg")
            ? string.Join(" ", File.ReadAllText("/proc/loadavg").Split(' ').Take(3))
            : "not available";

    public static Published Publish(string repository, string directory, string[] arguments)
    {
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (
            string argument in new[]
            {
                "publish",
                Path.Combine(repository, "src", "Darp.CodeEditor.CSharp.Server"),
                "-c",
                "Release",
                "-o",
                directory,
            }.Concat(arguments)
        )
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process =
            Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet publish did not start.");
        string log = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"dotnet publish failed:\n{log}");
        long bytes = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        string zip = directory + ".zip";
        File.Delete(zip);
        ZipFile.CreateFromDirectory(directory, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        return new Published(
            directory,
            Path.Combine(directory, "Darp.CodeEditor.CSharp.Server.dll"),
            bytes,
            new FileInfo(zip).Length
        );
    }
}

internal static class Report
{
    public static string Create(
        List<RunResult> results,
        Variant[] variants,
        int runs,
        string loadBefore,
        string loadAfter
    )
    {
        string[] columns = ["in-process", .. variants.Select(v => v.Name)];
        var report = new StringBuilder();
        report.AppendLine("# C# script language server benchmark");
        report.AppendLine();
        report.AppendLine(
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, "
                + $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores, load average "
                + $"{loadBefore} before and {loadAfter} after; {runs} cold starts per cell; one request at a time: "
                + $"{Typing.Rounds - Typing.SkippedRounds} rounds per run after {Typing.SkippedRounds} skipped; typing and "
                + $"stress: {Typing.OverlapKeystrokes} keystrokes every {Typing.Delay.TotalMilliseconds:F0} ms, with every third "
                + $"also in a second document, then every {Typing.StressDelay.TotalMilliseconds:F0} ms in both, per run."
        );
        report.AppendLine();
        report.AppendLine(
            "By default the server cuts lists of more than 1,000 items to the 1,000 that match the typed text best, "
                + "and marks them incomplete, so the client asks again for each letter. \"Unfiltered\" sends every item; "
                + "\"filtered, no max\" sends every matching item. In-process, the service warms up while it is created, "
                + "before the first request."
        );
        report.AppendLine();
        report.AppendLine($"| | {string.Join(" | ", columns)} |");
        report.AppendLine($"|---|{string.Concat(columns.Select(_ => "---:|"))}");
        Row("process start → first member list, median (min–max)", c => Cold(c, member: true));
        Row("process start → first word list, median (min–max)", c => Cold(c, member: false));
        Row("items: member / word list", c => $"{Pick(c).Max(r => r.MemberItems)} / {Pick(c).Max(r => r.WordItems)}");
        Row("one at a time: member list p50 / p95", c => Times(Pick(c).SelectMany(r => r.WarmMemberMs)));
        Row("one at a time: word list p50 / p95", c => Times(Pick(c).SelectMany(r => r.WarmWordMs)));
        Row(
            "one at a time: word list asked again per letter p50 / p95 (count)",
            c => Times(Pick(c).SelectMany(r => r.WarmRefineMs), count: true)
        );
        OverlapRows("typing", r => r.Overlap);
        OverlapRows("stress", r => r.Stress);
        Row(
            "RSS after typing, median (peak)",
            c => $"{Median(Pick(c).Select(r => r.RssMiB)):F0} MiB ({Median(Pick(c).Select(r => r.PeakRssMiB)):F0})"
        );
        Row(
            "response size: member / word list",
            c =>
                c == "in-process"
                    ? "–"
                    : $"{Pick(c).Max(r => r.MemberBytes) / 1024.0:F1} / {Pick(c).Max(r => r.WordBytes) / 1024.0:F0} KiB"
        );
        Row(
            "published files / zipped",
            c =>
                variants.FirstOrDefault(v => v.Name == c) is { } v
                    ? $"{v.Published.Bytes / 1048576.0:F1} / {v.Published.ZipBytes / 1048576.0:F1} MiB"
                    : "–"
        );
        Row(
            "initialize answered after (median)",
            c => c == "in-process" ? "–" : $"{Median(Pick(c).Select(r => r.InitializeMs)):F0} ms"
        );
        return report.ToString();

        void Row(string name, Func<string, string> cell) =>
            report.AppendLine($"| {name} | {string.Join(" | ", columns.Select(cell))} |");

        void OverlapRows(string phase, Func<RunResult, OverlapResult> select)
        {
            Row(
                $"{phase}: fresh completion p50 / p95",
                c => Times(Pick(c).SelectMany(r => select(r).FreshCompletionMs))
            );
            Row(
                $"{phase}: fresh semantic tokens p50 / p95",
                c => Times(Pick(c).SelectMany(r => select(r).FreshTokensMs))
            );
            Row(
                $"{phase}: requests the client kept, answered with result / RequestCancelled / ContentModified / other error",
                c => Pick(c).Select(r => select(r).Kept).Aggregate(AnswerCounts.Zero, (a, b) => a + b).ToString()
            );
            Row(
                $"{phase}: requests the client cancelled, answered with result / RequestCancelled / ContentModified / other error",
                c => Pick(c).Select(r => select(r).Superseded).Aggregate(AnswerCounts.Zero, (a, b) => a + b).ToString()
            );
            Row(
                $"{phase}: last keystroke → fresh word list answered, median (max); items",
                c =>
                    $"{Median(Pick(c).Select(r => select(r).RecoveryMs)):F0} ms ({Pick(c).Max(r => select(r).RecoveryMs):F0}); {Pick(c).Min(r => select(r).RecoveryItems)}"
            );
            Row(
                $"{phase}: last keystroke → every request answered, median (max)",
                c =>
                    $"{Median(Pick(c).Select(r => select(r).DrainMs)):F0} ms ({Pick(c).Max(r => select(r).DrainMs):F0})"
            );
        }

        IEnumerable<RunResult> Pick(string column) => results.Where(r => r.Name == column);

        string Cold(string column, bool member)
        {
            double[] times = [.. Pick(column).Where(r => r.FirstIsMember == member).Select(r => r.FirstMs)];
            return $"{Median(times):F0} ms ({times.Min():F0}–{times.Max():F0})";
        }

        static string Times(IEnumerable<double> values, bool count = false)
        {
            double[] times = [.. values];
            if (times.Length == 0)
                return "–";
            string text = $"{Percentile(times, 0.5):F1} / {Percentile(times, 0.95):F1} ms";
            return count ? $"{text} ({times.Length})" : text;
        }
    }

    private static double Median(IEnumerable<double> values) => Percentile(values, 0.5);

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length == 0
            ? double.NaN
            : sorted[(int)Math.Min(sorted.Length - 1, Math.Ceiling(percentile * sorted.Length) - 1)];
    }
}
