# Darp.CodeEditor (spike)

A prototype of a code editor library for Avalonia desktop apps. It exists to prove or disprove one architecture with
measured numbers; it is not the product. "Darp.CodeEditor" is a working name.

## Architecture

```
Darp.CodeEditor            NativeAOT-compatible; no Roslyn, no MEF
  CodeEditor               AvaloniaEdit TextEditor + TextMate highlighting + search panel
  Highlighting/            TextMate: visible lines tokenized in the frame (≤ 4 ms), the rest on the thread pool
  Theming/                 VS Code themes (Dark+/Light+ or a file) following the app's light/dark variant
  Popups/                  completion, signature help and hover, drawn on the window's overlay layer
  Editing/                 auto-indent, bracket matching, auto-closing pairs, toggle comment (LanguageConfiguration)
  LanguageServices/        ILanguageService: open/change/close documents, versioned requests, pushed diagnostics
    LspLanguageService     ILanguageService over LSP: one language server per service, restarted when it crashes
  LanguageFeatures         sends edits, asks for the visible lines' colors, drops or maps forward stale results
  Completion/              one list per session (asked again while incomplete), local filtering, lazy resolve
  Markers/                 semantic colors and diagnostic squiggles, read from segment caches during render
  Instrumentation/         UiStallProbe: UI-thread latency and GC pauses per phase

Darp.CodeEditor.Lsp        NativeAOT-compatible; only the shared framework
  JsonRpcConnection        JSON-RPC 2.0 with Content-Length framing, for both ends of a connection
  LanguageServer           the server lifecycle: initialize, shutdown, exit
  LanguageClient           the client lifecycle, and the requests servers send their client
  Hosting/                 ChildProcess: helper processes that end with the app; Supervisor: restarts with backoff
  Protocol/                source-generated DTOs for the subset of LSP the editor and its servers use

Darp.CodeEditor.CSharp     NativeAOT-compatible; no Roslyn
  CSharpLanguageServer     the LspLanguageService of the C# script language server for an environment
  build/                   Darp.CodeEditor.CSharp.targets: puts the server next to the app

Darp.CodeEditor.CSharp.Hosting    NativeAOT-compatible; no Roslyn
  CSharpScriptEnvironment         globals contract assembly + type name, references, imports, compiler options
  CSharpScriptInitializationOptions   the versioned "csharpScript" section of LSP initializationOptions

Darp.CodeEditor.CSharp.Scripting  JIT only; Roslyn Scripting
  ResolvedScriptEnvironment       reference files, parse and compilation options of an environment;
                                  ScriptOptions for hosts, the same options for the server

Darp.CodeEditor.CSharp.Server     JIT only, framework-dependent .NET 10 console app: LSP over stdio
  Darp.CodeEditor.Lsp             protocol layer and lifecycle
  ScriptWorkspace                 one script submission per document, globals type loaded as metadata only

Darp.CodeEditor.CSharp.Execution  NativeAOT-compatible; no Roslyn; StreamJsonRpc
  CSharpScriptRunner              runs scripts in the runner process, returns their handlers as typed proxies
  ScriptBatcher                   sends single items to a handler in batches
  build/*.targets                 puts the runner and its .NET runtime next to the app

Darp.CodeEditor.CSharp.Runner     JIT only, framework-dependent helper: compiles and runs scripts, RPC over stdio

Darp.CodeEditor.Luau       NativeAOT-compatible
  LuauLanguageServer       the LspLanguageService of luau-lsp for an environment: the app's Luau API
  LuauLanguage             the Luau grammar and editing helpers for an editor
  build/                   Darp.CodeEditor.Luau.targets: puts luau-lsp and the grammar next to the app
```

Every language goes through an out-of-process LSP server, for JIT and NativeAOT hosts alike: C# through the C# script
language server, Luau through luau-lsp.

Rules the code follows:

1. The editor works fully without a language service (typing, TextMate colors, search). Setting
   `CodeEditor.LanguageService` later switches on completion, diagnostics, semantic colors, quick info and signature help.
2. The boundary is async only. The editor calls the service on the UI thread, and the service only queues the work.
   Rendering and layout read only caches that results fill; they never call the service.
3. Every request carries a document version and a cancellation token. A keystroke cancels document-wide requests, which
   tells the server too; results for an older version are dropped, and cached results move with the text (AvaloniaEdit
   segments) until new ones arrive. Completion results map forward while the user keeps typing the same word.
4. Completion fetches its list off the UI thread and filters it locally per keystroke; a list the server marked
   incomplete is asked again for each further letter. Items resolve lazily for their descriptions and additional edits,
   and commit inserts text the service already computed.
5. No `.Result`, `.Wait()` or `GetAwaiter().GetResult()` on the UI thread.

## Layout

| Path | What |
| --- | --- |
| `src/Darp.CodeEditor` | The editor library (`IsAotCompatible`). |
| `src/Darp.CodeEditor.CSharp` | `IsAotCompatible`: C# script editing through the C# script language server, and the targets that put the server next to an app. |
| `src/Darp.CodeEditor.CSharp.Hosting` | `IsAotCompatible`: the script environment a host describes, and its `initializationOptions` section. |
| `src/Darp.CodeEditor.CSharp.Scripting` | Turns an environment into `ScriptOptions` for the host and compiler options for the server. |
| `src/Darp.CodeEditor.CSharp.Server` | The C# script language server (LSP over stdio). |
| `src/Darp.CodeEditor.CSharp.Execution` | `IsAotCompatible`: runs scripts in the runner; the MSBuild targets that ship it. |
| `src/Darp.CodeEditor.CSharp.Runner` | The runner: a helper process that compiles and runs scripts. |
| `src/Darp.CodeEditor.Lsp` | JSON-RPC and LSP layer for language servers and the editor's LSP client (`IsAotCompatible`). |
| `src/Darp.CodeEditor.Luau` | `IsAotCompatible`: Luau editing through luau-lsp with the app's Luau API, the Luau grammar, and the targets that put luau-lsp next to an app. |
| `samples/Darp.CodeEditor.Demo` | JIT desktop app: C# script through the C# server and Luau through luau-lsp, each with completion, diagnostics, semantic colors, quick info, signature help; JSON with highlighting; Run (F5) runs packet.csx in the script runner. |
| `samples/Darp.CodeEditor.Demo.Aot` | `PublishAot=true`, without Roslyn: the same C# and Luau features through the same servers, JSON highlighting, search, a small in-process JSON language service (word completion, parse errors), and Run (F5) for packet.csx through the script runner. |
| `samples/Darp.CodeEditor.Demo.Globals` | The globals of script.csx; the C# server reads the assembly as a file, also next to the AOT demo. |
| `samples/Darp.CodeEditor.Demo.ScriptApi` | The demos' script API: globals, a service the app implements, the handler scripts return. |
| `samples/Shared` | Sample documents, measurement scenarios, the Run action and the Luau API (`Luau/`) shared by both demos. |
| `tests/Darp.CodeEditor.Tests` | The LSP client against the layer's toy server and the C# server; the completion matcher; headless tests of the editing helpers. |
| `tests/Darp.CodeEditor.Lsp.Tests` | Tests of the LSP layer, a toy server and client, and an interop test against luau-lsp. |
| `tests/Darp.CodeEditor.Lsp.AotSmoke` | NativeAOT build of the toy server and client, and of the luau-lsp check. |
| `tests/Darp.CodeEditor.Luau.Tests` | Luau through the editor's LSP client and luau-lsp with a host API; the checksum checks of the luau-lsp download. |
| `tests/Darp.CodeEditor.Lsp.TestProcess` | A child process for the tests of `ChildProcess`, including one that dies with its host. |
| `tests/Darp.CodeEditor.CSharp.Server.Tests` | Compiler-parity tests against `CSharpScript`, and tests that drive the server process over stdio. |
| `tests/Darp.CodeEditor.CSharp.TestGlobals` | A globals contract assembly for the tests, including the demo's `ScriptGlobals`. |
| `tests/Shared` | A small LSP client over stdio, used by the tests and the benchmark. |
| `tests/Darp.CodeEditor.CSharp.Execution.Tests` | Tests of the runner: handlers, callbacks, batching, diagnostics, faults, process ownership, NativeAOT publish. |
| `tests/Darp.CodeEditor.CSharp.Execution.TestApi` | A script API assembly for the runner tests and benchmark. |
| `tests/Darp.CodeEditor.CSharp.Execution.TestHost` | A small app that runs a script; the tests start it and publish it with NativeAOT. |
| `benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark` | Server against the former in-process Roslyn service, kept in `InProcess/` as the baseline: startup, completion latency, memory, sizes. |
| `benchmarks/Darp.CodeEditor.CSharp.Execution.Benchmark` | The runner from a JIT or NativeAOT app: cold start to the first result, warm calls, batches. |
| `tools/Darp.CodeEditor.Probe` | Measures from outside the app: keystroke to changed pixels on screen, startup on screen, process-tree memory. |
| `tools/measure` | Scripts that run the benchmarks and the probe for both demos on Linux and Windows. |

## Run

Requires the .NET 10 SDK.

```bash
# JIT demo
dotnet run --project samples/Darp.CodeEditor.Demo -c Release

# NativeAOT demo
dotnet publish samples/Darp.CodeEditor.Demo.Aot -c Release -r linux-x64
samples/Darp.CodeEditor.Demo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Demo.Aot

# Tests
dotnet test --project tests/Darp.CodeEditor.Tests
dotnet test --project tests/Darp.CodeEditor.CSharp.Server.Tests
dotnet test --project tests/Darp.CodeEditor.CSharp.Execution.Tests
dotnet test --project tests/Darp.CodeEditor.Luau.Tests
```

The LSP layer has its own tests, with an interop test against luau-lsp; the NativeAOT check runs it too. The build
downloads luau-lsp for them, as for the demos (see [Luau](#luau)).

```bash
dotnet test --project tests/Darp.CodeEditor.Lsp.Tests

# Fails on any trim or AOT warning
dotnet publish tests/Darp.CodeEditor.Lsp.AotSmoke -c Release -r linux-x64
tests/Darp.CodeEditor.Lsp.AotSmoke/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Lsp.AotSmoke
```

Both demos start the C# script language server from `helpers/csharp-server/` next to them, which
`Darp.CodeEditor.CSharp.targets` fills on build and publish, with `dotnet` on the installed .NET 10 runtime, and
luau-lsp from `helpers/luau-lsp/`, which `Darp.CodeEditor.Luau.targets` fills. On a Linux machine without a display, run
the demos on a virtual X server (for example `xvfb-run` or any X server with `DISPLAY` set).

Command line flags of both demos:

| Flag | Effect |
| --- | --- |
| `--smoke` | Opens the window, waits for the first highlighted frame, checks search and replace, prints timings and exits. |
| `--bench` | `--smoke` plus the full measurements: completion latency, typing bursts, large file, long line. |
| `--out <file>` | Also writes the results to a file. |
| `--hold` | Keeps the window open after `--smoke` or `--bench`. |
| `--scene` | Prepares a scene for a screenshot and stays open. |
| `--run-script` | Runs packet.csx through the script runner once the window opens, prints the output and exits. |
| `--skip-highlighting-bench` | Skips the highlighting scenarios of `--bench`. |
| `--no-language-service` | Starts without the C# server and luau-lsp, to attribute startup stalls. |
| `--csharp-server <path>` | The C# server's `.dll` or app host, instead of the one next to the demo. |
| `--dotnet <path>` | The `dotnet` executable that runs the C# server. |
| `--luau-lsp <path>` | The luau-lsp executable, instead of the one next to the demo. |
| `--server-log` | Writes the standard error and log messages of the C# server and luau-lsp to standard error. |
| `--low-latency-gc` | JIT demo only: sets `GCLatencyMode.SustainedLowLatency`. |
| `--light` | Starts with the light theme variant. The status bar's button switches between light and dark at run time. |
| `--e2e <file>` | Writes where line 1 of a C# document without language features ends on screen, for `Probe latency`, and stays open. |

## Measure from outside

`tools/measure/run-windows.ps1` runs `--bench` for both demos and times highlighted text on screen after launch; it
injects no input. `tools/measure/run-linux.sh` does the same and, for test VMs, also measures keystroke to changed
pixels with the probe's `latency` mode (XTest).

Windows gets no key-to-pixel measurement: `SendInput` inserts keys into the system input stream, not into a window,
so a key the probe checked against the demo's window can still reach another application if the focus changes in
between. No check makes that safe on a machine someone works on, so the probe refuses `latency` on Windows; the bench's
keystroke timings, taken inside the demo process, stand in for it.

On X11 the probe types real keys into the demo window, so **nobody may use the display while it runs**. It never
clicks: a demo started with `--e2e` focuses its own editor and reports its process and window. Before and after every
key the probe checks that this window is the active window holding the input focus and that the demo still runs,
before every key that no modifier key or mouse button is down, and that XTest took every event; it aborts otherwise,
and the script then stops and closes the demo. It counts down 5 s with a warning before the first key and types at
most 400 keys. Its 3-minute deadline is checked before each key and does not interrupt one. Check and key are not
atomic, and a keyboard grab by another client is not detected.

Results of the editor polish work on Linux: [docs/editor-polish.md](docs/editor-polish.md).

## LSP layer

`Darp.CodeEditor.Lsp` dispatches the messages it receives as follows:

1. One loop reads the input. Responses and `$/cancelRequest` take effect as they are read, and every request gets its
   cancellation token on receipt, so a request can be cancelled while it is queued.
2. Requests and notifications are dispatched in arrival order. A `DispatchMode.Serial` handler (the default for
   notifications, such as `textDocument/didChange`) runs to completion before the next message. A
   `DispatchMode.Concurrent` handler (the default for requests) runs until its first incomplete `await`, so it reads
   the state that all earlier messages left behind, and then runs alongside later messages. It must therefore capture
   the state it works on, such as the document text and version, before that first `await`. A `Serial` handler must
   not await a request to the peer that the peer can only answer after a request of its own was dispatched here: that
   deadlocks.
3. A request whose token was cancelled by the time its handler returned answers `RequestCancelled`, whatever the
   handler returned. Handlers throw `JsonRpcException`, for example with `ContentModified`, to answer other errors.
4. Sending only queues a message. One write loop serializes and writes messages in the order they were sent, so
   callers never wait for serialization or a slow peer. A request cancelled before it went out is left out; otherwise
   `$/cancelRequest` follows it. Disposing drops what was not written yet. A failed write ends the connection, since
   part of the frame may be on the stream; messages that fail to serialize fail only their own send. The send queue
   has no bound: callers that send many large messages, such as document changes, await each send to get
   back-pressure.
5. `JsonRpcLimits` caps headers and messages; a longer message ends the connection. Null results are written as
   `"result": null`, and document URIs are kept as sent.

## Language servers in the editor

`LspLanguageService` is the editor's `ILanguageService` over LSP. One service owns one server and serves every editor of
its language. A host creates the C# one from the script environment:

```csharp
LspLanguageService csharp = CSharpLanguageServer.CreateService(environment);
_ = csharp.PrewarmAsync();          // optional: start the server before the first editor opens
editor.LanguageService = csharp;    // opens the document; the first open document starts the server
await csharp.DisposeAsync();        // shutdown and exit, then the process ends
```

`CSharpLanguageServerOptions` sets the server (`ServerPath`: its `.dll`, run with `dotnet`, or its app host), the
`dotnet` executable (`DotNetPath`) and the runtime an app host runs on (`DotNetRoot`), which is where a runtime bundled
with the app plugs in. Any other server runs through `LspLanguageServiceOptions` with a start function, such as
`LanguageServerTransport.StartProcessAsync`; `Darp.CodeEditor.Luau` starts luau-lsp this way, with whole-document
semantic tokens and its settings requests answered by the `Settings` hook.

1. The editor opens, changes and closes documents and makes requests on the UI thread; the service only queues them.
   One loop keeps a copy of each document, converts offsets to UTF-16 positions, and sends changes before the requests
   made after them. It waits until each change is written; edits that queued up meanwhile go out as one `didChange`, so
   a server that stops reading does not grow the queue by a message per keystroke.
2. Responses are converted with the document version they were requested for. Cancelling a request sends
   `$/cancelRequest`; `RequestCancelled` and `ContentModified` answers return nothing.
3. Diagnostics are pushed: the editor shows those of its current version and drops older ones.
4. Completion lists keep their incompleteness, per-item commit characters and edit ranges. Resolve loads descriptions
   and additional edits, such as a `using` directive, which commit applies in the same undo step: at once, or when a
   late resolve arrives while the document is still as the commit left it and the commit's undo step is the last;
   after any other edit or undo step they are dropped. Signature help that an edit made stale is asked again once
   typing pauses for 100 ms.
5. The server runs as a `ChildProcess`: on Windows in a job object that kills it when the app dies, on Linux with
   `PR_SET_PDEATHSIG` armed through `setpriv` from a launcher thread that never ends. When it crashes, a `Supervisor`
   starts it again after 0.5 s, doubling up to 30 s while it keeps failing, and opens the documents again. Meanwhile
   requests answer nothing and the editors keep their TextMate colors; `Invalidated` brings the features back.
6. A server that answers a request the editor still waits for, reads what it was sent, or answers `initialize` no
   sooner than `ResponseTimeout` (30 s by default) counts as hung: it is ended without shutdown and restarts like a
   crashed one, and `State` says `Restarting` at once. A server that has answered nothing yet, such as one that still
   loads, gets twice as long. Each hang doubles the time of the next server, up to `MaxResponseTimeout` (2 min by
   default), until a server answered and kept running for `StableAfter`. The UI thread never waits for any of this.

## C# script language server

A framework-dependent .NET 10 console app that speaks LSP over stdio. It needs the .NET 10 runtime, installed or
downloaded per user, and no SDK. It never runs script code: the globals type is read as metadata.

```bash
dotnet publish src/Darp.CodeEditor.CSharp.Server -c Release -o artifacts/csharp-server
# With ReadyToRun, which reaches the first completion about 40 % sooner (see the benchmark below):
dotnet publish src/Darp.CodeEditor.CSharp.Server -c Release -r linux-x64 --self-contained false \
  -p:PublishReadyToRun=true -o artifacts/csharp-server-r2r

# The editor starts it and talks LSP on stdin/stdout; stderr is a log.
dotnet artifacts/csharp-server/Darp.CodeEditor.CSharp.Server.dll
```

| Argument | Effect |
| --- | --- |
| `--diagnostics-delay <ms>` | How long the text must stay unchanged before diagnostics are computed. Default 200. |
| `--no-warm-up` | Skips running every feature once in the background after the environment loaded. |
| `--completion-filter-above <items>` | Completion lists with more items are filtered by the typed text and marked incomplete. Default 1000. |
| `--completion-limit <items>` | The most items a filtered list keeps, the best matches first. Default 1000. |

Other arguments, such as `--stdio`, are ignored. The server exits when its input ends, after `exit`, or when the
process named by `processId` in the answered `initialize` ends.

### Script environment

The host describes the environment once, with `CSharpScriptEnvironment` from `Darp.CodeEditor.CSharp.Hosting`, and
sends it in `initializationOptions`. JIT hosts compile their scripts from the same description with
`Darp.CodeEditor.CSharp.Scripting`, so the editor reports what the host compiles:

```csharp
var environment = new CSharpScriptEnvironment
{
    Globals = new CSharpScriptGlobals("/app/MyApp.Scripting.dll", "MyApp.Scripting.ScriptGlobals"),
    FrameworkReferences = ["System.Linq"],
    Imports = ["System", "System.Linq"],
};
JsonObject initializationOptions = CSharpScriptInitializationOptions.Create(environment);

// In a JIT host that runs the scripts:
ResolvedScriptEnvironment resolved = ResolvedScriptEnvironment.Resolve(environment);
Script<object> script = resolved.CreateScript(code, typeof(ScriptGlobals), resolved.CreateScriptOptions());
```

The section, as JSON (every value but `version` is optional):

```json
{
  "csharpScript": {
    "version": 1,
    "globals": { "assemblyPath": "/app/MyApp.Scripting.dll", "typeName": "MyApp.Scripting.ScriptGlobals" },
    "references": ["/app/MyApp.Devices.dll"],
    "frameworkReferences": ["System.Linq"],
    "imports": ["System", "System.Linq"],
    "languageVersion": "latest",
    "nullable": "disable",
    "allowUnsafe": false,
    "checkOverflow": false,
    "warningLevel": 4
  }
}
```

| Field | Meaning |
| --- | --- |
| `globals` | The managed contract assembly and the full name of the globals type. NativeAOT hosts ship the assembly as a file. It may be a reference assembly, but its identity (name, version, culture, public key token) must be that of the assembly the host loads the type from; `CreateScript` checks it. |
| `references` | Absolute paths of assemblies scripts can use. Their direct dependencies are referenced too, from the same directory or the .NET runtime. |
| `frameworkReferences` | .NET runtime assemblies by name, resolved in the runtime of the process that compiles. `System.Private.CoreLib` and `System.Runtime` are always referenced. |
| `imports` | Namespaces imported into every script. |
| `languageVersion` | `latest`, `preview`, `default` or a version such as `13.0`. |
| `nullable` | `disable`, `enable`, `warnings` or `annotations`. `CSharpScript` has no public option for it, so `CreateScriptOptions` accepts only `disable`; scripts opt in with `#nullable enable`. |
| `allowUnsafe`, `checkOverflow`, `warningLevel` | Compiler options, as in `ScriptOptions`. |

The server rejects other `version` values in `initialize`. v1 has no directives: `#r` and `#load` are errors, in the
editor and in the host. `#:package` comes later.

### Protocol

| Feature | Notes |
| --- | --- |
| `initialize`, `initialized`, `shutdown`, `exit` | Requests before `initialize` fail with `ServerNotInitialized`, requests after `shutdown` with `InvalidRequest`. A failed or cancelled `initialize`, such as one with invalid options, leaves the server uninitialized and starts nothing, so the client can try again. Exit code 0 after `shutdown`, else 1. `initialize` answers right away; the environment starts loading on `initialized` (or the first `didOpen`, for clients that skip it), and requests wait for it. |
| `didOpen`, `didChange` (incremental), `didClose` | Versions are kept per document; a change for the current or an older version is ignored. Each script is its own submission. |
| `$/cancelRequest` | Answered with `RequestCancelled`; a request cancelled while it is queued does not run. |
| `completion`, `completionItem/resolve` | Edits from Roslyn's `CompletionService.GetChangeAsync`, per-item commit characters from Roslyn's rules, `itemDefaults` when the client supports them. Lists of more than 1,000 items, such as a word list after its first letter, keep the 1,000 items that match the text before the caret best, scored by the editor's own `CompletionMatcher` and are marked `isIncomplete`, so the client asks again for each further letter. Types from namespaces that are not imported insert their name; resolve adds the `using` as `additionalTextEdits` (clients that cannot resolve those lazily do not get these items). Snippets such as `for` expand on resolve, for clients that list `textEdit` in `resolveSupport`, and are left out for others. `data` is opaque. |
| `hover` | Markdown: the signature as C#, then the documentation. |
| `signatureHelp` | Every accessible overload; the best one for the typed arguments, or the one the call binds to; the active parameter, also for named arguments and `params`; parameter documentation; label offsets. |
| `publishDiagnostics` | Compiler diagnostics only, 200 ms after the last change, tagged with the document version. |
| `semanticTokens/range` | Roslyn's classifier. Token types use LSP's names where one exists (`namespace`, `class`, `method`, `property`, `variable`, `keyword`, ...) and C# names otherwise (`delegate`, `extensionMethod`, `field`, `constant`, `label`); modifier `static`. |

A new document version drops pending diagnostics but ends no request: each request answers for the text it was sent
for. LSP 3.17 asks servers not to answer `ContentModified` for changes in messages they have yet to process, since the
result may still be of use; the client cancels what it no longer needs, and a completion list stays useful while the
user types the same word. Closing or reopening the document answers its pending requests with `null`, as for any
document the server does not know.

### Benchmark

```bash
dotnet build benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark -c Release
dotnet benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark/bin/Release/net10.0/Darp.CodeEditor.CSharp.Server.Benchmark.dll --runs 5
```

It publishes the server framework-dependent and with ReadyToRun into `measurements/csharp-server/`, then starts each
variant, and the in-process `CSharpScriptLanguageService` in a child process, once per run and per first request.
Both go through the same typing schedule. Each run measures:

- process start → the first non-empty member list (`Device.`) or word list (`D` at the start of a line), on the
  demo's sample script with the demo's `ScriptGlobals`, references and imports;
- one request at a time: 20 rounds of `\nD` (word list), each letter of `evice` (asking again while the list is
  incomplete), `.` (member list) and `ReadTemperature();`, 40 ms between chunks, the first two rounds skipped;
  latency is request sent → response parsed by the client;
- typing: 150 keystrokes 40 ms apart without waiting for answers, every third also in a second document; each
  keystroke asks for semantic tokens, word starts, dots and letters of incomplete lists for completion, `(` for
  signature help, and a newer request cancels the older one of its kind; then a fresh word list, and the time until
  every request is answered;
- stress: the same at 10 ms, in both documents on every keystroke, faster than the server answers;
- RSS one second after typing, response sizes, and the size of the published files.

It writes `report.md` and `results.json` to `measurements/csharp-server/`, which git ignores. (On one shared machine,
`dotnet run --project` of the benchmark was stopped while it published; the build output runs it reliably.)

Results on 2026-11-03, Linux x64, 8 cores, .NET 10.0.13, with the server on `Darp.CodeEditor.Lsp`, 5 cold starts per
cell. The machine ran other agents (load average 3–4.5), so the numbers are indicative. Medians and percentiles;
one-at-a-time values over 90 lists per cell. "Fresh" latencies are of requests the client kept that were answered with
a result; answers are counted as result / `RequestCancelled` / `ContentModified` / other error.

| | in-process | server | server, ReadyToRun | R2R, unfiltered | R2R, filtered, no max |
| --- | ---: | ---: | ---: | ---: | ---: |
| process start → first member list (9 items) | 1387 ms | 1068 ms | 720 ms | 740 ms | 699 ms |
| process start → first word list | 1400 ms | 1065 ms | 631 ms | 678 ms | 698 ms |
| word list items after "D" | 3282 | 1000 | 1000 | 3288 | 1431 |
| one at a time: member list p50 / p95 | 2.8 / 9.3 ms | 3.5 / 9.6 ms | 4.8 / 10.6 ms | 4.6 / 9.8 ms | 5.1 / 9.2 ms |
| one at a time: word list p50 / p95 | 7.7 / 20.0 ms | 8.8 / 31.8 ms | 14.2 / 23.8 ms | 16.0 / 25.4 ms | 15.7 / 22.7 ms |
| one at a time: asked again per further letter p50 / p95 | – | 6.8 / 18.5 ms | 11.9 / 20.3 ms | – | 12.4 / 18.1 ms |
| typing (40 ms): fresh completion p50 / p95 | 3.8 / 10.6 ms | 4.5 / 11.0 ms | 7.4 / 19.8 ms | 6.6 / 12.7 ms | 8.3 / 23.0 ms |
| typing: last keystroke → fresh word list answered | 4 ms | 5 ms | 6 ms | 8 ms | 9 ms |
| stress (10 ms): fresh completion p50 / p95 | 4.1 / 23.4 ms | 5.4 / 9.7 ms | 6.5 / 14.9 ms | 8.4 / 31.4 ms | 6.9 / 19.8 ms |
| stress: fresh semantic tokens p50 / p95 | 3.2 / 6.4 ms | 3.0 / 6.1 ms | 4.5 / 8.2 ms | 4.3 / 8.2 ms | 4.8 / 8.6 ms |
| stress: answers to requests the client kept | 3313 / 0 / 0 / 0 | 3777 / 0 / 0 / 0 | 3566 / 0 / 0 / 0 | 3015 / 0 / 0 / 0 | 3387 / 0 / 0 / 0 |
| stress: answers to requests the client cancelled | 1 / 26 / 0 / 0 | 23 / 43 / 0 / 0 | 66 / 184 / 0 / 0 | 109 / 216 / 0 / 0 | 122 / 277 / 0 / 0 |
| stress: last keystroke → fresh word list answered | 5 ms | 5 ms | 7 ms | 8 ms | 7 ms |
| RSS after typing | 249 MiB¹ | 273 MiB | 282 MiB | 281 MiB | 277 MiB |
| word list response | – | 126 KiB | 126 KiB | 412 KiB | 184 KiB |
| published files / zipped | – | 26 / 10 MiB | 65 / 29 MiB | 65 / 29 MiB | 65 / 29 MiB |

¹ Without the demo's UI. In-process, the service warms up while it is created, before the first request.

- Server-side filtering keeps the warm word list's p95 near the in-process value (24 vs 20 ms here; 18 vs 20 ms in a
  quieter run the day before); its median stays about twice the in-process one, which needs no protocol. Filtering
  without a maximum gives less (p95 23 ms); a maximum of 300 gives 12.8 / 19.0 ms. Of a word list, Roslyn's
  completion takes about 6–10 ms in both.
- At 40 ms per keystroke nothing piles up: fresh answers stay within milliseconds of one request at a time. At 10 ms,
  the server answers most requests the client cancels with `RequestCancelled`; the ones with a result had finished
  before the cancel arrived. No request is answered with `ContentModified` or another error. One ReadyToRun run fell
  behind under load, with 132 of its 366 requests cancelled, about half of that column's. The backlog clears within a
  few milliseconds of the last keystroke.
- Warm-up: an earlier run measured the first request after a 2 s idle at 37 / 44 ms (member / word) with warm-up and
  about 195 ms without; when the first request comes right after start, warm-up costs it up to about 50 ms.

## Running C# scripts

Scripts run in the runner: a helper process on a .NET runtime the app ships, for JIT and NativeAOT apps alike.
NativeAOT apps cannot run freshly compiled code, and a script that hangs or crashes must not take the app down. The
runner is not the language server; both compile with `Darp.CodeEditor.CSharp.Scripting` from the same
`CSharpScriptEnvironment`, so the runner's compile errors are the diagnostics the editor shows.

```csharp
await using CSharpScriptRunner runner = await CSharpScriptRunner.StartAsync(new CSharpScriptRunnerOptions
{
    Environment = environment,                                      // the one the editor gets
    Services = [CSharpScriptService.Create<IDeviceApp>(deviceApp)], // what the globals' constructor takes
    CallTimeout = TimeSpan.FromSeconds(5),
});
runner.RunnerExited += (_, e) => { /* crash or kill: evaluate the script again */ };

using IPacketHandler handler = await runner.EvaluateAsync<IPacketHandler>(code);
Reading reading = await handler.HandleAsync(packet);

// High rates: one call per batch instead of one per packet.
await using var batcher = new ScriptBatcher<Packet, Reading>(packets => handler.HandleBatchAsync(packets));
Reading next = await batcher.SubmitAsync(packet);
```

A script ends with an object of the handler type; the app keeps calling it through a proxy that StreamJsonRpc
generates at build time. `samples/Darp.CodeEditor.Demo.ScriptApi` and `samples/Shared/PacketScriptRun.cs` are a
complete example; both demos run it with Run (F5).

### Script API

The app's script API assembly references StreamJsonRpc (2.26.150, the version the runner ships, or older) and has
`[assembly: ExportRpcContractProxies]`. It defines:

| Part | Declaration |
| --- | --- |
| Globals | A class with one public constructor that takes the host services it needs, by interface. |
| Host services | `[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)] partial interface`; the app implements them. |
| Handlers | `[RpcMarshalable, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)] partial interface ... : IDisposable`; scripts implement them. |
| Data | Records or classes with `[GenerateShape]`. |

The app and the runner exchange the identity of each script API assembly they use, including its module version ID,
so a stale copy next to the app fails at start instead of in a call.

Every call crosses processes, so the API follows these rules:

- Methods are async: `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` or `IAsyncEnumerable<T>`. A final
  `CancellationToken` is how a call learns that it was cancelled.
- Handler interfaces have no properties and no events; pass a callback interface instead.
- No generic methods.
- Data is copied, never shared: changing an argument changes the copy, and objects have no identity across calls.
- No streams.
- Script API assemblies, and what they use, need no shared framework but `Microsoft.NETCore.App`.
- Scripts must not write to the process's standard output directly. `Console.Out` goes to the runner's standard
  error, which the app receives through `CSharpScriptRunnerOptions.Output`.

Scripts run culture-invariant. The runner uses .NET's invariant globalization: the current culture is the invariant
one, so formatting and parsing behave the same on every machine; creating another culture, such as `de-DE`, throws
`CultureNotFoundException`; and localized resources are not loaded, only the neutral ones in the main assembly. This
keeps scripts deterministic and needs no ICU library on Linux. A host that wants cultures turns globalization on for
the runner:

```csharp
RunnerEnvironmentVariables = new Dictionary<string, string> { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "0" },
```

The runner then needs ICU (libicu) on Linux, like any .NET app that uses cultures, and does not start without it.
Culture names that .NET does not know, such as custom cultures, also need
`DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=0`.

### Failures

| What happened | The call throws | The runner |
| --- | --- | --- |
| The script does not compile | `ScriptCompilationException` with every diagnostic and its position | keeps running |
| Script code threw | `ScriptException` with the script's exception type, message and stack trace | keeps running; the handler keeps working |
| A call passed `CallTimeout` | `TimeoutException`; the runner is asked to cancel the call | is killed if the call has not ended `CancellationGracePeriod` later: `RunnerExited` with `Killed` |
| The runner did not take or compile a script within `CompileTimeout` | `TimeoutException` | is killed: `RunnerExited` with `Killed` |
| The runner crashed or was killed | `ConnectionLostException` for calls in flight, `ObjectDisposedException` for later calls on its handlers | `RunnerExited` with `Crashed`, the exit code and its last lines of standard error |
| The environment cannot load, or the script API next to the app is another build than the app's | `InvalidOperationException` from `StartAsync` (or `EvaluateAsync` for a handler type), naming both builds | is not started |

The next `EvaluateAsync` after an exit starts a new runner. Retrying calls is the app's decision: a call that already
reached a device is not undone by killing the runner. Compiling runs no script code and has its own
`CompileTimeout` instead of the call deadline.

### Process ownership

The runner ends with the app, even while a script is busy, and `DisposeAsync` kills the runner and the processes its
scripts started.

| Platform | Mechanism | Checked |
| --- | --- | --- |
| Linux | The runner arms `PR_SET_PDEATHSIG` (`SIGKILL`) before it reads the first request, and exits if its parent is no longer the app. The app starts runners from one thread that lives as long as the app, because the signal fires when the thread that started the child ends. | tests (JIT app), by hand (NativeAOT app) |
| Windows | The app puts the runner in a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` and holds its only handle. | no |
| macOS | The runner polls its parent process ID. | no |
| all | The runner exits at the end of its input. | by hand |

The runner inherits the app's environment without the variables that start with `DOTNET_`, `COMPlus_` or
`CORECLR_`. Through them .NET loads or attaches code at startup (startup hooks, profilers, diagnostic ports, extra
dependencies) or changes runtime settings, and they are meant for the app. Everything else passes through; apps set
variables for the runner, such as tracing settings, with `CSharpScriptRunnerOptions.RunnerEnvironmentVariables`.

On Windows the runner is started normally and assigned to the job right after, not created suspended. Before the app's
first request, which the app sends after the assignment, the runner runs only its own startup and starts no process;
without those variables nothing else can run in it. An app that dies in between closes the runner's input, which ends
it. A suspended start would need its own `CreateProcess` code for pipes and handle inheritance instead of
`Process.Start`, and would still let the app's profilers and hooks into the runner. Code injected machine-wide, for
example through `AppInit_DLLs`, is out of scope. If the job cannot be assigned, starting fails.

Processes that scripts start survive a runner crash or the app's death on Linux, because the death signal is not
inherited; apps that let scripts start processes need a cgroup or a policy against it. The runner is not a security
sandbox: scripts run with the app's user rights.

### Packaging

The runtime is shared by every helper, so it ships once:

```
MyApp(.exe)
MyApp.ScriptApi.dll, StreamJsonRpc.dll, PolyType.dll   what scripts compile against; NativeAOT apps mark it
helpers/
  dotnet/                       the .NET runtime, laid out as an installed one:
    host/fxr/<version>/         hostfxr
    shared/Microsoft.NETCore.App/<version>/
  csharp-runner/                framework-dependent and ReadyToRun; its app host looks only in ../dotnet
  csharp-server/                later: the language server, published the same way
```

| | Shared runtime (chosen) | Self-contained helpers |
| --- | --- | --- |
| Runner alone, Linux x64 | 78 MiB runtime + 36 MiB runner (33 + 15 MiB gzipped) | about the same |
| Runner and language server | the runtime once | the runtime twice, about 78 MiB more |
| Runtime updates | one directory | per helper |
| Launch | the runner's app host, `AppHostRelativeDotNet=../dotnet` | the helper's app host |

The runtime comes from the `Microsoft.NETCore.App.Runtime.<rid>` package, so no SDK or runtime download is needed on
the build machine beyond NuGet. Import the targets into the app project:

```xml
<Import Project="path/to/src/Darp.CodeEditor.CSharp.Execution/build/Darp.CodeEditor.CSharp.Execution.targets" />
<ItemGroup>
  <!-- NativeAOT: publish copies the DLLs of marked references next to the app, with StreamJsonRpc and PolyType. -->
  <ProjectReference Include="..\MyApp.ScriptApi\MyApp.ScriptApi.csproj" CSharpScriptReference="true" />
  <PackageReference Include="MyApp.Devices" CSharpScriptReference="true" />
</ItemGroup>
```

Mark the references whose types scripts use. Publish also ships what the marked assemblies need at run time, found
by their assembly references among the app's own assemblies, with their satellite assemblies; the runner loads those
from the app's directory. Assemblies the runner ships itself, with the same identity, stay out. JIT apps have all their
DLLs next to them already.

The runner has `Microsoft.NETCore.App` only. When a marked reference, or something it uses, needs another shared
framework, such as `Microsoft.AspNetCore.App`, the build fails and names the reference and the framework.

- `dotnet build`: the runner's build output goes to `helpers/csharp-runner`, and it runs on the runtime that runs the
  app, so `dotnet run` works without publishing.
- `dotnet publish -r <rid>`: the runner, ReadyToRun for the app's runtime identifier, and the runtime go to
  `helpers/`.

A NuGet package would carry the same targets in `buildTransitive/` and the runner's build output instead of the
project reference; it is not built yet.

### Benchmark

```bash
dotnet run --project benchmarks/Darp.CodeEditor.CSharp.Execution.Benchmark -c Release -- --label jit-build-output
dotnet publish benchmarks/Darp.CodeEditor.CSharp.Execution.Benchmark -c Release -r linux-x64 -p:PublishAot=true -o artifacts/bench-aot
artifacts/bench-aot/Darp.CodeEditor.CSharp.Execution.Benchmark --label nativeaot-published
```

It starts five fresh runners and measures the time from starting the runner process to the first handler result,
then warm calls on one runner with 100-byte payloads. Reports go to `measurements/csharp-runner/`, which git ignores.

Results on 2026-11-02, Linux x64, 8 cores, .NET 10.0.13, on a shared machine with a load of about 5 (medians; warm
values are p50 per call or per batch):

| | NativeAOT app, published | JIT app, published | JIT app, build output |
| --- | ---: | ---: | ---: |
| runner start → first handler result | 363 ms | 336 ms | 651 ms |
| runner RSS after the first result | 125 MiB | 126 MiB | 120 MiB |
| small call | 38 µs (21.6k calls/s) | 29 µs (28.6k calls/s) | 27 µs (29.1k calls/s) |
| call with a callback into the app | 80 µs | 53 µs | 65 µs |
| batch of 100 | 79 µs (1.18M packets/s) | 54 µs (1.73M packets/s) | 64 µs (1.46M packets/s) |
| `ScriptBatcher`, 100k single packets | 1.20M packets/s | 1.41M packets/s | 1.21M packets/s |

Published runners are ReadyToRun on the bundled runtime; the build output is IL on the installed runtime.
ReadyToRun halves the time to the first result.

## Luau

Luau scripts get completion, hover, signature help, diagnostics and semantic colors from
[luau-lsp](https://github.com/JohnnyMorganz/luau-lsp), set up like C#: an out-of-process language server behind the
editor's LSP client, one server for all Luau editors of an app, restarted when it crashes. The host describes what its
scripts see:

```csharp
var environment = new LuauEnvironment
{
    Definitions = [new LuauDefinitions("app", Path.Combine(AppContext.BaseDirectory, "app.d.luau"))],
    DocumentationFiles = [Path.Combine(AppContext.BaseDirectory, "app.docs.json")],
    Mode = LuauMode.Strict,
};
LspLanguageService luau = LuauLanguageServer.CreateService(environment);
LuauLanguage.Configure(editor);     // the Luau grammar and editing helpers
editor.LanguageService = luau;      // opens the document; the first open document starts luau-lsp
await luau.DisposeAsync();          // shutdown and exit, then the process ends
```

| Part | Meaning |
| --- | --- |
| `Definitions` | Luau definition files: the app's globals, functions and types, the equivalent of C#'s globals. Each is loaded under a name, here `@app`. |
| `DocumentationFiles` | JSON documentation for the declarations, keyed by symbol: `@app/global/log`, `@app/global/limits.maxTemperature`, `@app/globaltype/Device.setLed`, and `@app/globaltype/Device.setLed/param/1` for a parameter (`self` is 0; `param/0` for functions). Completion, hover and signature help show them. `---` comments in a definition file document its declarations too. |
| `Mode` | `Strict`, `Nonstrict` (Luau's default) or `NoCheck`, for scripts without a `--!strict`, `--!nonstrict` or `--!nocheck` comment; passed to luau-lsp as its base `.luaurc`. |
| `Settings` | Further luau-lsp settings, merged over the package's: the contents of the `luau-lsp` section, such as `{ "types": { "disabledGlobals": ["os.exit"] } }`. |

A definition file in current Luau syntax (`declare class` is now `declare extern type ... with`):

```luau
declare function log(message: string): ()

declare extern type Device with
    name: string
    function readTemperature(self): number
    function setLed(self, on: boolean): ()
end

declare device: Device
declare limits: { maxTemperature: number }
```

luau-lsp gets the definitions and documentation as `--definitions` and `--docs` and the mode as `--base-luaurc`; it
reads them at startup, so a changed environment needs a new service. Its settings go out as answers to
`workspace/configuration`: the standard platform instead of luau-lsp's default, Roblox (no Roblox globals, types or
sourcemaps), and completions that insert names without parentheses, since the editor has no snippets and `(` brings
signature help. `initializationOptions` of luau-lsp 1.70.1 carry only Luau flags, which
`LuauLanguageServerOptions.Arguments` sets as `--flag:Name=Value`.

Documents live in a luau-lsp workspace folder: `LuauLanguageServerOptions.WorkspaceDirectory`, where scripts can
`require` each other and `.luaurc` files apply, or by default a folder that does not exist, so each script stands
alone and document ids are names such as `script.luau`. Their URIs are written the way luau-lsp writes them back
(`file:///c%3A/...`), so its diagnostics find their documents. luau-lsp classifies whole documents only; the service
asks once per version and cuts out the visible range.

### luau-lsp binaries

`Darp.CodeEditor.Luau.targets` puts luau-lsp 1.70.1 next to the app on build and publish, for the app's runtime
identifier (or the SDK's portable one when the build has none):

| Runtime identifier | Release asset |
| --- | --- |
| `win-x64` | `luau-lsp-win64.zip` |
| `win-arm64` | the same x64 build, which Windows on Arm runs under emulation: luau-lsp has no Windows arm64 build |
| `linux-x64` | `luau-lsp-linux-x86_64.zip` |
| `linux-arm64` | `luau-lsp-linux-arm64.zip` |
| `osx-x64`, `osx-arm64` | `luau-lsp-macos.zip`, one universal binary |

Linux builds need glibc. Other identifiers, such as `linux-musl-x64` or `freebsd-x64`, fail the build with a message.
The archive is downloaded from the GitHub release once into `.tools/luau-lsp/` and checked on every build against its
SHA-256, which the targets pin per asset; a download that does not match is deleted and fails the build. Builds unpack
it into `obj/` and copy the executable to `helpers/luau-lsp/`, with the base configurations; the grammar goes to
`Grammars/`.

| Property | Effect |
| --- | --- |
| `LuauLspArchive` | The release's zip on disk, for offline builds; it is checked like a download. |
| `LuauLspCacheDirectory` | Where downloads are kept, such as a shared cache. A filled cache needs no network. |
| `LuauLspDownloadUrl` | A mirror of the release's zip. |
| `LuauLspExecutable` | Another luau-lsp to ship, such as a native Windows arm64 build; it is not checked. |
| `IncludeLuauLsp` | `false`: no luau-lsp; the app brings its own (`LuauLanguageServerOptions.ServerPath`). |

The download is a stand-in for development: the NuGet package is to carry the binaries per runtime identifier, laid out
like the other helpers. `LuauLspExecutable` and `LuauLanguageServerOptions.ServerPath` are where that plugs in.

In the NativeAOT demo on Linux x64 (2026-11-03, 8 cores, shared machine), luau-lsp answered `initialize` 87 ms after
process start, and the Luau editor showed semantic colors and diagnostics 52 ms after it was created. Warm completion
through the editor took 1 ms at p50 and p95 for the word list (55 items) and 1 / 3 ms for members; luau-lsp used
12–13 MiB.

## Third-party content

`src/Darp.CodeEditor.Luau/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.

luau-lsp (MIT) is downloaded from [its releases](https://github.com/JohnnyMorganz/luau-lsp/releases) at build time and
ships next to apps that use `Darp.CodeEditor.Luau`.

The C# script language server uses Roslyn (MIT). The script runner uses
[StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) (MIT), Roslyn and the .NET runtime (MIT), which published
apps ship in `helpers/dotnet`.
