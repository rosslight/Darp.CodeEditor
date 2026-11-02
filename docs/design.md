# Darp.CodeEditor: design and decision log

Status as of 2026-11-02. The repository is private. This document condenses the design conversation (questions Q1–Q38), the research reports and the WebView bake-off report. The sources are listed in [section 7](#7-sources). Where a source is unclear or conflicts with another, the text says `TODO(verify)`.

Terms used below:

- **Editor core**: the `Darp.CodeEditor` package. AOT-safe. It has no Roslyn and no MEF.
- **Language server**: a separate process that speaks LSP over stdio.
- **Script runner**: a separate helper process that compiles and runs scripts for the host app.
- **JIT host / AOT host**: an app built for normal .NET, or published with NativeAOT.
- **Existing editor**: the editor this project replaces. It is based on the VS editor platform and MEF.

## 1. Purpose and goals

### 1.1 Why

The existing editor has three structural problems:

- Its text buffer comes out of one MEF composition that also holds Roslyn. Text cannot appear until Roslyn has loaded. The composition is rebuilt by reflection on every start.
- Its VS editor contracts are synchronous: highlighting, markers and completion commit are requested on the UI thread. Slow work behind them freezes the UI.
- MEF and reflection-based discovery block NativeAOT publishing, even when no C# is used.

### 1.2 What it is for

A code editor library for script editors inside the product owner's Avalonia apps. It is not an IDE. Users get highlighting, completion, signature help, hover, compiler diagnostics and search. The host app defines the script (references, imports, globals) and the library runs the language work. Running scripts is also part of the package, in a helper process (#33).

### 1.3 Requirements

- **R1 Performance.** Meet the budgets in [1.4](#14-budgets).
- **R2 Backend never blocks the UI.** Loading the language backend (Roslyn, luau-lsp) must never stall the UI thread. The editor works with no language service at all.
- **R3 Highlighting.** Syntax highlighting from VS Code theme files, with light and dark defaults.
- **R4 Languages.** C# first. Luau follows through luau-lsp. Other languages get highlighting and search.
- **R5 AOT editor.** The editor core must be AOT-compatible on its own.
- **R6 Hosts.** C# is primary. Hosts may be JIT or AOT.
- **R7 One definition.** The editor and the runner read the same script definition, so editor errors match what runs (#3).
- **R8 Run scripts.** A script edited in an app can be run in both JIT and AOT apps. The script can use the app's own types and the NuGet packages the app already ships (#22, #29).
- **R9 Platforms.** Windows and Linux, x64 and arm64, required. macOS best-effort (#21).

### 1.4 Budgets

| Budget | Target | Source |
|---|---|---|
| Highlighted text | within 100 ms, without waiting for the language service | #4 |
| UI thread | no language work over 16 ms; tests fail above 50 ms | #4 |
| C# ready (JIT host) | within 1.5 s of a cold start, warm-up included, with ReadyToRun on | #4 |
| Completion | within 50 ms (p95) once warm | #4 |

Status against the budgets, from the measurements in [section 4](#4-key-measurements):

| Budget | Measured | Status |
|---|---|---|
| Highlighted text ≤ 100 ms | AOT on Linux: 99–102 ms highlighted (bake-off), 83 ms to visible text. JIT on Linux: 701–878 ms. AOT on the Windows laptop: 225 ms. JIT on the Windows laptop: 1,123 ms | Met for AOT on Linux, and 2 ms over in the bake-off run. Missed in JIT and on Windows. The prototype measured from process start; whether that matches "within 100 ms of creation" is `TODO(verify)` |
| UI thread ≤ 16 ms | Linux keystroke p99 1.1 ms. Windows laptop p99 40 ms before in-window popups, 3 ms after. A 27 ms first-completion stall remains on Windows | Met on Linux. Open on Windows |
| C# ready ≤ 1.5 s | Prototype JIT: 1.75 s without ReadyToRun. Own server, start to first member list: 0.73 s with ReadyToRun. Windows laptop: 2.68 s | Met only with ReadyToRun on Linux. Missed on Windows |
| Completion ≤ 50 ms p95 | Member list p95 10–20 ms. Word list (~3,300 items) p95 25–36 ms on Linux. Windows laptop word list p50 36 ms | Met on Linux. At risk for word lists on Windows |

### 1.5 Scope

**In v1:** highlighting, completion, signature help, quick info (hover), compiler diagnostics, find and replace (match case, whole word, regex, literal replace, all matches highlighted), and basic editing helpers (auto-indent, bracket matching, auto-closing brackets and quotes, toggle comment, Tab and Shift+Tab indentation). The product owner wants search in v1 and everything else on top (#8, #14).

**Not in v1:** Luau, rename, quick fixes, refactorings, find usages, format-as-you-type, analyzer diagnostics, go-to-line, `$1` capture groups in replace, `#r` and `#load` directives, cross-file scripts. Regular C# project files are open (section 5).

**Design targets:** scripts up to about 5,000 lines and a handful of open editors. One Roslyn instance per app, shared by all editors (#5, #15). Above about 20,000 lines, C# features switch off and only TextMate highlighting stays (#5).

**Not a security sandbox.** The helper process isolates accidental failures (exceptions, hangs, crashes). It does not isolate hostile code.

## 2. Architecture

### 2.1 Overview

```
Host app (JIT or AOT)
│
├─ Darp.CodeEditor  (editor core: AOT-safe, no Roslyn, no MEF)
│    AvaloniaEdit text surface · background TextMate highlighter
│    completion, hover, signature popups drawn inside the window
│    │
│    │  ILanguageService (async; document snapshot, version, cancellation)
│    ▼
│  LSP client  (planned: Darp.CodeEditor.Lsp, working name)
│    │ LSP over stdio                         │ LSP over stdio
│    ▼                                        ▼
│  C# language server (Roslyn 5.9.0)       luau-lsp (follow-up)
│    one process, shared by the app's editors
│
└─ Script runner (separate process, bundled .NET runtime)
     StreamJsonRpc + MessagePack over stdio; batched calls
     CSharpScript.EvaluateAsync<IHandler>; handler returned to the app
     scripts call back into the app through interfaces on the globals
```

### 2.2 Components

- **Editor core (`Darp.CodeEditor`, AOT-safe).** AvaloniaEdit 12.0.0 is the text surface. TextMateSharp 2.0.4 supplies grammars. The background highlighter is ours (`src/Darp.CodeEditor/Highlighting/BackgroundTextMateHighlighter.cs`). Completion, hover and signature help are our own controls, drawn inside the editor window (#19). Search uses AvaloniaEdit's search panel.
- **Language service contract (`ILanguageService`).** Six async methods: completions, completion description, diagnostics, classifications (for the visible range), quick info and signature help. A document is `DocumentSnapshot(DocumentId, Version, Text)`. Every request carries a cancellation token. The editor always calls the service through `Task.Run`. A keystroke cancels requests for the document. Results for an older version are dropped, and cached results move with the text until new ones arrive.
- **C# language server (`Darp.CodeEditor.CSharp.Server`).** Out of process, LSP over stdio, Roslyn 5.9.0. It keeps one Roslyn document per version and applies `didChange` edits incrementally. It loads the globals type with `MetadataLoadContext`, so no user code runs in the server. Its 36 tests pass, including 19 compiler-parity checks against `CSharpScript`. It currently uses the EmmyLua framework, which is to be replaced (#37).
- **Shared script environment.** `Darp.CodeEditor.CSharp.Hosting` (AOT-safe, no Roslyn) describes a script: globals assembly path and type name, references, framework references, imports, language version, nullable setting, unsafe, overflow and warning level. It is sent in a versioned `csharpScript` initialization section. `Darp.CodeEditor.CSharp.Scripting` (JIT) turns the description into `ScriptOptions` and compilation options. The server uses it; the runner will use it too, so the two cannot drift apart (#3).
- **Script runner (not in the repository yet).** A separate helper process on the bundled runtime (#36). It uses StreamJsonRpc 2.26.150 with MessagePack over stdio, with a length-header framing handler. The helper compiles the script with `CSharpScript.EvaluateAsync<IHandler>` and returns the handler. The scripts receive a generated proxy for the globals. The spike tested exceptions, hangs, crashes and parent death on Linux.
- **Luau (follow-up).** luau-lsp runs as a separate process behind the same LSP client. Type definitions for the host API would come from the team's Luau library, whose type-definition generation is planned, not built (#2).

### 2.3 Rules that keep the UI thread free

1. The editor works with no language service. Typing and TextMate colors are immediate. C# features switch on when the server is ready.
2. The boundary is async only. Drawing code reads caches and never calls the language service.
3. Every request carries a document version and a cancellation token. A keystroke cancels the document's requests. Late results are dropped or moved to the current text.
4. Completion fetches its list once, off the UI thread. It filters locally on each keystroke, loads descriptions only when shown, and commit inserts text that is already computed.
5. A UI stall probe (`src/Darp.CodeEditor/Instrumentation/UiStallProbe.cs`) measures stalls in the demos and benchmarks. The headless test that types while Roslyn loads, which the original design planned, is not in the repository.

### 2.4 Packaging and the bundled runtime

- The language server and the script runner each run on a .NET runtime that ships with the app (#31, #36). Measured sizes: the helper is 34.9 MiB framework-dependent, 73.9 MiB with ReadyToRun and 113.7 MiB self-contained. The runner spike measured 100–122 MiB on disk with its runtime and 175–183 MiB of RAM.
- The current server builds are framework-dependent and need .NET 10 at run time: 27 MiB published, 10 MiB zipped; with ReadyToRun, 68 MiB and 29 MiB. Which build the apps will ship is `TODO(verify)`.
- No SDK is needed on user machines (#28, #31). NuGet packages that an app already ships are passed to scripts as references by the host (#22).
- AOT apps ship the script API (globals type and the types scripts use) as separate DLLs next to the app (#29). The original design proposes a build-time copy step, as an MSBuild target. It is not built, and its details are `TODO(verify)`.
- Opt-in MSBuild switch, off by default: copies the SDK's English XML documentation next to the app, including the docs of the app's script DLLs, so hover shows descriptions (#38).

### 2.5 Current state, 2026-11-02

| Area | Where | State |
|---|---|---|
| Editor core and demos (JIT and AOT) | `main`, commit `01cef73` | Built. 15 tests pass (per the conversation). In-window popups exist behind `--overlay-popups` |
| C# language server and shared environment | `feat/csharp-script-server` (draft PR #1) | Built. 36 tests pass. Review: 7 findings, 4 must-fix (cancellation, closed-document cache, version rules, server outliving the app). Merge after fixes |
| LSP layer (`Darp.CodeEditor.Lsp`) | `feat/lsp-protocol` has no commits beyond `main` | Not started |
| Script runner | `feat/script-runner` has no runner code yet | Spike done, see section 4. Product code not started |
| Editor LSP client | none | Not started. Demos still use the in-process C# service |
| Luau | none | Follow-up |
| WebView editor | `feat/webview-bakeoff` | Record only, not merged |
| Repository setup (CI, releases, feed) | in flight | Not in the repository yet |
| Greenfield test app | none | Planned (#24) |

## 3. Decision log

Question numbers are from the design conversation. Later answers replace earlier ones. The Status column says which. Rows marked `TODO(verify)` are listed in section 6.

| # | Topic | Decision | Why | Status |
|---|---|---|---|---|
| 1 | C# in AOT apps | First answer: v1 has C# only in JIT apps. Long term, AOT apps get C# through a helper process | Roslyn runs in NativeAOT only as a ~121 MiB setup the research calls unsupported, and emitted code cannot run in NativeAOT | superseded by #10 and #33 (AOT C# is now in scope) |
| 2 | Luau | After C#, not in v1. Highlighting first, then luau-lsp as a separate process | A maintained MIT Luau grammar exists. luau-lsp provides completion, signature help, hover and diagnostics. An own type checker would take weeks | decided |
| 3 | Script definition | One description per script: references, imports, globals type, language version, nullable setting, script kind. Editor and runner both read it | Editor errors then match what runs. The first answer's "running stays in the app" is superseded by #33 | decided (running part superseded by #33) |
| 4 | Performance budgets | Highlighted text within 100 ms without the language service. No language work over 16 ms on the UI thread (tests fail above 50 ms). C# ready within 1.5 s in JIT apps with ReadyToRun. Completion within 50 ms p95 | Revised from the first numbers (2 s to C# ready, 100 ms completion p95) after the C# hosting research | decided |
| 5 | Document size and count | Scripts up to ~5,000 lines, a handful of editors. One Roslyn per app. Above ~20,000 lines, C# features off, TextMate only | Product owner: "sounds reasonable" | decided |
| 6 | Theming | VS Code theme files. Light and dark defaults. The app can pass its own file. Follows the app's Fluent light/dark variant. Semantic colors come from the same theme | TextMate and C# colors never clash | decided (only Dark+ is wired; semantic colors hard-coded in the prototype) |
| 7 | Repository, name, visibility | Name `Darp.CodeEditor`. First planned as public on nuget.org | Changed to private, see #23 | superseded by #23 |
| 8 | Search | Find and replace with match case, whole word and regex, all matches highlighted. Replace is literal. Go-to-line later | Literal replace is enough for v1. `$1` capture groups are a later patch | decided |
| 9 | Highlighting off the UI thread | Own background TextMate tokenizer: one cache entry per line, results applied only to lines not edited since the request. Lines over 5,000 characters are not colored. Hybrid: visible lines colored synchronously within ~4 ms, the rest in the background | Stock AvaloniaEdit.TextMate stalls 2.5 s per keystroke on a 20,000-character line. The hybrid gives first-frame colors | decided (hybrid not built yet; the prototype colors arrive after the text, 16–95 ms) |
| 10 | Language service: contract and process model | Own small contract in the editor (`ILanguageService`, async, snapshot and version, offsets). LSP between processes. Every language runs in its own language-server process, in JIT and AOT hosts. Accepted refinements: incremental open, change and close; lazy completion resolve; per-item commit characters; classification for the visible range only; signature parameter offsets; structured text | One setup to maintain. Roslyn's memory work stays out of the UI process. The GC research found no in-process stall, so isolation is a maintainability and robustness choice | decided |
| 11 | Kinds of C# | Scripts first. Regular C# files too, where possible | Product owner: C if possible, A (scripts) more important | open (whether regular files are needed at all is unanswered) |
| 12 | Script definition contents | References, imports, globals type, language version, nullable setting, script kind. No `#r` or `#load` in v1. Each file is its own script | Host controls references, so editor and runner agree. Product owner: "keep it simple" | decided |
| 13 | Diagnostics | Compiler diagnostics only. Analyzers can be opted in later | Analyzers add load time and memory | decided |
| 14 | Editing helpers | Auto-indent, bracket matching, auto-closing brackets and quotes, toggle comment, Tab and Shift+Tab. No go-to-line | Cheap, and expected from any code editor | decided (not in `main` yet) |
| 15 | When Roslyn starts | Starts when the first C# editor opens. `PrewarmAsync()` lets the app start it earlier. One shared instance per app | Apps that never open an editor pay nothing. Roslyn costs ~220 MiB once loaded | decided (`TODO(verify)`: `PrewarmAsync` is not on any branch; the server warms up at start instead) |
| 16 | Pilot app and existing editor | First consumer is the greenfield test app (#24). The real app follows after the test app | Product owner: "greenfield test app for now" | decided (`TODO(verify)`: freezing the existing editor was recommended, not confirmed) |
| 17 | Repository setup | Copy the CI, release-please, NuGet publishing and changelog setup of an existing sibling library | Proven setup | decided (not in the repository yet) |
| 18 | Helper runtime, long term | Separate helper on an installed .NET runtime; download a per-user runtime if none is installed | Supported setup. A crashing script cannot take the app down | superseded by #31 |
| 19 | Popups | Completion, hover and signature help drawn inside the editor window, always | Native popups cost 10–30 ms to open or close on Linux. On Windows, in-window popups brought keystroke p99 from 41 ms to 3 ms | decided (default switch is in `feat/editor-polish`) |
| 20 | Roslyn APIs | Public Roslyn APIs only, where possible. Completion resolve uses the public `GetChangeAsync` | The package must work with whatever Roslyn version the app ships | decided (`TODO(verify)`: not fully met. The prototype still reads `Properties["InsertionText"]`, and the server report says shared completion items depend on Roslyn internals) |
| 21 | Platforms | Windows and Linux, x64 and arm64, required. macOS best-effort | Product owner: "sounds good". luau-lsp has no native Windows arm64 build; its x64 build runs under emulation | decided (only Linux x64 and Windows x64 measured) |
| 22 | Package imports (`#:package`) | Accepted in round 3: host list plus `#:package Name@Version` in scripts | Dropped later. "NuGet packages" means DLLs the app already ships, passed by the host as references | moot (dropped; no numbered decision) |
| 23 | Hosting and feed | Private GitHub repository and the team's existing private package feed | Product owner: yes | decided |
| 24 | Greenfield test app | New app in the repository, generated from the team's Avalonia template (DI, FluentAvalonia, NativeAOT). It replaces the demo apps as the realistic host | Tests realistic hosting in one repository | decided (not built yet) |
| 25 | Editor surface | Own editor: AvaloniaEdit text surface with our highlighter, completion and popups. No WebView editor | The bake-off (25a) found it the only option inside the 100 ms startup budget on Linux | decided |
| 25a | Bake-off result (not a question) | On Linux, AOT: own editor 102 ms to highlighted text, CodeMirror 293 ms, Monaco 544 ms. Idle memory 91 MB against 568 and 760 MB. Closing the window while the page loads crashed the WebView app in 5 of 28 tries. Report on `feat/webview-bakeoff`, not merged | Keystroke-to-pixel times are about equal, within one frame. The WebView was not run on Windows | decided |
| 26 | Windows benchmark host | Run the Windows benchmarks on the Windows laptop, not on the Windows test machine, whose desktop session was disconnected | Product owner asked for the laptop to run them | decided |
| 27 | Regular C# files: existing server | Product owner: yes, use an existing server if possible | Answered before #22 was dropped | moot (see #11) |
| 28 | Bundling NuGet restore libraries | Bundle everything needed for restore, so app users need no SDK | Product owner: "in general, yes" | moot (restore dropped with #22) |
| 29 | Script API in AOT apps | The script API (globals type and the types scripts use) is its own small project and ships as separate DLLs next to the app | AOT code cannot be loaded at run time. The helper loads the API by path | decided |
| 30 | Regular files with "bundle everything" | Options were: server plus bundled SDK, own server for single files, or server only with an installed SDK | Moot after #22 was dropped | moot |
| 31 | Helper runtime | The C# helper ships its own .NET runtime. No download step, no SDK on user machines | Works offline. Adds about 79 MiB per app (113.7 MiB self-contained against 34.9 MiB framework-dependent) | decided |
| 32 | Script handler and callbacks | A script returns a handler implementing an app interface, and the app keeps calling it. Scripts can call back into the app through interfaces on the globals | Covers the current `EvaluateAsync<IHandler>` pattern. Calls cross the process boundary through generated proxies | decided |
| 33 | Where scripts run | In the helper process, in AOT and JIT apps. JIT is a trial that stands only if per-call cost holds | Isolates hangs and crashes, one setup. Warm call measured at about 30 µs p50 and 64 µs p99 | decided (JIT part is a trial) |
| 34 | Script API interface rules | Methods are async only (`Task`, `Task<T>`, `IAsyncEnumerable<T>`). Returned handlers have no events or properties; use callback interfaces. No generic methods. Data is copied. No streams in v1 | The price of a process boundary and of AOT | decided |
| 35 | High-frequency calls | Batching in the runner API from the start. No in-process mode | 100k packets: one call each takes 3.3 s; batches of 100 take 0.07 s (~1.5M packets/s) | decided |
| 36 | Processes | Two separate processes, language server and script runner, on the same bundled runtime | A hung or killed script must not take down completion. The runner must work with no editor open. Sharing would save ~150 MiB | decided |
| 37 | EmmyLua framework | Replace it with one small LSP layer (working name `Darp.CodeEditor.Lsp`), shared by the editor client and the server | EmmyLua needed a replaced scheduler for cancellation, fixed shutdown and hand-written completion lists. Its NativeAOT publish gives 100 warnings, so it cannot serve as the AOT client | decided (not built; the server still uses EmmyLua) |
| 38 | .NET docs for hover | Opt-in MSBuild switch, off by default. Copies the SDK's English XML docs at build time, including the app's script DLL docs. No SDK needed on user machines | Without docs, hover shows signatures but no descriptions. Product owner: do not overfit on "bundle everything" | decided (`TODO(verify)`: "off by default" was proposed in the conversation and not confirmed explicitly) |

## 4. Key measurements

Only the numbers that drove decisions. Source names refer to section 7.

| Measure | Value | Drove | Source |
|---|---|---|---|
| Highlighted text, AOT, Linux VM | 102 ms on screen (median of 5, bake-off re-run); 99 ms in the bake-off's in-process run. 83 ms is time to visible text, not highlighted text | #4, #25 | WebView bake-off report |
| Highlighted text, JIT, Linux VM | 701 ms (prototype); 878 ms (bake-off) | #4 (budget missed in JIT) | Editor prototype; WebView bake-off report |
| Highlighted text, Windows laptop | AOT 225 ms; JIT 1,123 ms (medians of 3) | #4 | Windows benchmark run (`TODO(verify)`: not a committed report) |
| Stock TextMate on a 20,000-character line | 2,522–2,526 ms per keystroke; background tokenizer 2.9 ms | #9 | Editor prototype |
| Native popups | 10–30 ms to open or close on Linux. List rebuild up to 33 ms per keystroke | #19 | Editor prototype |
| Keystroke handling p99, Linux | 1.1 ms (prototype); 0.43 ms in-process (bake-off) | #19, #25 | Editor prototype; WebView bake-off report |
| Keystroke handling p99, Windows laptop | 40 ms before in-window popups; 3 ms after (Roslyn); 1.2 ms (AOT) | #19 | Windows benchmark and typing investigation (`TODO(verify)`: fix not pushed) |
| Key press to changed pixels, Linux (p50 / p95) | Own editor 15 / 22 ms; CodeMirror 17–18 / 23–24 ms; Monaco 21–22 / 28–29 ms | #25 | WebView bake-off report |
| Idle memory, all processes, AOT | Own editor 91 MB; CodeMirror 568 MB; Monaco 760 MB | #25 | WebView bake-off report |
| Roslyn in JIT: start to first completion | 1.12 s; 0.82 s with ReadyToRun. Warm completion p50 3.3 ms, p95 6.1 ms | #4, #15 | C# hosting research |
| Roslyn in NativeAOT | Works when four Roslyn assemblies are preserved. Executable 120.70 MiB. Loading an emitted assembly throws `PlatformNotSupportedException` | #1, #10 | C# hosting research |
| Helper size | 34.91 MiB framework-dependent; 73.87 MiB ReadyToRun; 113.66 MiB self-contained | #31 | C# hosting research |
| NativeAOT host runs a script through CoreCLR | 565–579 ms to first result; 15–17 ms per later call | Feasibility of #33; not chosen | AOT helper runtime research |
| Runner call cost (warm, recommended configuration) | 30 µs p50, 64 µs p99. One call per packet: 3.3 s per 100k packets. Batches of 100: 0.07 s | #33, #35 | Script-runner spike report |
| Runner start to first result | 0.56 s with ReadyToRun; 0.96 s without | #33 | Script-runner spike report |
| GC pauses | 40 new runs: longest 21.4 ms, none over 50 ms. The prototype's 57–167 ms outliers were not reproduced | #10 (GC alone does not force a separate process) | GC pause research |
| Existing C# language servers | Microsoft's server: 1.99 s to first completion, 385 MiB, 147.6 MiB payload. csharp-ls: 2.81 s, 256 MiB. Neither supports a custom globals type | #10 (build our own) | C# language server research |
| Own C# server against the in-process service | Start to first member list: 1,123 ms (729 ms ReadyToRun) against 1,341 ms. Warm word list p50 22 ms against 11 ms. Memory 248 MiB against 216 MiB | #10, #20 | C# language server benchmark (README on `feat/csharp-script-server`) |

## 5. Open questions and next steps

### 5.1 Open questions

1. **Regular C# files.** Are they needed, or scripts only? Asked as Q27–Q30 and not answered. The assistant recommended dropping them. (#11, #27, #30)
2. **Final package split.** The names proposed in round 2 (`Darp.CodeEditor.CSharp`, `.CsharpAot`, `.Luau`) predate #10 and #36. The branch has `.Hosting`, `.Scripting` and `.Server`. The final list is `TODO(verify)`.
3. **Shipped server build.** Framework-dependent or bundled with its runtime (`TODO(verify)`, see [2.4](#24-packaging-and-the-bundled-runtime)).
4. **Public-API policy (#20).** Completion resolve still depends on Roslyn internals in the server report. The prototype reads `Properties["InsertionText"]`.
5. **Word lists.** The server takes about 2× the in-process time for ~3,300 items (22 ms against 11 ms p50). On the Windows laptop this may exceed the 50 ms budget. Options: filter on the server with `isIncomplete`, drop namespace labels, or bypass the framework's response copies. Not decided.
6. **Windows first-completion stall.** A 27 ms UI stall on the first completion of a session. The fix is a local, unpushed commit on the Windows laptop (`TODO(verify)`).
7. **Windows budgets.** Highlighted text (1,123 ms JIT) and C# ready (2.68 s) are over budget on the Windows laptop. The Windows re-run is not done.
8. **Untested platforms.** arm64 and macOS are untested. The server is tested on Linux x64 only.
9. **GC outliers.** The 57–167 ms pauses seen in the early prototype are unexplained. They were not reproduced in 40 runs.
10. **Script runner.** The Windows process cleanup is documented, not tested. The batching numbers come from synthetic load. Real handler work and UI load at 1,000–10,000 packets per second are not measured.
11. **Nullable in scripts.** `CSharpScript` has no public nullable option. The server accepts only `disable`. Scripts can opt in with `#nullable enable`. The flag for duplicated core-library types cannot be set.
12. **IME and accessibility.** Not tested. The test VM has no input-method engine.
13. **Hover docs switch (#38).** Default and cost (`TODO(verify)`).
14. **Theme model.** Only Dark+ is wired. Semantic colors are hard-coded.
15. **Environment changes.** Changing the script definition requires a server restart.

### 5.2 Next steps (planned as of 2026-11-02)

1. Merge the C# server after the review fixes: cancellation, closed-document results, version rules, a watchdog for a dead parent process, lifecycle checks, globals identity, and a benchmark with overlapping requests. Add tests that fail when the bug is present.
2. Build `Darp.CodeEditor.Lsp`, switch the server to it and remove EmmyLua (#37).
3. Build the script runner: a separate helper on the bundled runtime, sharing one runtime folder with the language server. Batching, timeouts, restart, and no orphan processes on Windows and Linux. A Run button in both demos.
4. Editor polish: in-window popups by default, the first-completion stall, first-frame colors, editing helpers, light and dark themes, and the key-to-pixel probe with a guard so no keystroke reaches another app.
5. Build the editor's LSP client, so both demos get C# through the server. The AOT demo gets C# for the first time.
6. Repository setup: CI on Linux and Windows with an AOT gate, release-please, and publishing to the private feed.
7. Re-run the Windows benchmarks on the Windows laptop with in-window popups.
8. Build the greenfield test app from the template (#24). The real app follows.
9. Luau through luau-lsp (#2).

## 6. Inconsistencies in the sources

1. **#1 against later decisions.** The first answer keeps C# out of AOT apps in v1. #10, #33 and the next steps put AOT C# into the current work. Treat #1 as superseded.
2. **#9.** The first answer was stock TextMate, with a wait for AvaloniaEdit to fix long lines. The reopened answer is the hybrid, which is final.
3. **#4.** The first budgets (2 s to C# ready; 100 ms completion p95) were replaced by the revised ones.
4. **#7 against #23.** Public on nuget.org was first planned. The product owner made it private.
5. **#18 against #31.** Installed runtime with download was the answer for "lets go with A for now". The bundled runtime (#31) replaces it.
6. **#3 against #33.** "Running stays in the app" is superseded: the package now owns a runner.
7. **#22.** `#:package` was accepted and then dropped. The project memory note still lists `#:package` as current in one line and as dropped in another. The dropped line is the one that matches the conversation.
8. **#11 and #27 against section 5.1, item 1.** "Yes" to regular files, but the later question whether they are needed was not answered.
9. **#20 against the code.** "Public APIs only" against the prototype's `Properties["InsertionText"]` and the server report's note on Roslyn internals.
10. **Budget clock.** The budget says "within 100 ms of creation". The prototype measured from process start. Not reconciled (`TODO(verify)`).
11. **83 ms.** The prototype summary in the conversation calls 83 ms "highlighted text". The bake-off report shows 83 ms for visible text and 99 ms for highlighted text in the same run. The report is used here.

## 7. Sources

- **Design conversation** (round-by-round questions Q1–Q38, answers and "Decided" lists). Not in this repository.
- **Project memory note.** Checked. One stale line, see section 6.
- **C# hosting research.** Roslyn in NativeAOT, JIT start-up and completion costs, helper sizes, reference assemblies.
- **AOT helper runtime research.** Six options for the helper's .NET runtime. Runtime archive sizes and NativeAOT hosting of CoreCLR.
- **Existing editors research.** Avalonia-compatible controls, WebView with Monaco and CodeMirror, commercial controls. No product meets all requirements.
- **GC pause research.** Root cause of the early outliers. 40 runs.
- **C# language server research.** Existing servers compared. Recommendation: a thin adapter around the Roslyn service.
- **Script-runner spike report.** StreamJsonRpc with MessagePack over stdio. Per-call cost, batching, isolation tests on Linux.
- **WebView bake-off report** (`docs/webview-bakeoff.md` on `feat/webview-bakeoff`). Own editor against WebView with Monaco and CodeMirror on Linux.
- **Editor prototype** (`main`, commit `01cef73`). Benchmarks are in the gitignored `measurements/` folder, recorded in the design conversation.
- **C# language server** (`feat/csharp-script-server`, draft PR #1). README benchmark table; review of the branch.
- **Windows benchmark and typing investigation.** Recorded in the design conversation. The fix is a local, unpushed commit on the Windows laptop.
