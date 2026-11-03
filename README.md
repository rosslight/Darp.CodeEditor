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

Darp.CodeEditor.CSharp.Execution  NativeAOT-compatible; no Roslyn; StreamJsonRpc
  CSharpScriptRunner              runs scripts in the runner process, returns their handlers as typed proxies
  ScriptBatcher                   sends single items to a handler in batches
  build/*.targets                 puts the runner and its .NET runtime next to the app

Darp.CodeEditor.CSharp.Runner     JIT only, framework-dependent helper: compiles and runs scripts, RPC over stdio
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
| `src/Darp.CodeEditor.CSharp.Execution` | `IsAotCompatible`: runs scripts in the runner; the MSBuild targets that ship it. |
| `src/Darp.CodeEditor.CSharp.Runner` | The runner: a helper process that compiles and runs scripts. |
| `samples/Darp.CodeEditor.Demo` | JIT desktop app: C# script with completion, diagnostics, semantic colors, quick info, signature help; Luau and JSON with highlighting; Run (F5) runs packet.csx in the script runner. |
| `samples/Darp.CodeEditor.Demo.Aot` | `PublishAot=true`, without Roslyn: Luau, JSON and C# highlighting, search, a small AOT-safe JSON language service (word completion, parse errors), and Run (F5) for packet.csx through the script runner. |
| `samples/Darp.CodeEditor.Demo.ScriptApi` | The demos' script API: globals, a service the app implements, the handler scripts return. |
| `samples/Shared` | Sample documents, measurement scenarios, the Run action and the Luau grammar shared by both demos. |
| `tests/Darp.CodeEditor.Tests` | Contract tests for the C# service and the completion matcher. |
| `tests/Darp.CodeEditor.CSharp.Server.Tests` | Compiler-parity tests against `CSharpScript`, and tests that drive the server process over stdio. |
| `tests/Darp.CodeEditor.CSharp.TestGlobals` | A globals contract assembly for the tests, including the demo's `ScriptGlobals`. |
| `tests/Shared` | A small LSP client over stdio, used by the tests and the benchmark. |
| `tests/Darp.CodeEditor.CSharp.Execution.Tests` | Tests of the runner: handlers, callbacks, batching, diagnostics, faults, process ownership, NativeAOT publish. |
| `tests/Darp.CodeEditor.CSharp.Execution.TestApi` | A script API assembly for the runner tests and benchmark. |
| `tests/Darp.CodeEditor.CSharp.Execution.TestHost` | A small app that runs a script; the tests start it and publish it with NativeAOT. |
| `benchmarks/Darp.CodeEditor.CSharp.Server.Benchmark` | Server against in-process service: startup, completion latency, memory, sizes. |
| `benchmarks/Darp.CodeEditor.CSharp.Execution.Benchmark` | The runner from a JIT or NativeAOT app: cold start to the first result, warm calls, batches. |

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
| `--run-script` | Runs packet.csx through the script runner once the window opens, prints the output and exits. |
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

Every call crosses processes, so the API follows these rules:

- Methods are async: `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` or `IAsyncEnumerable<T>`. A final
  `CancellationToken` is how a call learns that it was cancelled.
- Handler interfaces have no properties and no events; pass a callback interface instead.
- No generic methods.
- Data is copied, never shared: changing an argument changes the copy, and objects have no identity across calls.
- No streams.
- Scripts must not write to the process's standard output directly. `Console.Out` goes to the runner's standard
  error, which the app receives through `CSharpScriptRunnerOptions.Output`.

### Failures

| What happened | The call throws | The runner |
| --- | --- | --- |
| The script does not compile | `ScriptCompilationException` with every diagnostic and its position | keeps running |
| Script code threw | `ScriptException` with the script's exception type, message and stack trace | keeps running; the handler keeps working |
| A call passed `CallTimeout` | `TimeoutException`; the runner is asked to cancel the call | is killed if the call has not ended `CancellationGracePeriod` later: `RunnerExited` with `Killed` |
| The runner crashed or was killed | `ConnectionLostException` for calls in flight, `ObjectDisposedException` for later calls on its handlers | `RunnerExited` with `Crashed`, the exit code and its last lines of standard error |
| The environment cannot load | `InvalidOperationException` from `StartAsync` | is not started |

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

On Windows the runner is started normally and assigned to the job right after, not created suspended: it runs no
script and starts no process before the app's first request, which the app sends after the assignment, and an app
that dies in between closes the runner's input. If the job cannot be assigned, starting fails.

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
by their assembly references among the app's own assemblies, and the runner loads those from the app's directory. JIT
apps have all their DLLs next to them already.

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

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.

The C# script language server uses [EmmyLua.LanguageServer.Framework](https://github.com/CppCXY/LanguageServer.Framework)
(MIT) and Roslyn (MIT). The script runner uses [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc) (MIT),
Roslyn and the .NET runtime (MIT), which published apps ship in `helpers/dotnet`.
