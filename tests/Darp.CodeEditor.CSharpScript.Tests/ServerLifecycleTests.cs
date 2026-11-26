using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Darp.CodeEditor.CSharpScript.Protocol;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;
using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

public sealed class ServerLifecycleTests
{
    private static string[] Diagnosed(IEnumerable<JsonElement> messages) =>
        [
            .. messages
                .Where(m =>
                    m.TryGetProperty("method", out JsonElement method)
                    && method.ValueEquals("textDocument/publishDiagnostics")
                )
                .Select(m => m.GetProperty("params").GetProperty("version").GetRawText()),
        ];

    [Fact]
    public async Task A_restart_reopens_documents_in_their_environment()
    {
        Assert.SkipUnless(ScriptServerFixture.HasServer, "This app has no C# script server next to it.");
        await using var server = new CSharpScriptServer();
        await server.StartAsync(TestContext.Current.CancellationToken);
        using var script = await Script<ScriptTestGlobals>.OpenAsync(
            server,
            new CSharpScriptEnvironment<ScriptTestGlobals> { Imports = ["ScriptTests.Api"] },
            $"var api = ApiType.Value;\n{Script<ScriptTestGlobals>.EndMarker}"
        );
        (await script.DiagnosticsAsync()).ShouldBeEmpty();
        int first = server.ProcessId.ShouldNotBeNull();

        Process.GetProcessById(first).Kill();
        await Wait.UntilAsync(() => server.ProcessId is { } id && id != first && script.Document.Features is not null);

        await Wait.UntilAsync(() => script.Document.Diagnostics.Count > 0);
        (await script.DiagnosticsAsync()).ShouldBeEmpty();
        CompletionList list = await script.CompleteAsync();
        list.Items.ShouldContain(item => item.Label == "ApiType");
    }

    [Fact]
    public async Task Only_the_latest_version_publishes_diagnostics()
    {
        await using var server = RawServer.Start();
        await server.InitializeAsync();
        var messages = new List<string> { RawServer.Open("untitled:a", "int x = 0;") };
        for (int version = 1; version <= 4; version++)
            messages.Add(RawServer.Change("untitled:a", version, $"int x = {version};"));
        await server.WriteAsync(messages);

        List<JsonElement> read = await server.ReadUntilAsync(m =>
            m.TryGetProperty("method", out JsonElement method)
            && method.ValueEquals("textDocument/publishDiagnostics")
            && m.GetProperty("params").GetProperty("version").GetInt32() == 4
        );
        Diagnosed(read).ShouldBe(["4"]);
    }

    [Fact]
    public async Task A_cancelled_request_stops_and_the_server_goes_on()
    {
        await using var server = RawServer.Start();
        await server.InitializeAsync();
        string text = string.Concat(Enumerable.Repeat("int value = Add(1, 2);\n", 2000));
        await server.WriteAsync([
            RawServer.Open("untitled:a", text),
            RawServer.Request(10, "textDocument/completion", RawServer.Position("untitled:a", 1000, 0)),
            """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":10}}""",
        ]);
        JsonElement answer = (await server.ReadUntilAsync(m => IsAnswerTo(m, 10)))[^1];
        answer.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(JsonRpcErrorCodes.RequestCancelled);

        await server.WriteAsync([
            RawServer.Request(11, "textDocument/completion", RawServer.Position("untitled:a", 1000, 0)),
        ]);
        JsonElement next = (await server.ReadUntilAsync(m => IsAnswerTo(m, 11)))[^1];
        next.GetProperty("result").GetProperty("items").GetArrayLength().ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task A_cancelled_request_keeps_the_four_lists_before_it_resolving()
    {
        await using var server = RawServer.Start();
        await server.InitializeAsync();
        string request = RawServer.Position("untitled:a", 0, "var builder = new Str".Length);
        await server.WriteAsync([RawServer.Open("untitled:a", "var builder = new Str")]);
        // The item of the first of four lists that reached the client.
        string first = "";
        for (int id = 10; id < 14; id++)
        {
            await server.WriteAsync([RawServer.Request(id, "textDocument/completion", request)]);
            JsonElement items = (await server.ReadUntilAsync(m => IsAnswerTo(m, id)))[^1]
                .GetProperty("result")
                .GetProperty("items");
            if (id == 10)
                first = items
                    .EnumerateArray()
                    .Single(i => i.GetProperty("label").ValueEquals("StringBuilder"))
                    .GetRawText();
        }

        await server.WriteAsync([
            RawServer.Request(14, "textDocument/completion", request),
            """{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":14}}""",
        ]);
        await server.ReadUntilAsync(m => IsAnswerTo(m, 14));

        await server.WriteAsync([RawServer.Request(15, "completionItem/resolve", first)]);
        JsonElement resolved = (await server.ReadUntilAsync(m => IsAnswerTo(m, 15)))[^1].GetProperty("result");
        resolved.TryGetProperty("additionalTextEdits", out JsonElement edits).ShouldBeTrue();
        edits.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Without_the_folder_of_dotnet_docs_hover_answers_without_them()
    {
        string missing = Path.Combine(AppContext.BaseDirectory, "missing-dotnet-docs");
        await using var server = RawServer.Start(ScriptAppFiles.ServerCommand(frameworkDocs: missing));
        await server.InitializeAsync();
        await server.WriteAsync([
            RawServer.Open("untitled:a", "var text = string.Concat(\"a\", \"b\");"),
            RawServer.Request(2, "textDocument/hover", RawServer.Position("untitled:a", 0, 20)),
        ]);
        JsonElement answer = (await server.ReadUntilAsync(m => IsAnswerTo(m, 2)))[^1];
        string hover = answer.GetProperty("result").GetProperty("contents").GetProperty("value").GetString() ?? "";
        hover.ShouldContain("string.Concat(");
        hover.ShouldNotContain("Concatenates");
    }

    [Fact]
    public void The_server_runs_on_the_host_of_the_installed_runtime_only()
    {
        string root = Directory.CreateTempSubdirectory("script-host").FullName;
        try
        {
            string installed = Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0");
            string selfContained = Path.Combine(root, "apps", "tools", "app");
            Directory.CreateDirectory(installed);
            Directory.CreateDirectory(selfContained);
            string host = Path.Combine(root, "dotnet");
            File.WriteAllText(host, "");

            ScriptAppFiles.InstalledHost(installed + Path.DirectorySeparatorChar, "").ShouldBe(host);
            ScriptAppFiles.InstalledHost(selfContained + Path.DirectorySeparatorChar, "").ShouldBeNull();
            File.Delete(host);
            ScriptAppFiles.InstalledHost(installed + Path.DirectorySeparatorChar, "").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool IsAnswerTo(JsonElement message, int id) =>
        message.TryGetProperty("id", out JsonElement value) && value.GetInt32() == id;

    /// <summary>
    /// The real server with a client that writes messages in one go, so that the server reads them one right after the
    /// other: what an editor's fast typing is, without depending on how fast this machine answers.
    /// </summary>
    private sealed class RawServer : IAsyncDisposable
    {
        private readonly ServerProcess _process;
        private readonly PipeFrames _frames;

        private RawServer(ServerProcess process)
        {
            _process = process;
            _frames = new PipeFrames(process.Output);
        }

        public static RawServer Start() => Start(ScriptAppFiles.ServerCommand());

        public static RawServer Start((string FileName, string[] Arguments) command)
        {
            Assert.SkipUnless(ScriptServerFixture.HasServer, "This app has no C# script server next to it.");
            (string fileName, string[] arguments) = command;
            var startInfo = new ProcessStartInfo(fileName);
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return new RawServer(ServerProcess.Start(startInfo, standardError: null));
        }

        public async Task InitializeAsync()
        {
            await WriteAsync([Request(1, "initialize", "{}")]);
            await ReadUntilAsync(m => IsAnswerTo(m, 1));
        }

        public static string Open(string uri, string text)
        {
            ScriptEnvironmentData data = ScriptAppFiles
                .Read()
                .Describe(new CSharpScriptEnvironment<ScriptTestGlobals>());
            string environment = JsonSerializer.Serialize(data, ScriptProtocolJson.Default.ScriptEnvironmentData);
            string document =
                $"\"uri\":\"{uri}\",\"languageId\":\"csharp\",\"version\":0,\"text\":{Quoted(text)},\"data\":{environment}";
            return "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{"
                + document
                + "}}}";
        }

        public static string Change(string uri, int version, string text) =>
            $$$"""
                {"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"{{{uri}}}","version":{{{version}}}},"contentChanges":[{"text":{{{Quoted(
                    text
                )}}}}]}}
                """;

        public static string Request(int id, string method, string parameters) =>
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"{{{method}}}","params":{{{parameters}}}}""";

        public static string Position(string uri, int line, int character) =>
            $$$"""{"textDocument":{"uri":"{{{uri}}}"},"position":{"line":{{{line}}},"character":{{{character}}}},"context":{"triggerKind":1}}""";

        private static string Quoted(string text) => JsonSerializer.Serialize(text, ScriptTestJson.Default.String);

        /// <summary>Writes <paramref name="messages"/> in one write.</summary>
        public async Task WriteAsync(IEnumerable<string> messages)
        {
            var frames = new StringBuilder();
            foreach (string message in messages)
                frames.Append($"Content-Length: {Encoding.UTF8.GetByteCount(message)}\r\n\r\n").Append(message);
            byte[] bytes = Encoding.UTF8.GetBytes(frames.ToString());
            await _process.Input.WriteAsync(bytes, TestContext.Current.CancellationToken);
            await _process.Input.FlushAsync(TestContext.Current.CancellationToken);
        }

        /// <summary>Reads messages, up to the first that <paramref name="last"/> holds for.</summary>
        public async Task<List<JsonElement>> ReadUntilAsync(Func<JsonElement, bool> last)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var messages = new List<JsonElement>();
            while (true)
            {
                JsonElement message = await _frames.ReadAsync(timeout.Token);
                messages.Add(message);
                if (last(message))
                    return messages;
            }
        }

        public ValueTask DisposeAsync()
        {
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Reads the protocol's frames of a stream.</summary>
    private sealed class PipeFrames(Stream stream)
    {
        private readonly Stream _stream = stream;
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (TryTake(out JsonElement message))
                    return message;
                Memory<byte> memory = _buffer.GetMemory(64 * 1024);
                int read = await _stream.ReadAsync(memory, cancellationToken);
                if (read == 0)
                    throw new EndOfStreamException();
                _buffer.Advance(read);
            }
        }

        private bool TryTake(out JsonElement message)
        {
            message = default;
            ReadOnlySpan<byte> written = _buffer.WrittenSpan;
            int headerEnd = written.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
                return false;
            string header = Encoding.ASCII.GetString(written[..headerEnd]);
            int length = int.Parse(
                header["Content-Length: ".Length..],
                System.Globalization.CultureInfo.InvariantCulture
            );
            int start = headerEnd + 4;
            if (written.Length < start + length)
                return false;
            message = JsonElement.Parse(written.Slice(start, length));
            byte[] rest = written[(start + length)..].ToArray();
            _buffer.ResetWrittenCount();
            _buffer.Write(rest);
            return true;
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ScriptTestJson : System.Text.Json.Serialization.JsonSerializerContext;
