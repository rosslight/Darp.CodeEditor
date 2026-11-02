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
// process start to first completion, warm completion latency while typing, memory and payload sizes.
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

Console.WriteLine("Publishing the server...");
string rid = RuntimeInformation.RuntimeIdentifier;
Variant[] variants =
[
    new("server", Benchmark.Publish(repository, Path.Combine(output, "publish-fdd"), []), []),
    new(
        "server R2R",
        Benchmark.Publish(
            repository,
            Path.Combine(output, "publish-r2r"),
            ["-r", rid, "--self-contained", "false", "-p:PublishReadyToRun=true"]
        ),
        []
    ),
];
variants =
[
    .. variants,
    variants[1] with
    {
        Name = "server R2R, no warm-up",
        ServerArguments = ["--no-warm-up"],
    },
    // An editor usually sits idle for a while after opening a script; warm-up runs in that time.
    variants[1] with
    {
        Name = "server R2R, 2 s idle",
        Idle = TimeSpan.FromSeconds(2),
    },
    variants[1] with
    {
        Name = "server R2R, no warm-up, 2 s idle",
        ServerArguments = ["--no-warm-up"],
        Idle = TimeSpan.FromSeconds(2),
    },
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

string report = Report.Create(results, variants, runs);
await File.WriteAllTextAsync(Path.Combine(output, "report.md"), report);
await File.WriteAllTextAsync(
    Path.Combine(output, "results.json"),
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true })
);
Console.WriteLine(report);
return 0;

/// <summary>A published server and the arguments it runs with.</summary>
/// <param name="Idle">How long to wait after opening the script before the first completion request.</param>
internal sealed record Variant(string Name, Benchmark.Published Published, string[] ServerArguments)
{
    public TimeSpan Idle { get; init; }
}

/// <param name="FirstMs">Process start to the first non-empty completion list.</param>
/// <param name="FirstRequestMs">The first completion request alone: sent to parsed.</param>
/// <param name="WarmMs">Request to response, parsed, while typing: completion lists after the first two rounds.</param>
/// <param name="WarmTransportMs">Request to the last byte of the response, before parsing (server only).</param>
internal sealed record RunResult(
    string Name,
    bool FirstIsMember,
    double FirstMs,
    double FirstRequestMs,
    int FirstItems,
    double InitializeMs,
    List<double> WarmMemberMs,
    List<double> WarmWordMs,
    List<double> WarmMemberTransportMs,
    List<double> WarmWordTransportMs,
    int MemberItems,
    int WordItems,
    int MemberBytes,
    int WordBytes,
    double RssMiB,
    double PeakRssMiB
);

/// <summary>What the benchmark types after the first completion, in chunks, 40 ms apart.</summary>
internal static class Typing
{
    public const int Rounds = 20;
    public const int SkippedRounds = 2;
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(40);

    /// <summary>The script with a line that ends where the first completion is requested.</summary>
    public static string Initial(bool member) => SampleDocuments.CSharpScript + (member ? "\nDevice." : "\nD");

    /// <summary>Completes the statement the first completion was requested in.</summary>
    public static string Finish(bool member) => member ? "ReadTemperature();" : "evice.ReadTemperature();";
}

internal static class ServerRun
{
    private const string Uri = "file:///benchmark/script.csx";

    public static async Task<RunResult> MeasureAsync(Variant variant, bool member)
    {
        await using LspClient client = LspClient.Start(variant.Published.ServerDll, variant.ServerArguments);
        await client.RequestAsync(
            "initialize",
            new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["rootUri"] = null,
                ["capabilities"] = Capabilities(),
                ["initializationOptions"] = CSharpScriptInitializationOptions.Create(Benchmark.Environment),
            }
        );
        double initializeMs = Stopwatch.GetElapsedTime(client.StartTimestamp).TotalMilliseconds;
        await client.NotifyAsync("initialized", new JsonObject());

        string text = Typing.Initial(member);
        int version = 1;
        await client.NotifyAsync(
            "textDocument/didOpen",
            new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = Uri,
                    ["languageId"] = "csharp",
                    ["version"] = version,
                    ["text"] = text,
                },
            }
        );
        await Task.Delay(variant.Idle);
        (LspResponse first, double firstRequestMs, _) = await CompleteAsync(client, text, member);
        double firstMs = Stopwatch.GetElapsedTime(client.StartTimestamp).TotalMilliseconds;
        int firstItems = first.Result?["items"]?.AsArray().Count ?? 0;

        var memberMs = new List<double>();
        var wordMs = new List<double>();
        var memberTransportMs = new List<double>();
        var wordTransportMs = new List<double>();
        LspResponse? lastMember = null;
        LspResponse? lastWord = null;
        async Task TypeAsync(string typed)
        {
            await Task.Delay(Typing.Delay);
            JsonObject end = Position(text, text.Length);
            text += typed;
            version++;
            await client.NotifyAsync(
                "textDocument/didChange",
                new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = version },
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

        await TypeAsync(Typing.Finish(member));
        for (int round = 0; round < Typing.Rounds; round++)
        {
            await TypeAsync("\nD");
            (lastWord, double wordTotal, double wordTransport) = await CompleteAsync(client, text, member: false);
            await TypeAsync("evice");
            await TypeAsync(".");
            (lastMember, double memberTotal, double memberTransport) = await CompleteAsync(client, text, member: true);
            await TypeAsync("ReadTemperature();");
            if (round < Typing.SkippedRounds)
                continue;
            wordMs.Add(wordTotal);
            memberMs.Add(memberTotal);
            wordTransportMs.Add(wordTransport);
            memberTransportMs.Add(memberTransport);
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
        client.Process.Refresh();
        double rss = client.Process.WorkingSet64 / 1048576.0;
        double peak = client.Process.PeakWorkingSet64 / 1048576.0;
        await client.ShutdownAsync();
        return new RunResult(
            variant.Name,
            member,
            firstMs,
            firstRequestMs,
            firstItems,
            initializeMs,
            memberMs,
            wordMs,
            memberTransportMs,
            wordTransportMs,
            lastMember?.Result?["items"]?.AsArray().Count ?? 0,
            lastWord?.Result?["items"]?.AsArray().Count ?? 0,
            lastMember?.ContentLength ?? 0,
            lastWord?.ContentLength ?? 0,
            rss,
            peak
        );
    }

    /// <summary>Requests completion at the end of <paramref name="text"/>: after "." for members, else invoked.</summary>
    /// <returns>
    /// The response, the time from sending the request to having parsed the response, and the time to having read it.
    /// </returns>
    private static async Task<(LspResponse Response, double TotalMs, double TransportMs)> CompleteAsync(
        LspClient client,
        string text,
        bool member
    )
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri },
            ["position"] = Position(text, text.Length),
            ["context"] = member
                ? new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = "." }
                : new JsonObject { ["triggerKind"] = 1 },
        };
        long start = Stopwatch.GetTimestamp();
        LspResponse response = await client.Send("textDocument/completion", parameters).Response;
        double total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (response.Result?["items"]?.AsArray().Count is null or 0)
            throw new InvalidOperationException(
                $"No completion list: {response.Error?.Message}\n{client.StandardError}"
            );
        return (response, total, Stopwatch.GetElapsedTime(start, response.ReceivedTimestamp).TotalMilliseconds);
    }

    private static JsonObject Position(string text, int offset)
    {
        int line = text.AsSpan(0, offset).Count('\n');
        int character = offset - (text.LastIndexOf('\n', offset - 1) + 1);
        return new JsonObject { ["line"] = line, ["character"] = character };
    }

    /// <summary>What VS Code's client declares for the features the server uses.</summary>
    private static JsonNode Capabilities() =>
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
}

/// <summary>The prototype's in-process service, measured in a child process the same way as the server.</summary>
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
        string[] parts = first.Split(' ');
        return result with
        {
            FirstMs = firstMs,
            FirstItems = int.Parse(parts[0], CultureInfo.InvariantCulture),
            FirstRequestMs = double.Parse(parts[1], CultureInfo.InvariantCulture),
        };
    }

    public static async Task MeasureAsync(bool member)
    {
        using CSharpScriptLanguageService service = await CSharpScriptLanguageService.CreateAsync(
            new CSharpScriptOptions(
                typeof(ScriptGlobals),
                [typeof(Enumerable).Assembly, typeof(List<>).Assembly, typeof(StringBuilder).Assembly],
                ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"]
            )
        );
        string text = Typing.Initial(member);
        int version = 1;
        long firstStart = Stopwatch.GetTimestamp();
        CompletionList first = await CompleteAsync(member);
        // The service warmed up while it was created, so this is the request after warm-up.
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{first.Items.Count} {Stopwatch.GetElapsedTime(firstStart).TotalMilliseconds:F1}"
            )
        );
        Console.Out.Flush();

        var memberMs = new List<double>();
        var wordMs = new List<double>();
        CompletionList? lastMember = null;
        CompletionList? lastWord = null;
        async Task TypeAsync(string typed)
        {
            await Task.Delay(Typing.Delay);
            text += typed;
            version++;
        }
        async Task<CompletionList> CompleteAsync(bool isMember) =>
            await service.GetCompletionsAsync(
                new DocumentSnapshot("benchmark", version, text),
                text.Length,
                isMember
                    ? new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.')
                    : new CompletionTrigger(CompletionTriggerKind.Insertion, 'D'),
                CancellationToken.None
            ) ?? throw new InvalidOperationException("No completion list.");

        await TypeAsync(Typing.Finish(member));
        for (int round = 0; round < Typing.Rounds; round++)
        {
            await TypeAsync("\nD");
            long start = Stopwatch.GetTimestamp();
            lastWord = await CompleteAsync(isMember: false);
            double word = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await TypeAsync("evice");
            await TypeAsync(".");
            start = Stopwatch.GetTimestamp();
            lastMember = await CompleteAsync(isMember: true);
            double memberTime = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            await TypeAsync("ReadTemperature();");
            if (round < Typing.SkippedRounds)
                continue;
            wordMs.Add(word);
            memberMs.Add(memberTime);
        }

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
                    0,
                    memberMs,
                    wordMs,
                    [],
                    [],
                    lastMember?.Items.Count ?? 0,
                    lastWord?.Items.Count ?? 0,
                    0,
                    0,
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
    public static string Create(List<RunResult> results, Variant[] variants, int runs)
    {
        string[] columns = ["in-process", .. variants.Select(v => v.Name)];
        var report = new StringBuilder();
        report.AppendLine("# C# script language server benchmark");
        report.AppendLine();
        report.AppendLine(
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription}, "
                + $"{RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical cores; {runs} cold starts per cell; "
                + $"warm: {Typing.Rounds - Typing.SkippedRounds} rounds per run after {Typing.SkippedRounds} skipped."
        );
        report.AppendLine();
        report.AppendLine(
            "Process start → first list includes the 2 s idle in the idle columns. In-process, the service warms up "
                + "while it is created, before the first request."
        );
        report.AppendLine();
        report.AppendLine($"| | {string.Join(" | ", columns)} |");
        report.AppendLine($"|---|{string.Concat(columns.Select(_ => "---:|"))}");
        Row("process start → first member list, median (min–max)", c => Cold(c, member: true));
        Row("process start → first word list, median (min–max)", c => Cold(c, member: false));
        Row(
            "first request alone (sent → parsed): member / word, median",
            c =>
                $"{Median(Pick(c).Where(r => r.FirstIsMember).Select(r => r.FirstRequestMs)):F0} / {Median(Pick(c).Where(r => !r.FirstIsMember).Select(r => r.FirstRequestMs)):F0} ms"
        );
        Row("items: member / word list", c => $"{Pick(c).Max(r => r.MemberItems)} / {Pick(c).Max(r => r.WordItems)}");
        Row("warm member list p50 / p95", c => Warm(c, r => r.WarmMemberMs));
        Row("warm word list p50 / p95", c => Warm(c, r => r.WarmWordMs));
        Row(
            "warm p50 without client JSON parsing: member / word",
            c =>
                c == "in-process"
                    ? "–"
                    : $"{Percentile(Pick(c).SelectMany(r => r.WarmMemberTransportMs), 0.5):F1} / {Percentile(Pick(c).SelectMany(r => r.WarmWordTransportMs), 0.5):F1} ms"
        );
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

        IEnumerable<RunResult> Pick(string column) => results.Where(r => r.Name == column);

        string Cold(string column, bool member)
        {
            double[] times = [.. Pick(column).Where(r => r.FirstIsMember == member).Select(r => r.FirstMs)];
            return $"{Median(times):F0} ms ({times.Min():F0}–{times.Max():F0})";
        }

        string Warm(string column, Func<RunResult, List<double>> select)
        {
            double[] times = [.. Pick(column).SelectMany(select)];
            return $"{Percentile(times, 0.5):F1} / {Percentile(times, 0.95):F1} ms";
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
