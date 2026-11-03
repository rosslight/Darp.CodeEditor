# Darp.CodeEditor (spike)

A prototype of a code editor library for Avalonia desktop apps. It exists to prove or disprove one architecture with
measured numbers; it is not the product. "Darp.CodeEditor" is a working name.

## Architecture

```
Darp.CodeEditor            NativeAOT-compatible; no Roslyn, no MEF
  CodeEditor               AvaloniaEdit TextEditor + TextMate highlighting + search panel
  Highlighting/            TextMate tokenization on the thread pool; rendering reads a per-line cache
  LanguageServices/        ILanguageService: async only, snapshot + version + CancellationToken, plain DTOs
  LanguageFeatures         calls the service via Task.Run, debounces, drops or maps forward stale results
  Completion/              one fetch per session, local ordinal filtering, lazy descriptions, sync commit
  Markers/                 semantic colors and diagnostic squiggles, read from segment caches during render
  Instrumentation/         UiStallProbe: UI-thread latency and GC pauses per phase

Darp.CodeEditor.CSharp     JIT only; Roslyn Workspaces + Features (no EditorFeatures, no VS editor, no VS-MEF)
  CSharpScriptLanguageService   script submissions with a globals type, created and warmed up on the thread pool

Darp.CodeEditor.CSharp.Hosting    NativeAOT-compatible; no Roslyn
  CSharpScriptEnvironment         globals contract assembly + type name, references, imports, compiler options
  CSharpScriptInitializationOptions   the versioned "csharpScript" section of LSP initializationOptions

Darp.CodeEditor.CSharp.Scripting  JIT only; Roslyn Scripting
  ResolvedScriptEnvironment       reference files, parse and compilation options of an environment;
                                  ScriptOptions for hosts, the same options for the server

Darp.CodeEditor.CSharp.Server     JIT only, framework-dependent .NET 10 console app: LSP over stdio
  EmmyLua.LanguageServer.Framework  protocol layer
  ScriptWorkspace                 one script submission per document, globals type loaded as metadata only
```

```
Darp.CodeEditor.Lsp        NativeAOT-compatible; only the shared framework
  JsonRpcConnection        JSON-RPC 2.0 with Content-Length framing, for both ends of a connection
  LanguageServer           the server lifecycle: initialize, shutdown, exit
  Protocol/                source-generated DTOs for the subset of LSP the editor and its servers use
```
The C# script language server is the path forward: every language goes through an out-of-process LSP server, for JIT
and NativeAOT hosts alike. `Darp.CodeEditor.CSharp` stays as the in-process baseline until the editor's LSP client
exists.

Rules the code follows:

1. The editor works fully without a language service (typing, TextMate colors, search). Setting
   `CodeEditor.LanguageService` later switches on completion, diagnostics, semantic colors, quick info and signature help.
2. The boundary is async only. Rendering and layout read only caches that results fill; they never call the service.
3. Every request carries a document version and a cancellation token. A keystroke cancels document-wide requests;
   results for an older version are dropped, and cached results move with the text (AvaloniaEdit segments) until new
   ones arrive. Completion results map forward while the user keeps typing the same word.
4. Completion fetches its list once per session off the UI thread, filters locally per keystroke, loads descriptions
   lazily, and commits text the service already computed.
5. No `.Result`, `.Wait()` or `GetAwaiter().GetResult()` on the UI thread.

## Layout

| Path | What |
| --- | --- |
| `src/Darp.CodeEditor` | The editor library (`IsAotCompatible`). |
| `src/Darp.CodeEditor.CSharp` | C# script language service on Roslyn (JIT only), used in-process by the JIT demo. |
| `src/Darp.CodeEditor.CSharp.Hosting` | `IsAotCompatible`: the script environment a host describes, and its `initializationOptions` section. |
| `src/Darp.CodeEditor.CSharp.Scripting` | Turns an environment into `ScriptOptions` for the host and compiler options for the server. |
| `src/Darp.CodeEditor.CSharp.Server` | The C# script language server (LSP over stdio). |
| `samples/Darp.CodeEditor.Demo` | JIT desktop app: C# script with completion, diagnostics, semantic colors, quick info, signature help; Luau and JSON with highlighting. |
| `samples/Darp.CodeEditor.Demo.Aot` | `PublishAot=true`, references only `Darp.CodeEditor`: Luau, JSON and C# highlighting, search, and a small AOT-safe JSON language service (word completion, parse errors). |
| `samples/Shared` | Sample documents, measurement scenarios and the Luau grammar shared by both demos. |
| `tests/Darp.CodeEditor.Tests` | Contract tests for the C# service and the completion matcher. |
| `src/Darp.CodeEditor.Lsp` | JSON-RPC and LSP layer for language servers and the editor's LSP client (`IsAotCompatible`). |
| `tests/Darp.CodeEditor.Lsp.Tests` | Tests of the LSP layer, a toy server and client, and an interop test against luau-lsp. |
| `tests/Darp.CodeEditor.Lsp.AotSmoke` | NativeAOT build of the toy server and client, and of the luau-lsp check. |
| `tests/Darp.CodeEditor.CSharp.Server.Tests` | Compiler-parity tests against `CSharpScript`, and tests that drive the server process over stdio. |
| `tests/Darp.CodeEditor.CSharp.TestGlobals` | A globals contract assembly for the tests, including the demo's `ScriptGlobals`. |
| `tests/Shared` | A small LSP client over stdio, used by the tests and the benchmark. |
| `benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark` | Server against in-process service: startup, completion latency, memory, sizes. |

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
```

The LSP layer has its own tests. The luau-lsp interop test is skipped until the script downloaded luau-lsp to
`.tools/`; the NativeAOT check runs it too.

```bash
tests/Darp.CodeEditor.Lsp.Tests/fetch-luau-lsp.sh
dotnet test --project tests/Darp.CodeEditor.Lsp.Tests

# Fails on any trim or AOT warning
dotnet publish tests/Darp.CodeEditor.Lsp.AotSmoke -c Release -r linux-x64
tests/Darp.CodeEditor.Lsp.AotSmoke/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Lsp.AotSmoke
```

On a Linux machine without a display, run the demos on a virtual X server (for example `xvfb-run` or any X server
with `DISPLAY` set).

Command line flags of both demos:

| Flag | Effect |
| --- | --- |
| `--smoke` | Opens the window, waits for the first highlighted frame, checks search and replace, prints timings and exits. |
| `--bench` | `--smoke` plus the full measurements: completion latency, typing bursts, large file, long line. |
| `--out <file>` | Also writes the results to a file. |
| `--hold` | Keeps the window open after `--smoke` or `--bench`. |
| `--scene` | Prepares a scene for a screenshot and stays open. |
| `--overlay-popups` | Draws popups inside the window instead of native windows (Avalonia platform option). |
| `--skip-highlighting-bench` | Skips the highlighting scenarios of `--bench`. |
| `--no-language-service` | JIT demo only: starts without the C# service, to attribute startup stalls. |
| `--low-latency-gc` | JIT demo only: sets `GCLatencyMode.SustainedLowLatency`. |

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
process named by `processId` in `initialize` ends.

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
| `initialize`, `initialized`, `shutdown`, `exit` | Requests before `initialize` fail with `ServerNotInitialized`, requests after `shutdown` with `InvalidRequest`. Exit code 0 after `shutdown`, else 1. `initialize` answers right away; the environment loads in the background and requests wait for it. |
| `didOpen`, `didChange` (incremental), `didClose` | Versions are kept per document; a change for the current or an older version is ignored. Each script is its own submission. |
| `$/cancelRequest` | Not effective yet: the protocol framework reads every cancel id as 0. It works once the server moves to `Darp.CodeEditor.Lsp`. |
| `completion`, `completionItem/resolve` | Edits from Roslyn's `CompletionService.GetChangeAsync`, per-item commit characters from Roslyn's rules, `itemDefaults` when the client supports them. Lists of more than 1,000 items, such as a word list after its first letter, keep the 1,000 items that match the typed text best (by the editor's rules: prefix, word parts, substring) and are marked `isIncomplete`, so the client asks again for each further letter. Types from namespaces that are not imported insert their name; resolve adds the `using` as `additionalTextEdits` (clients that cannot resolve those lazily do not get these items). Snippets such as `for` expand on resolve, for clients that list `textEdit` in `resolveSupport`, and are left out for others. `data` is opaque. |
| `hover` | Markdown: the signature as C#, then the documentation. |
| `signatureHelp` | Every accessible overload; the best one for the typed arguments, or the one the call binds to; the active parameter, also for named arguments and `params`; parameter documentation; label offsets. |
| `publishDiagnostics` | Compiler diagnostics only, 200 ms after the last change, tagged with the document version. |
| `semanticTokens/range` | Roslyn's classifier. Token types use LSP's names where one exists (`namespace`, `class`, `method`, `property`, `variable`, `keyword`, ...) and C# names otherwise (`delegate`, `extensionMethod`, `field`, `constant`, `label`); modifier `static`. |

A new document version cancels hover, signature help and semantic tokens for the old one (`RequestCancelled`) and
drops pending diagnostics. Completion requests are not cancelled by new versions: the client can keep filtering the
list while the user types the same word. Closing or reopening the document cancels them.

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

Results on 2026-11-02, Linux x64, 8 cores, .NET 10.0.13, 5 cold starts per cell. The machine ran other agents (load
average 2–4), so the numbers are indicative. Medians and percentiles; one-at-a-time values over 90 lists per cell.

| | in-process | server | server, ReadyToRun | R2R, unfiltered | R2R, filtered, no max |
| --- | ---: | ---: | ---: | ---: | ---: |
| process start → first member list (9 items) | 1416 ms | 1104 ms | 703 ms | 701 ms | 698 ms |
| process start → first word list | 1354 ms | 1003 ms | 636 ms | 634 ms | 628 ms |
| word list items after "D" | 3282 | 1000 | 1000 | 3288 | 1431 |
| one at a time: member list p50 / p95 | 2.7 / 8.9 ms | 3.7 / 9.1 ms | 4.9 / 7.3 ms | 4.3 / 8.0 ms | 4.8 / 8.2 ms |
| one at a time: word list p50 / p95 | 7.1 / 21.0 ms | 12.7 / 26.6 ms | 15.2 / 20.4 ms | 17.8 / 25.6 ms | 16.9 / 23.0 ms |
| one at a time: asked again per further letter p50 / p95 | – | 8.4 / 18.3 ms | 11.7 / 16.8 ms | – | 12.0 / 17.9 ms |
| typing (40 ms): fresh completion p50 / p95 | 3.6 / 8.8 ms | 4.6 / 10.7 ms | 6.7 / 14.1 ms | 7.5 / 13.7 ms | 7.2 / 15.7 ms |
| typing: last keystroke → fresh word list answered | 4 ms | 6 ms | 7 ms | 9 ms | 7 ms |
| stress (10 ms): cancelled by the client / answered in full anyway | 17 / 1 | 105 / 64 | 54 / 48 | 147 / 59 | 40 / 30 |
| stress: last keystroke → fresh word list answered | 4 ms | 5 ms | 6 ms | 9 ms | 7 ms |
| RSS after typing | 248 MiB¹ | 281 MiB | 292 MiB | 298 MiB | 293 MiB |
| word list response | – | 127 KiB | 127 KiB | 414 KiB | 185 KiB |
| published files / zipped | – | 27 / 10 MiB | 68 / 29 MiB | 68 / 29 MiB | 68 / 29 MiB |

¹ Without the demo's UI. In-process, the service warms up while it is created, before the first request.

- Server-side filtering brings the warm word list's p95 to the in-process value; its median stays about twice the
  in-process one, which needs no protocol. Filtering without a maximum gives less (p95 23 ms); a maximum of 300 gives
  13.0 / 17.3 ms. Of a word list, Roslyn's completion takes about 6–10 ms in both.
- At 40 ms per keystroke nothing piles up: fresh answers stay within milliseconds of one request at a time. At 10 ms,
  most requests the client cancels are still answered in full, because `$/cancelRequest` has no effect yet (see
  Protocol); the backlog still clears within a few milliseconds of the last keystroke.
- Warm-up: an earlier run measured the first request after a 2 s idle at 37 / 44 ms (member / word) with warm-up and
  about 195 ms without; when the first request comes right after start, warm-up costs it up to about 50 ms.

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.

The C# script language server uses [EmmyLua.LanguageServer.Framework](https://github.com/CppCXY/LanguageServer.Framework)
(MIT) and Roslyn (MIT).
