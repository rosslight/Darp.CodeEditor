// A language server for the tests. It speaks the Language Server Protocol over stdio with framing and messages of its
// own, so that the client's wire format is checked against an independent reading of the specification, and it writes
// what it receives to standard error, one line each, for the tests to wait for.
//
// Documents of the language "fake-command" hold commands, one per line, which run when the document opens or changes.
// Every other document is echoed: the server publishes one diagnostic over its whole text, whose message is the text
// as the server has it.
//
// Hover over a word answers with Markdown: the word in bold, the document's language as inline code, and the word's
// line as a code block in that language.
// Hover over the word "slow" is never answered. Semantic tokens come from words, runs of letters, digits and
// underscores: a word "class_static" is a token of the type "class" with the modifier "static", if the legend names
// them; other words are not tokens. Either request about a document that is not open is answered with an error.
//
// Completion answers with what the command "completion" set, or with generated items (--completion-items); "." asks
// for it as it is typed, and ";" commits items. Requests are logged with the caret's offset at the end, and so is
// signature help. Resolving an item answers the item with the properties of its "data"
// added. Signature help answers inside parentheses, "(" asks for it and "," asks again: the signatures that the command
// "signatures" set, or two of "call", with the parameter at the position active, and the signature that the client
// says it shows, or the first. Completion in a document with the word "slow" is never answered.
//
// Arguments:
//   --initialize never      never answer initialize
//   --stderr <text>         write a line to standard error at start
//   --exit-at-start <code>  exit at start, before reading anything
//   --unversioned           publish diagnostics without a version
//   --no-diagnostics        publish no diagnostics for echoed documents
//   --full-sync             ask for the full text with every change
//   --text-document-sync <json>  answer initialize with this textDocumentSync capability
//   --vscode-uris           publish diagnostics with file URIs written as VS Code's URI library writes them
//   --ignore-shutdown       never answer shutdown
//   --semantic-tokens <range|full|none>  offer semantic tokens for ranges and the whole text, the whole text only, or
//                           none; ranges and the whole text by default
//   --completion-items <n>  answer completion with n generated items, as a large server does
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
    private readonly bool _noDiagnostics;
    private readonly bool _fullSync;
    private readonly bool _ignoreShutdown;
    private readonly JsonNode? _textDocumentSync;
    private readonly bool _vscodeUris;
    private readonly int? _exitAtStart;
    private readonly string _semanticTokens = "range";
    private readonly int _completionItems;

    /// <summary>The legend of semantic tokens, in another order than the client's, and with a type of its own.</summary>
    private static readonly string[] s_tokenTypes =
    [
        "stringLiteral",
        "variable",
        "class",
        "function",
        "keyword",
        "type",
    ];

    private static readonly string[] s_tokenModifiers = ["static", "readonly", "defaultLibrary", "deprecated"];

    /// <summary>Set by the command <c>hold-semantic-tokens</c>: semantic tokens are no longer answered.</summary>
    private bool _holdSemanticTokens;

    /// <summary>Set by the command <c>completion</c>: the result that completion answers with.</summary>
    private JsonNode? _completion;

    /// <summary>Set by the command <c>hold-completion</c>: completion is no longer answered.</summary>
    private bool _holdCompletion;

    /// <summary>Set by the command <c>hold-resolve</c>: resolve requests wait for the command <c>release-resolve</c>.</summary>
    private bool _holdResolve;

    private readonly List<(JsonNode? Id, JsonNode Item)> _heldResolves = [];

    /// <summary>Set by the command <c>signatures</c>: the signatures of signature help.</summary>
    private JsonNode _signatures = JsonNode.Parse(
        """
        [
          { "label": "call(first, second)", "documentation": "Calls with two.",
            "parameters": [ { "label": "first", "documentation": { "kind": "markdown", "value": "The **first** one." } }, { "label": "second" } ] },
          { "label": "call(first, second, third)", "documentation": "Calls with three.",
            "parameters": [ { "label": [5, 10] }, { "label": [12, 18] }, { "label": [20, 25], "documentation": "The third one." } ] }
        ]
        """
    )!;
    private readonly List<string> _startLines = [];
    private int _lastId;

    /// <summary>Set by the command <c>peek</c>: the file whose existence lets the server read on.</summary>
    private string? _peekSignal;

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
                case "--no-diagnostics":
                    _noDiagnostics = true;
                    break;
                case "--full-sync":
                    _fullSync = true;
                    break;
                case "--ignore-shutdown":
                    _ignoreShutdown = true;
                    break;
                case "--vscode-uris":
                    _vscodeUris = true;
                    break;
                case "--text-document-sync":
                    _textDocumentSync = JsonNode.Parse(arguments[++i]);
                    break;
                case "--semantic-tokens":
                    _semanticTokens = arguments[++i];
                    break;
                case "--completion-items":
                    _completionItems = int.Parse(arguments[++i]);
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
                                ["textDocumentSync"] =
                                    _textDocumentSync?.DeepClone()
                                    ?? new JsonObject { ["openClose"] = true, ["change"] = _fullSync ? 1 : 2 },
                                ["hoverProvider"] = true,
                                ["semanticTokensProvider"] = SemanticTokensProvider(),
                                ["completionProvider"] = new JsonObject
                                {
                                    ["triggerCharacters"] = new JsonArray("."),
                                    ["allCommitCharacters"] = new JsonArray(";"),
                                    ["resolveProvider"] = true,
                                },
                                ["signatureHelpProvider"] = new JsonObject
                                {
                                    ["triggerCharacters"] = new JsonArray("("),
                                    ["retriggerCharacters"] = new JsonArray(","),
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
            case "textDocument/hover":
                Hover(id, Required(parameters, "params"));
                break;
            case "textDocument/semanticTokens/full":
            case "textDocument/semanticTokens/range":
                SemanticTokens(id, method, Required(parameters, "params"));
                break;
            case "textDocument/completion":
                Completion(id, Required(parameters, "params"));
                break;
            case "completionItem/resolve":
                Resolve(id, Required(parameters, "params"));
                break;
            case "textDocument/signatureHelp":
                SignatureHelp(id, Required(parameters, "params"));
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
            if (_noDiagnostics)
                return;
            (int line, int character) = End(document.Text);
            Publish(document, !_unversioned, Diagnostic(0, 0, line, character, document.Text));
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
                        !_unversioned,
                        Diagnostic(-1, -1, 0, 0, "before the start"),
                        Diagnostic(0, 1_000_000, 1, 0, "past the end of the first line"),
                        Diagnostic(1_000_000, 0, int.MaxValue, int.MaxValue, "past the last line"),
                        Diagnostic(0, 2, 0, 1, "end before start")
                    );
                }
                break;
            case ["stop-reading"]:
                Log("not reading");
                Thread.Sleep(Timeout.Infinite);
                break;
            case ["hold-semantic-tokens"]:
                _holdSemanticTokens = true;
                Log("holding semantic tokens");
                break;
            case ["completion", var json]:
                _completion = JsonNode.Parse(json);
                Log("set completion");
                break;
            case ["hold-completion"]:
                _holdCompletion = true;
                Log("holding completion");
                break;
            case ["hold-resolve"]:
                _holdResolve = true;
                Log("holding resolve");
                break;
            case ["release-resolve"]:
                _holdResolve = false;
                foreach ((JsonNode? id, JsonNode item) in _heldResolves)
                    Reply(id, Resolved(item));
                _heldResolves.Clear();
                Log("released resolve");
                break;
            case ["signatures", var json]:
                _signatures = JsonNode.Parse(json) ?? throw new InvalidDataException("No signatures.");
                Log("set signatures");
                break;
            case ["peek", var signal]:
                _peekSignal = signal;
                Log("peeking");
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

    private JsonNode? SemanticTokensProvider() =>
        _semanticTokens == "none"
            ? null
            : new JsonObject
            {
                ["legend"] = new JsonObject
                {
                    ["tokenTypes"] = new JsonArray([.. s_tokenTypes.Select(t => JsonValue.Create(t))]),
                    ["tokenModifiers"] = new JsonArray([.. s_tokenModifiers.Select(m => JsonValue.Create(m))]),
                },
                ["range"] = _semanticTokens == "range",
                ["full"] = true,
            };

    /// <summary>Answers with the word at the position, and its line as code; or never, for the word "slow".</summary>
    private void Hover(JsonNode? id, JsonNode parameters)
    {
        if (Known(id, "hover", parameters) is not { } document)
            return;
        int offset = Offset(document.Text, Required(parameters["position"], "position"));
        (int start, int end) = WordAt(document.Text, offset);
        string word = document.Text[start..end];
        Log($"hover {id?.ToJsonString()} {word}");
        if (word == "slow")
            return;
        (int lineStart, int lineEnd) = Lines(document.Text).Last(line => line.Start <= offset);
        Reply(
            id,
            new JsonObject
            {
                ["contents"] = new JsonObject
                {
                    ["kind"] = "markdown",
                    ["value"] =
                        $"**{word}** is a word in `{document.LanguageId}`.\n\n```{document.LanguageId}\n{document.Text[lineStart..lineEnd]}\n```",
                },
                ["range"] = new JsonObject
                {
                    ["start"] = Position(document.Text, start),
                    ["end"] = Position(document.Text, end),
                },
            }
        );
    }

    /// <summary>
    /// The open document that a request names; for one that is not open, says so and answers the request with an error,
    /// as a server that never heard of it would.
    /// </summary>
    private Document? Known(JsonNode? id, string request, JsonNode parameters)
    {
        string uri = Text(parameters["textDocument"]?["uri"], "uri");
        if (_documents.TryGetValue(uri, out Document? document))
            return document;
        Log($"{request} of a document that is not open: {uri}");
        ReplyError(id, -32602, $"{uri} is not open.");
        return null;
    }

    /// <summary>Answers with the tokens of the words in the range, or in the whole text; or never, once held.</summary>
    private void SemanticTokens(JsonNode? id, string method, JsonNode parameters)
    {
        string uri = Text(parameters["textDocument"]?["uri"], "uri");
        if (Known(id, method["textDocument/".Length..], parameters) is not { } document)
            return;
        (int first, int last) = (0, int.MaxValue);
        if (parameters["range"] is { } range)
            (first, last) = (Number(range["start"]?["line"], "line"), Number(range["end"]?["line"], "line"));
        Log($"{method["textDocument/".Length..]} {uri} {(parameters["range"] is null ? "all" : $"{first}-{last}")}");
        if (_holdSemanticTokens)
            return;
        var data = new JsonArray();
        (int line, int character) previous = (0, 0);
        List<(int Start, int End)> lines = Lines(document.Text);
        for (int i = first; i < lines.Count && i <= last; i++)
        {
            (int lineStart, int lineEnd) = lines[i];
            for (int start = lineStart; start < lineEnd; )
            {
                (int wordStart, int wordEnd) = WordAt(document.Text, start);
                if (wordEnd == start)
                {
                    start++;
                    continue;
                }
                string[] parts = document.Text[wordStart..wordEnd].Split('_');
                int type = Array.IndexOf(s_tokenTypes, parts[0]);
                if (type >= 0)
                {
                    int modifiers = 0;
                    foreach (string part in parts[1..])
                        modifiers |= Array.IndexOf(s_tokenModifiers, part) is var bit and >= 0 ? 1 << bit : 0;
                    int character = wordStart - lineStart;
                    foreach (
                        int number in (int[])
                            [
                                i - previous.line,
                                i == previous.line ? character - previous.character : character,
                                wordEnd - wordStart,
                                type,
                                modifiers,
                            ]
                    )
                    {
                        data.Add((JsonNode)JsonValue.Create(number));
                    }
                    previous = (i, character);
                }
                start = wordEnd;
            }
        }
        Reply(id, new JsonObject { ["data"] = data });
    }

    /// <summary>Answers with the generated items, or the result set by the command; or never, once held.</summary>
    private void Completion(JsonNode? id, JsonNode parameters)
    {
        string uri = Text(parameters["textDocument"]?["uri"], "uri");
        Document document = _documents[uri];
        JsonNode? context = parameters["context"];
        int offset = Offset(document.Text, Required(parameters["position"], "position"));
        Log(
            $"completion {id?.ToJsonString()} {uri} {(int?)context?["triggerKind"]} {(string?)context?["triggerCharacter"] ?? "-"} {offset}"
        );
        if (_holdCompletion || document.Text.Contains("slow", StringComparison.Ordinal))
            return;
        if (_completionItems == 0)
        {
            Reply(id, _completion?.DeepClone());
            return;
        }
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];
        var items = new JsonArray();
        for (int i = 0; i < _completionItems; i++)
        {
            string label = $"{words[i % words.Length]}{i}";
            items.Add(
                (JsonNode)
                    new JsonObject
                    {
                        ["label"] = label,
                        ["kind"] = i % 25 + 1,
                        ["sortText"] = $"{i:D5}",
                        ["filterText"] = label,
                        ["data"] = new JsonObject { ["index"] = i },
                    }
            );
        }
        Reply(id, new JsonObject { ["isIncomplete"] = false, ["items"] = items });
    }

    /// <summary>Answers with the item and the properties of its data; once held, when the hold is released.</summary>
    private void Resolve(JsonNode? id, JsonNode item)
    {
        Log($"resolve {id?.ToJsonString()} {(string?)item["label"]}");
        if (_holdResolve)
            _heldResolves.Add((id?.DeepClone(), item.DeepClone()));
        else
            Reply(id, Resolved(item));
    }

    private static JsonNode Resolved(JsonNode item)
    {
        JsonObject resolved = item.DeepClone().AsObject();
        if (item["data"] is JsonObject data)
        {
            foreach ((string name, JsonNode? value) in data)
                resolved[name] = value?.DeepClone();
        }
        return resolved;
    }

    /// <summary>
    /// Answers inside parentheses with the signatures, the parameter at the position active and the signature that the
    /// client shows, or the first; outside them with null.
    /// </summary>
    private void SignatureHelp(JsonNode? id, JsonNode parameters)
    {
        Document document = _documents[Text(parameters["textDocument"]?["uri"], "uri")];
        JsonNode? context = parameters["context"];
        JsonNode? showing = context?["activeSignatureHelp"];
        int offset = Offset(document.Text, Required(parameters["position"], "position"));
        Log(
            $"signatureHelp {id?.ToJsonString()} {(int?)context?["triggerKind"]} {(string?)context?["triggerCharacter"] ?? "-"} "
                + $"{(bool?)context?["isRetrigger"]} {showing?["activeSignature"]?.ToJsonString() ?? "-"} {offset}"
        );
        // The innermost parenthesis still open before the position, and the commas in it.
        int depth = 0;
        int commas = 0;
        for (int i = offset - 1; i >= 0; i--)
        {
            char c = document.Text[i];
            if (c == ')')
                depth++;
            else if (c == '(' && depth > 0)
                depth--;
            else if (c == '(')
            {
                Reply(
                    id,
                    new JsonObject
                    {
                        ["signatures"] = _signatures.DeepClone(),
                        ["activeSignature"] = (int?)showing?["activeSignature"] ?? 0,
                        ["activeParameter"] = commas,
                    }
                );
                return;
            }
            else if (c == ',' && depth == 0)
                commas++;
        }
        Reply(id, null);
    }

    /// <summary>The word of letters, digits and underscores at an offset; empty where there is none.</summary>
    private static (int Start, int End) WordAt(string text, int offset)
    {
        int start = offset;
        while (start > 0 && IsWordCharacter(text[start - 1]))
            start--;
        int end = offset;
        while (end < text.Length && IsWordCharacter(text[end]))
            end++;
        return (start, end);

        static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }

    private static JsonObject Position(string text, int offset)
    {
        List<(int Start, int End)> lines = Lines(text);
        int line = lines.FindLastIndex(l => l.Start <= offset);
        return new JsonObject { ["line"] = line, ["character"] = offset - lines[line].Start };
    }

    private void Publish(Document document, bool versioned, params JsonNode[] diagnostics)
    {
        string uri = _vscodeUris ? VscodeUri(document.Uri) : document.Uri;
        var parameters = new JsonObject { ["uri"] = uri, ["diagnostics"] = new JsonArray(diagnostics) };
        if (versioned)
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

    /// <summary>
    /// A file URI as VS Code's URI library writes it: a drive letter in lower case, and every byte of the path but
    /// letters, digits, <c>-._~</c> and <c>/</c> percent-encoded, so <c>file:///C:/a+b.txt</c> becomes
    /// <c>file:///c%3A/a%2Bb.txt</c>.
    /// </summary>
    private static string VscodeUri(string uri)
    {
        if (!uri.StartsWith("file://", StringComparison.Ordinal))
            return uri;
        string path = Uri.UnescapeDataString(uri["file://".Length..]);
        if (path.Length > 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':')
            path = $"/{char.ToLowerInvariant(path[1])}{path[2..]}";
        var encoded = new StringBuilder("file://");
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            if (
                char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/'
            )
                encoded.Append((char)b);
            else
                encoded.Append($"%{b:X2}");
        }
        return encoded.ToString();
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
        if (_peekSignal is { } signal)
        {
            // Between a message's header and its body: the client is still writing the body.
            _peekSignal = null;
            foreach (Document document in _documents.Values.Where(d => d.LanguageId != "fake-command"))
            {
                (int line, int character) = End(document.Text);
                Publish(document, versioned: false, Diagnostic(0, 0, line, character, document.Text));
            }
            Log($"peeked into a message of {length} bytes");
            while (!File.Exists(signal))
                Thread.Sleep(10);
        }
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
