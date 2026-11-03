# Review guide

Status as of 2026-11-03. This guide is for reviewing and trying the editor as it stands on `main`. The design and every
decision behind it are in [design.md](design.md); the README has the commands and the details per component.

## What is built

| Piece | Projects | What it does |
| --- | --- | --- |
| Editor | `src/Darp.CodeEditor` | AvaloniaEdit editor, NativeAOT-safe: background TextMate highlighting with first-frame colors, in-window completion, hover and signature help, search and replace, editing helpers, light and dark themes. Talks to languages only through `ILanguageService`; `LspLanguageService` implements it over LSP. |
| LSP layer | `src/Darp.CodeEditor.Lsp` | JSON-RPC and LSP for both ends, no dependencies: framing, dispatch order, cancellation, lifecycle, an ordered background write loop, `LanguageClient`, restarts with backoff, `ChildProcess` (helper processes that end with the app). |
| C# script server | `src/Darp.CodeEditor.CSharp.Server`, `.Scripting`, `.Hosting` | LSP server on Roslyn Workspaces/Features for scripts with a host-defined globals type: completion with resolve, hover, signature help, compiler diagnostics, semantic tokens. Never runs script code. |
| Script runner | `src/Darp.CodeEditor.CSharp.Runner`, `.Execution` | Compiles and runs scripts in a helper process: typed handlers, callbacks into the app, batching, deadlines, crash restart. |
| C# package | `src/Darp.CodeEditor.CSharp` | What an app references for C#: the environment, the language service, the runner API, and the server and runner, built for the app's platform when it publishes. |
| Luau | `src/Darp.CodeEditor.Luau` | Luau through luau-lsp 1.70.1, with the app's API from definition and documentation files. |
| Demos | `samples/Darp.CodeEditor.Demo` (JIT), `samples/Darp.CodeEditor.Demo.Aot` (NativeAOT) | C# with all features through the server, Run (F5) through the runner, Luau through luau-lsp, JSON, themes. |

Packages: `Darp.CodeEditor`, `Darp.CodeEditor.Lsp`, `Darp.CodeEditor.CSharp` and `Darp.CodeEditor.Luau`. The release
PR (0.1.0) is open and not merged: merging it publishes them to the feed.

## Try it

Requires the .NET SDK that `global.json` pins (10.0.402) and, for NativeAOT, the platform's native toolchain (on
Windows, Visual Studio with the C++ workload).

```bash
dotnet run --project samples/Darp.CodeEditor.Demo -c Release                       # JIT demo
dotnet publish samples/Darp.CodeEditor.Demo.Aot -c Release -r linux-x64             # NativeAOT demo (win-x64 on Windows)
samples/Darp.CodeEditor.Demo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Demo.Aot
dotnet test                                                                          # all tests
```

On Windows, if NativeAOT publishing fails with MSB3073, put the folder of `vswhere.exe` (the Visual Studio Installer
folder) on `PATH` for that shell.

In the demos:

- `script.csx`: Ctrl+Space completes, `Device.` lists members, hovering shows docs, `Device.SetLed(` shows signature
  help, and line 27 has an error. Choosing a type of a namespace that is not imported, such as `StringBuilder`, adds the
  `using` in the same undo step.
- Run packet.csx (F5) compiles the packet script in the runner and calls its handler.
- `script.luau`: completion, hover and diagnostics from luau-lsp, including the demo's own API (`device`, `limits`,
  `log`).
- Light / dark switches the theme; Ctrl+F searches.
- `--bench` measures startup, typing, completion and highlighting and prints a report (README, Run).

## Use it in an app

```xml
<PropertyGroup>
  <PublishReadyToRun>true</PublishReadyToRun> <!-- also for NativeAOT apps: it fetches the runtime for the helpers -->
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Darp.CodeEditor.CSharp" />
  <PackageReference Include="Darp.CodeEditor.Luau" />
  <!-- Assemblies scripts may use; publish copies them, with what they need, next to the app. -->
  <ProjectReference Include="..\MyApp.ScriptApi\MyApp.ScriptApi.csproj" CSharpScriptReference="true" />
</ItemGroup>
```

```csharp
var environment = new CSharpScriptEnvironment
{
    Globals = new CSharpScriptGlobals(Path.Combine(AppContext.BaseDirectory, "MyApp.ScriptApi.dll"), "MyApp.ScriptApi.Globals"),
    References = [Path.Combine(AppContext.BaseDirectory, "MyApp.ScriptApi.dll")],
    Imports = ["System", "MyApp.ScriptApi"],
};

// Editing: one server per app, shared by all C# editors.
LspLanguageService csharp = CSharpLanguageServer.CreateService(environment);
_ = csharp.PrewarmAsync();                 // optional: start before the first editor opens
editor.LanguageService = csharp;

// Running: the script returns a handler; the app calls it, and the script calls the app's services.
await using var runner = await CSharpScriptRunner.StartAsync(new CSharpScriptRunnerOptions
{
    Environment = environment,
    Services = [CSharpScriptService.Create<IDeviceApp>(app)],
});
IPacketHandler handler = await runner.EvaluateAsync<IPacketHandler>(code);
```

Script API interfaces are async only, with no events or properties on returned handlers and no generic methods; data is
copied (README, Script API).

## Where to look

| Area | Start with | Then |
| --- | --- | --- |
| UI thread rules | `src/Darp.CodeEditor/LanguageFeatures.cs`, `LanguageServices/ILanguageService.cs` | README, Architecture: the rules every feature follows |
| Highlighting | `src/Darp.CodeEditor/Highlighting/BackgroundTextMateHighlighter.cs` | first-frame pass, background pass, `TokenKindBefore` for editing helpers |
| Completion | `src/Darp.CodeEditor/Completion/CompletionController.cs` | `CompletionPopup.cs`, `SignatureHelpController.cs`, late resolve edits and undo |
| LSP client | `src/Darp.CodeEditor/LanguageServices/Lsp/LspLanguageService.cs` | `HangTimeout.cs`, document sync, stale results |
| LSP layer | `src/Darp.CodeEditor.Lsp/JsonRpcConnection.cs` | `LanguageServer.cs`, `LanguageClient.cs`, `Hosting/Supervisor.cs`, `Hosting/ChildProcess.cs` |
| C# server | `src/Darp.CodeEditor.CSharp.Server/ScriptSession.cs` | `CompletionHandler.cs`, `DocumentWork.cs`; `Darp.CodeEditor.CSharp.Scripting/ResolvedScriptEnvironment.cs` |
| Runner | `src/Darp.CodeEditor.CSharp.Execution/CSharpScriptRunner.cs` | `Hosting/RunnerConnection.cs`, `src/Darp.CodeEditor.CSharp.Runner` |
| Packaging | `src/Darp.CodeEditor.CSharp/buildTransitive/Darp.CodeEditor.CSharp.targets` | `tests/Darp.CodeEditor.Packaging.Tests` (publishes an app from the packed packages) |
| Luau | `src/Darp.CodeEditor.Luau/LuauLanguageServer.cs` | `buildTransitive/Darp.CodeEditor.Luau.targets` (luau-lsp per platform, checksums) |
| CI and releases | `.github/workflows` | `docs/packages.md` (feed access) |

## Measurements

Linux: x64 VM, 8 cores, no GPU (software rendering), shared with other builds. Windows: laptop with an i7-1265U (2
performance and 8 efficiency cores), 125% scaling, on AC. Budgets from [design.md](design.md), section 1.4.

| | Budget | Linux, AOT | Linux, JIT | Windows, AOT | Windows, JIT |
| --- | --- | ---: | ---: | ---: | ---: |
| Highlighted text after the editor is created | ≤ 100 ms | 51 ms | 472 ms | 94 ms | 663 ms |
| C# ready after the editor is created (published, ReadyToRun) | ≤ 1.5 s | 0.66 s | 0.80 s | not re-run | not re-run |
| Warm completion p95, word list / members | ≤ 50 ms | 25 / 11 ms | 35 / 21 ms | 31 / 13 ms | 33 / 13 ms |
| Keystroke handling p99 while typing C# | — | 1.1 ms | 2.5–4.6 ms | 1.9 ms | 2.1 ms |
| Memory idle, app + C# server | — | 351 MiB | 408 MiB | 151 + 165 MiB | 201 + 169 MiB |

The Windows numbers were taken before the helpers shipped with ReadyToRun; there, C# was ready after 2.3 s (AOT) and
2.8 s (JIT). On Windows, typing went from a keystroke p99 of 40 ms to 2 ms when popups moved into the window. Key to
pixels is measured on Linux only: the probe injects input with XTest and refuses to inject on Windows.

## Decisions taken during implementation

The product owner made the design decisions in [design.md](design.md). These were taken along the way; each is easy to
revisit.

- **No ContentModified for edits.** The C# server answers requests for the text they were sent for, as the LSP
  specification asks; the editor cancels requests it no longer needs.
- **Hang detection.** A server that does not answer within 30 s is restarted; until its first answer it gets twice that,
  and each hang doubles the next server's limit up to 2 minutes. A starting server can therefore wait up to 4 minutes
  after repeated hangs. The UI never waits.
- **Late completion edits.** Edits that resolve brings after a commit, such as a `using`, join the commit's undo step
  only while the document is unchanged and that step is still the last one; otherwise they are dropped.
- **Runner frameworks.** The runner bundles only `Microsoft.NETCore.App`. Script references that need another shared
  framework, such as ASP.NET Core, fail the build with a message.
- **Script cultures.** Scripts run with invariant globalization: deterministic, and no ICU needed on Linux. Apps opt in
  through `RunnerEnvironmentVariables` (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0`), which needs ICU on Linux.
- **No injected keys on Windows.** `SendInput` reaches whatever window has the focus, so the key-to-pixel probe runs on
  Linux test machines only.
- **Package payload.** The C# package carries the server and runner as portable code; the app's publish compiles them
  with ReadyToRun for its platform and adds the runtime. One 15 MiB package serves every platform; apps set
  `PublishReadyToRun=true`. The Luau package carries all four luau-lsp builds (21.5 MiB).
- **Two copies of Roslyn.** The server and the runner each carry their own copy of the compiler (28 MiB with
  ReadyToRun), so they stay separate processes with independent dependencies.

## Open issues

- **Startup stalls on Windows.** The JIT demo's first frame took 2.2 s on the laptop, and the UI stalled up to 252 ms
  while the C# server started (AOT: 179 ms and 69 ms). Most is first-use compilation in the app; the app's own
  ReadyToRun build and moving work off the first frame are the next steps. Typing, completion and highlighting stay
  under 16 ms.
- **Windows startup after packaging** is not re-measured; ReadyToRun saved 0.35–0.65 s on Linux.
- **Not run on arm64 or macOS.** Publishing for linux-arm64, win-arm64 and macOS works from Linux; nothing ran there.
  luau-lsp has no Windows arm64 build and runs under emulation there.
- **SDK coupling.** The publish step calls SDK tasks for ReadyToRun and app hosts; a future SDK may change them.
- **Not built:** the hover-docs switch (decisions #38 and #40), snippet completion, IME and accessibility checks,
  regular C# files (scripts only, #39).
- **Small ones:** luau-lsp's newline trigger sends empty completion requests; changing a script environment needs a new
  service; the first light/dark switch takes about 50 ms in Avalonia's restyle.

## Next steps

1. Merge the release PR when the packages should reach the feed.
2. Build the greenfield test app from the Avalonia template with the packages (#24), then move the real app over.
3. Re-run the Windows benchmarks with the ReadyToRun helpers, and run once on arm64.
