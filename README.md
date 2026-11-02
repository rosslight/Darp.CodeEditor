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

Other arguments, such as `--stdio`, are ignored.

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
| `globals` | The managed contract assembly and the full name of the globals type. NativeAOT hosts ship the assembly as a file; a reference assembly works too. |
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
| `didOpen`, `didChange` (incremental), `didClose` | Versions are kept per document. Each script is its own submission. |
| `$/cancelRequest` | Answered with `RequestCancelled`. |
| `completion`, `completionItem/resolve` | Edits from Roslyn's `CompletionService.GetChangeAsync`, per-item commit characters from Roslyn's rules, `itemDefaults` when the client supports them. Types from namespaces that are not imported insert their name; resolve adds the `using` as `additionalTextEdits` (clients that cannot resolve those lazily do not get these items). Snippets such as `for` expand on resolve, for clients that list `textEdit` in `resolveSupport`, and are left out for others. `data` is opaque. |
| `hover` | Markdown: the signature as C#, then the documentation. |
| `signatureHelp` | Every accessible overload; the best one for the typed arguments, or the one the call binds to; the active parameter, also for named arguments and `params`; parameter documentation; label offsets. |
| `publishDiagnostics` | Compiler diagnostics only, 200 ms after the last change, tagged with the document version. |
| `semanticTokens/range` | Roslyn's classifier. Token types use LSP's names where one exists (`namespace`, `class`, `method`, `property`, `variable`, `keyword`, ...) and C# names otherwise (`delegate`, `extensionMethod`, `field`, `constant`, `label`); modifier `static`. |

A new document version cancels hover, signature help and semantic tokens for the old one (`RequestCancelled`) and
drops pending diagnostics. Completion requests are not cancelled by new versions: the client can keep filtering the
list while the user types the same word.

### Benchmark

```bash
dotnet run --project benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark -c Release -- --runs 5
```

It publishes the server framework-dependent and with ReadyToRun into `measurements/csharp-server/`, then starts each
variant, and the in-process `CSharpScriptLanguageService` in a child process, once per run and per first request. Each
run measures:

- process start → the first non-empty member list (`Device.`) or word list (`D` at the start of a line), on the
  demo's sample script with the demo's `ScriptGlobals`, references and imports;
- warm latency while typing: 20 rounds of `\nD` (word list), `evice`, `.` (member list), `ReadTemperature();`, 40 ms
  between chunks, the first two rounds skipped; server latency is request sent → response parsed by the client;
- RSS one second after typing, response sizes, and the size of the published files.

It writes `report.md` and `results.json` to `measurements/csharp-server/`, which git ignores.

Results on 2026-11-02, Linux x64, 8 cores, .NET 10.0.13, 5 cold starts per cell (medians; warm values over 90
completions):

| | in-process | server | server, ReadyToRun |
| --- | ---: | ---: | ---: |
| process start → first member list (9 items) | 1341 ms | 1123 ms | 729 ms |
| process start → first word list (~3.3k items) | 1549 ms | 1043 ms | 650 ms |
| first completion request after 2 s idle: member / word | 35 / 111 ms¹ | – | 37 / 44 ms |
| warm member list p50 / p95 | 4.8 / 12.3 ms | 6.3 / 11.2 ms | 5.9 / 8.8 ms |
| warm word list p50 / p95 | 11.4 / 20.6 ms | 22.4 / 36.5 ms | 23.1 / 30.7 ms |
| RSS after typing | 216 MiB² | 248 MiB | 265 MiB |
| word list response | – | 414 KiB | 414 KiB |
| published files / zipped | – | 27 / 10 MiB | 68 / 29 MiB |

¹ In-process, the service warms up while it is created, before the first request. ² Without the demo's UI.

Without warm-up (`--no-warm-up`), the ReadyToRun server answers its first request after a 2 s idle in about 195 ms;
when the first request comes right after start, warm-up costs it up to about 50 ms. Of the warm word list, Roslyn's
completion takes about 6–10 ms; the rest is converting and writing 3,300 items and the framework copying the response
twice before it is sent.

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.

The C# script language server uses [EmmyLua.LanguageServer.Framework](https://github.com/CppCXY/LanguageServer.Framework)
(MIT) and Roslyn (MIT).
