// A language server for the tests. It speaks the Language Server Protocol over stdio with framing and messages of its
// own, so that the client's wire format is checked against an independent reading of the specification, and it writes
// what it receives to standard error, one line each, for the tests to wait for.
//
// Documents of the language "fake-command" hold commands, one per line, which run when the document opens or changes.
// Every other document is echoed: the server publishes one diagnostic over its whole text, whose message is the text
// as the server has it.
//
// Arguments:
//   --initialize never      never answer initialize
//   --stderr <text>         write a line to standard error at start
//   --exit-at-start <code>  exit at start, before reading anything
//   --unversioned           publish diagnostics without a version
//   --full-sync             ask for the full text with every change
//   --ignore-shutdown       never answer shutdown
//   host                    start a child in "sleep" as the library starts servers, print its process id, and wait
//   sleep                   sleep, and never read standard input
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;

if (args is ["host"])
{
    string self = Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Tests.FakeServer");
    ProcessStartInfo child = RuntimeFeature.IsDynamicCodeSupported
        ? new(Environment.ProcessPath ?? "dotnet", [$"{self}.dll", "sleep"])
        : new(Environment.ProcessPath ?? self, ["sleep"]);
    using ServerProcess sleeper = ServerProcess.Start(child, null);
    Console.WriteLine(sleeper.Id);
    Thread.Sleep(Timeout.Infinite);
}
if (args is ["sleep"])
    Thread.Sleep(Timeout.Infinite);

var server = new FakeServer(args);
return server.Run();

internal sealed class FakeServer
{
    private readonly Stream _input = Console.OpenStandardInput();
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly Dictionary<string, Document> _documents = [];
    private readonly bool _neverInitialize;
    private readonly bool _unversioned;
    private readonly bool _fullSync;
    private readonly bool _ignoreShutdown;
    private readonly int? _exitAtStart;
    private readonly List<string> _startLines = [];
    private int _lastId;

    public FakeServer(string[] arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--initialize":
                    _neverInitialize = arguments[++i] == "never";
                    break;
                case "--stderr":
                    _startLines.Add(arguments[++i]);
                    break;
                case "--exit-at-start":
                    _exitAtStart = int.Parse(arguments[++i]);
                    break;
                case "--unversioned":
                    _unversioned = true;
                    break;
                case "--full-sync":
                    _fullSync = true;
                    break;
                case "--ignore-shutdown":
                    _ignoreShutdown = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument {arguments[i]}");
            }
        }
    }

    public int Run()
    {
        Log($"pid {Environment.ProcessId}");
        Log($"cwd {Directory.GetCurrentDirectory()}");
        Log($"env {Environment.GetEnvironmentVariable("FAKE_SERVER_VARIABLE") ?? "(none)"}");
        foreach (string line in _startLines)
            Log(line);
        if (_exitAtStart is { } code)
            return code;
        while (Read() is { } message)
            Handle(message);
        Log("end of input");
        return 0;
    }

    private void Handle(JsonObject message)
    {
        string? method = (string?)message["method"];
        JsonNode? id = message["id"];
        JsonNode? parameters = message["params"];
        switch (method)
        {
            case null:
                Log($"response {id?.ToJsonString() ?? "null"} {Describe(message)}");
                break;
            case "initialize":
                Log($"initialize {parameters?.ToJsonString()}");
                if (!_neverInitialize)
                {
                    Reply(
                        id,
                        new JsonObject
                        {
                            ["capabilities"] = new JsonObject
                            {
                                ["textDocumentSync"] = new JsonObject
                                {
                                    ["openClose"] = true,
                                    ["change"] = _fullSync ? 1 : 2,
                                },
                            },
                        }
                    );
                }
                break;
            case "initialized":
                Log("initialized");
                break;
            case "shutdown":
                Log("shutdown");
                if (!_ignoreShutdown)
                    Reply(id, null);
                break;
            case "exit":
                Log("exit");
                Environment.Exit(0);
                break;
            case "$/cancelRequest":
                Log($"cancel {parameters?["id"]?.ToJsonString()}");
                break;
            case "textDocument/didOpen":
                Open(Required(parameters?["textDocument"], "textDocument"));
                break;
            case "textDocument/didChange":
                Change(Required(parameters, "params"));
                break;
            case "textDocument/didClose":
                string closed = Text(parameters?["textDocument"]?["uri"], "uri");
                _documents.Remove(closed);
                Log($"close {closed}");
                break;
            case "workspace/didChangeConfiguration":
                Log($"configuration changed {parameters?.ToJsonString()}");
                Request("workspace/configuration", Configuration("fake", null));
                break;
            default:
                if (id is not null)
                    ReplyError(id, -32601, $"Unknown method {method}");
                break;
        }
    }

    private void Open(JsonNode item)
    {
        var document = new Document(
            Text(item["uri"], "uri"),
            Text(item["languageId"], "languageId"),
            Number(item["version"], "version"),
            Text(item["text"], "text")
        );
        _documents[document.Uri] = document;
        Log($"open {document.Uri} {document.LanguageId} {document.Version}");
        Changed(document);
    }

    private void Change(JsonNode parameters)
    {
        string uri = Text(parameters["textDocument"]?["uri"], "uri");
        Document document = _documents[uri];
        int ranged = 0;
        int full = 0;
        foreach (JsonNode? change in parameters["contentChanges"]?.AsArray() ?? [])
        {
            string text = Text(change?["text"], "text");
            if (change?["range"] is { } range)
            {
                ranged++;
                int start = Offset(document.Text, Required(range["start"], "start"));
                int end = Offset(document.Text, Required(range["end"], "end"));
                document.Text = string.Concat(document.Text.AsSpan(0, start), text, document.Text.AsSpan(end));
            }
            else
            {
                full++;
                document.Text = text;
            }
        }
        document.Version = Number(parameters["textDocument"]?["version"], "version");
        Log($"change {uri} {document.Version} ranged {ranged} full {full}");
        Changed(document);
    }

    private void Changed(Document document)
    {
        if (document.LanguageId != "fake-command")
        {
            (int line, int character) = End(document.Text);
            Publish(document, Diagnostic(0, 0, line, character, document.Text));
            return;
        }
        foreach (string command in document.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Run(command.Split(' ', 2));
    }

    private void Run(string[] command)
    {
        switch (command)
        {
            case ["exit", var code]:
                Log("exiting");
                Environment.Exit(int.Parse(code));
                break;
            case ["stderr", var text]:
                Console.Error.WriteLine(text);
                break;
            case ["configuration", var rest]:
                string[] parts = rest.Split(' ');
                Request("workspace/configuration", Configuration(parts[0], parts.Length > 1 ? parts[1] : null));
                break;
            case ["register"]:
                Request(
                    "client/registerCapability",
                    new JsonObject
                    {
                        ["registrations"] = new JsonArray(
                            new JsonObject { ["id"] = "1", ["method"] = "workspace/didChangeWatchedFiles" }
                        ),
                    }
                );
                break;
            case ["unregister"]:
                Request(
                    "client/unregisterCapability",
                    new JsonObject
                    {
                        ["unregisterations"] = new JsonArray(
                            new JsonObject { ["id"] = "1", ["method"] = "workspace/didChangeWatchedFiles" }
                        ),
                    }
                );
                break;
            case ["unknown-request"]:
                Request("fake/unknown", null);
                break;
            case ["out-of-range"]:
                foreach (Document document in _documents.Values.Where(d => d.LanguageId != "fake-command"))
                {
                    Publish(
                        document,
                        Diagnostic(-1, -1, 0, 0, "before the start"),
                        Diagnostic(0, 1_000_000, 1, 0, "past the end of the first line"),
                        Diagnostic(1_000_000, 0, int.MaxValue, int.MaxValue, "past the last line"),
                        Diagnostic(0, 2, 0, 1, "end before start")
                    );
                }
                break;
            case ["malformed"]:
                WriteFrame("{\"jsonrpc\": \"2.0\", \"method\": "u8.ToArray());
                break;
            case ["oversized"]:
                WriteRaw("Content-Length: 100000000\r\n\r\n"u8.ToArray());
                break;
            default:
                Log($"unknown command {string.Join(' ', command)}");
                break;
        }
    }

    private void Publish(Document document, params JsonNode[] diagnostics)
    {
        var parameters = new JsonObject { ["uri"] = document.Uri, ["diagnostics"] = new JsonArray(diagnostics) };
        if (!_unversioned)
            parameters["version"] = document.Version;
        Write(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "textDocument/publishDiagnostics",
                ["params"] = parameters,
            }
        );
    }

    private static JsonObject Diagnostic(
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter,
        string message
    ) =>
        new()
        {
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
                ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
            },
            ["severity"] = 1,
            ["code"] = "echo",
            ["message"] = message,
        };

    private static JsonObject Configuration(string section, string? scopeUri)
    {
        var item = new JsonObject { ["section"] = section };
        if (scopeUri is not null)
            item["scopeUri"] = scopeUri;
        return new JsonObject { ["items"] = new JsonArray(item) };
    }

    private void Request(string method, JsonNode? parameters)
    {
        int id = ++_lastId;
        Log($"request {id} {method}");
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
            message["params"] = parameters;
        Write(message);
    }

    private void Reply(JsonNode? id, JsonNode? result) =>
        Write(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result,
            }
        );

    private void ReplyError(JsonNode? id, int code, string message) =>
        Write(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            }
        );

    private static string Describe(JsonObject response) =>
        response["error"] is { } error
            ? $"error {error["code"]?.ToJsonString()}"
            : $"result {response["result"]?.ToJsonString() ?? "null"}";

    /// <summary>The offset of a position: lines split at \r\n, \n and \r; a character past the line's end is its end.</summary>
    private static int Offset(string text, JsonNode position)
    {
        int line = Number(position["line"], "line");
        int character = Number(position["character"], "character");
        List<(int Start, int End)> lines = Lines(text);
        if (line >= lines.Count)
            return text.Length;
        (int start, int end) = lines[line];
        return Math.Min(start + character, end);
    }

    private static (int Line, int Character) End(string text)
    {
        List<(int Start, int End)> lines = Lines(text);
        return (lines.Count - 1, text.Length - lines[^1].Start);
    }

    /// <summary>Where each line starts, and where its text ends, before its line break.</summary>
    private static List<(int Start, int End)> Lines(string text)
    {
        var lines = new List<(int, int)>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
                continue;
            int end = i;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            lines.Add((start, end));
            start = i + 1;
        }
        lines.Add((start, text.Length));
        return lines;
    }

    private JsonObject? Read()
    {
        int length = -1;
        while (ReadLine() is { } line)
        {
            if (line.Length == 0)
                break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line["Content-Length:".Length..].Trim());
        }
        if (length < 0)
            return null;
        byte[] body = new byte[length];
        _input.ReadExactly(body);
        return JsonNode.Parse(body)?.AsObject();
    }

    /// <summary>A header line without its \r\n; null at the end of the input.</summary>
    private string? ReadLine()
    {
        var line = new List<byte>();
        while (_input.ReadByte() is var b and >= 0)
        {
            if (b == '\n' && line.Count > 0 && line[^1] == '\r')
                return Encoding.ASCII.GetString([.. line[..^1]]);
            line.Add((byte)b);
        }
        return null;
    }

    private void Write(JsonNode message) => WriteFrame(Encoding.UTF8.GetBytes(message.ToJsonString()));

    private void WriteFrame(byte[] body)
    {
        WriteRaw(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        WriteRaw(body);
    }

    private void WriteRaw(byte[] bytes)
    {
        _output.Write(bytes);
        _output.Flush();
    }

    private static void Log(string line) => Console.Error.WriteLine(line);

    private static JsonNode Required(JsonNode? node, string name) =>
        node ?? throw new InvalidDataException($"{name} is missing.");

    private static string Text(JsonNode? node, string name) =>
        (string?)node ?? throw new InvalidDataException($"{name} is missing.");

    private static int Number(JsonNode? node, string name) =>
        (int?)node ?? throw new InvalidDataException($"{name} is missing.");

    private sealed class Document(string uri, string languageId, int version, string text)
    {
        public string Uri { get; } = uri;
        public string LanguageId { get; } = languageId;
        public int Version { get; set; } = version;
        public string Text { get; set; } = text;
    }
}
