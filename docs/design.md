# Darp.CodeEditor: design

Status as of 2026-11-05. The project restarted from scratch. The prototype, its full decision log (#1–#49), its
measurements and reports are on the [`draft`](https://github.com/rosslight/Darp.CodeEditor/tree/draft) branch, in
`docs/design.md` and `docs/review-guide.md`. This document keeps what still holds and records the restart's
decisions. The public API is drafted in [api.md](api.md).

## 1. Goal

A code editor library for Avalonia desktop apps, to edit scripts inside a host app. It is not an IDE. Users get
highlighting, search, completion, signature help, hover and diagnostics. The host app defines what its C# scripts see
(globals, references, imports), and the library runs them in a separate process.

### 1.1 Requirements

These are the prototype's requirements; R4 changed with the restart.

- **R1 Performance.** Meet the budgets in [1.2](#12-budgets).
- **R2 Backend never blocks the UI.** Loading or running a language backend never stalls the UI thread. The editor
  works with no language server at all.
- **R3 Highlighting.** Syntax highlighting from VS Code theme files, with light and dark defaults.
- **R4 Languages.** C# scripts first. Any language through its LSP server and a TextMate grammar. Luau after v1.
- **R5 AOT editor.** Everything an app loads is NativeAOT-compatible.
- **R6 Hosts.** Hosts may be JIT or NativeAOT apps.
- **R7 One definition.** What the editor checks is what runs: one script environment for the editor, the compiler and
  the runner.
- **R8 Run scripts.** Scripts edited in an app run in JIT and AOT apps, with the app's own types and the DLLs it ships.
- **R9 Platforms.** Windows and Linux on x64 and arm64 required. macOS best-effort.

### 1.2 Budgets

| Budget | Target |
|---|---|
| Highlighted text | within 100 ms of creating an editor, without waiting for a language server: every editor in NativeAOT apps, and from the second editor on in JIT apps (#103) |
| UI thread | no language work over 16 ms |
| C# ready | within 1.5 s of a cold start, in JIT apps too |
| Completion | within 50 ms (p95) once warm, on scripts of 5,000 lines |

The budgets are targets. Benchmarks measure them, and each pull request compares the benchmarks of the `CI` category,
which track them, with its target branch; unit tests assert no latency (#102).

The first editor of a JIT app takes about 450 ms to its first colors, and about 150 ms with ReadyToRun once the app has
shown its first window. Almost all of it is Avalonia's and AvaloniaEdit's code running for the first time: a plain
AvaloniaEdit editor without highlighting takes about 440 ms. Highlighting adds about 28 ms, on a background thread.
Apps that need a fast first editor publish with NativeAOT, where it takes about 45 ms, or with ReadyToRun.

### 1.3 Scope of v1

**In:** highlighting, find and replace, editing helpers (auto-indent, bracket matching, auto-closing pairs, toggle
comment, Tab and Shift+Tab), completion, signature help, hover, diagnostics, any LSP server, C# scripts with a
host-defined environment, running scripts. Marks beside the scroll bar for errors and search matches, added once the
core works (#73).

**Not in v1:** Luau, rename, quick fixes, refactorings, find usages, formatting, analyzers, go-to-line, `#r`,
`#load` and `#line` (compile errors), scripts across files, regular C# files.

**Design targets:** scripts up to about 5,000 lines and a handful of open editors. Above about 20,000 lines, C#
features may turn off and highlighting stays.

**Not a security sandbox.** The runner process isolates accidents: exceptions, hangs, crashes. It does not isolate
hostile code.

## 2. Structure

### 2.1 Projects

A project boundary needs a reason: a separate process, Roslyn kept out of AOT apps, Avalonia kept out of helper
processes, opt-in size, or a wire format two processes share. Layering alone is not one; folders do that.

| Project | Kind | Why it is separate |
|---|---|---|
| `Darp.CodeEditor` | package | What apps reference: the control, highlighting, themes, search, the client for any LSP server |
| `Darp.CodeEditor.Lsp` | internal assembly, in `Darp.CodeEditor` | JSON-RPC, LSP types, positions and child processes. The C# server uses it too, without Avalonia |
| `Darp.CodeEditor.CSharpScript` | package | C# scripts: the C# language, environment, language server and compiling, without sessions. Opt-in, since it brings Roslyn and a .NET runtime |
| `Darp.CodeEditor.CSharpScript.Protocol` | internal assembly, in `Darp.CodeEditor.CSharpScript` | What the app, the server and the runner exchange: the script environment, compiled scripts, the runner's calls |
| `Darp.CodeEditor.CSharpScript.Server` | executable, in `Darp.CodeEditor.CSharpScript` | Roslyn: the C# language server, and the compiler for the runner |
| `Darp.CodeEditor.CSharpScript.Runner` | package | Running compiled scripts in sessions: a runner process each, with a typed bridge to the app, and the contracts that runner-side script APIs implement |
| `Darp.CodeEditor.CSharpScript.Runner.Process` | executable, in `Darp.CodeEditor.CSharpScript.Runner` | The process scripts run in. It holds no compiler |
| `Darp.CodeEditor.Luau` | package, after v1 | luau-lsp's native builds, opt-in |

Folders, not projects: highlighting, themes, popups, search and the LSP client inside `Darp.CodeEditor`.

The executables' names are placeholders. `Runner` is the package's name, so the runner's executable needs another one.

### 2.2 Processes

```
App (JIT or NativeAOT)
├─ Darp.CodeEditor ─────────────── LSP ───────────► any language server: yaml-language-server, luau-lsp, …
├─ Darp.CodeEditor.CSharpScript ── LSP, compile ──► C# script server (Roslyn), one per app
└─ Darp.CodeEditor.CSharpScript.Runner ── RPC ────► one runner process per session (no Roslyn): runs compiled scripts

The C# server and the runner run on one .NET runtime that the app ships, with ReadyToRun.
```

### 2.3 Tests, samples and benchmarks

- **Tests:** one test project per package. Fixture projects only where a separate DLL or process is what a test checks. Tests
  also run as a NativeAOT app (#101).
- **Samples:** one sample app from the Avalonia template, published both JIT and NativeAOT from the same source. It
  shows how to use the API, nothing else. `samples/Darp.CodeEditor.UseCases` holds the API draft's use cases.
- **Benchmarks:** a BenchmarkDotNet project for most measurements. Measurements of a published app, such as the first
  highlighted frame or key-to-pixel times, need a small app of their own, and fail loudly when a run fails.

## 3. Decisions of the restart

Question numbers continue the prototype's log. The prototype's decisions still hold unless a row here replaces them.

| # | Topic | Decision | Why |
|---|---|---|---|
| 50 | Public packages | `Darp.CodeEditor`, `Darp.CodeEditor.CSharpScript`; `Darp.CodeEditor.Luau` after v1; others when needed. The LSP layer is internal. The editor's API takes any LSP server | The prototype's public surface was too large (about 740 public members, most from the LSP layer and from deriving from AvaloniaEdit's editor). Replaces #43 |
| 51 | Runner package | Running scripts is its own package, `Darp.CodeEditor.CSharpScript.Runner` | Product owner's choice |
| 52 | Who compiles | The C# server compiles scripts; the runner only loads and runs them, without Roslyn. Compiling runs beside the editor's requests on Roslyn's immutable snapshots: completion keeps answering while a script compiles. Both wait while the server starts or restarts. Proven in a spike on 2026-11-05 ([api.md](api.md#9-not-proven-yet)) | Roslyn ships once instead of twice (the runner's copy was 28 MiB); what runs is what the editor checked (R7); the runner restarts quickly after a kill. Replaces the compiling part of #33 and #36 |
| 53 | Benchmarks | Most benchmarks in a BenchmarkDotNet project | Product owner's choice |
| 54 | Restart | The prototype stays on the `draft` branch. `main` starts a new history | Product owner's choice |
| 61 | Built-in languages | The editor package ships plain text, JSON, XML and YAML; the C# package ships C#. Their grammar and editing-rule files are copied from VS Code's extensions, not taken from TextMateSharp's grammar package, together with the grammars they include (VS Code's YAML grammar delegates to two more) and their upstream license notices. More when needed | The grammar package is a 6.5 MB DLL that NativeAOT can't trim. Product owner's choice |
| 64 | Editor properties | A setting is a property of `CodeEditor` when apps would set it differently per editor; one good value for every editor is built in; settings of the text belong to the document. Line numbers and the search bar are part of the editor's API without a use case asking for them | Product owner: a good editor experience counts even before a use case asks, taking the best from CodeMirror and Monaco |
| 65 | Selection | One `TextSelection` value with an anchor and a caret, not a range plus a caret offset | Product owner's choice; keeps a backward selection's direction, as Monaco and CodeMirror do |
| 66 | Indentation | On the document, with the language's default. Not detected from the text in v1 | Product owner: detection is easy to add later |
| 67 | Markers and view state | No markers from the app and no storable view state in v1. The editor's view state in memory: #88 | Product owner's choice |
| 68 | Line and column numbers | `TextPosition` counts lines and columns from 1; offsets stay 0-based. The LSP client converts | Status bars and error messages show positions from 1, as Monaco and AvaloniaEdit count them |
| 69 | Color theme | One `ColorTheme` property instead of a light and a dark theme. Its default comes from the resource `CodeEditorColorTheme`, which the library defines per theme variant (Light+, Dark+) and apps override in their theme dictionaries. `CodeEditorTheme` (now `CodeEditorColorTheme`, #93) is created in XAML with `Source` | Avalonia's own way: editors follow the variant through resource lookup, so `ThemeVariantScope` and custom variants need no API of ours. `Theme` is taken by Avalonia's control themes. Product owner's choice |
| 70 | What the color theme styles | The color theme styles the text area only. Completion, hover, signature help, the search bar and scroll bars take the app's look, from its flyout, tooltip and input styles; code inside popups keeps the editor's colors and font. One include, `CodeEditorStyles`, brings the control theme and AvaloniaEdit's styles. Popups take the editor's theme variant, not the window's, so a `ThemeVariantScope` around an editor reaches them | Token colors belong to their theme's background, as in AvaloniaEdit, Monaco and CodeMirror; popups then match Fluent and FluentAvalonia, font, padding and corners included. Product owner's choice |
| 71 | Errors and hover | Squiggles in the theme's colors; hover shows the diagnostics' messages above the server's Markdown, with code blocks highlighted by the editor's grammar | As in Monaco and CodeMirror. The prototype dropped emphasis and links |
| 72 | Completion icons | VS Code's codicons as vector shapes, in the theme's `symbolIcon.*` colors. Their CC BY 4.0 credit goes in the package's third-party notices | One icon set made for code symbols, with a clear license. Visual Studio's image library has no clear license to redistribute. Product owner's choice |
| 73 | Scroll bar marks | Marks for errors, warnings and search matches beside the scroll bar are in v1, but designed and added after the core | They add to the API without changing it. Product owner's choice |
| 74 | Languages | Built-in languages are static properties of `CodeLanguage`, and packages add theirs as extension members (`CodeLanguage.CSharp`, later Luau's). Built-in and app languages use the same public constructor, with grammar and editing rules as `avares://` or file URIs. Replaces `Bundled(id)` and `FromFiles` | Apps see every language in IntelliSense instead of guessing ids; the built-in ones prove the public path. Product owner's choice |
| 75 | Indentation | Only on the document, starting as in VS Code (2 spaces for YAML, 4 otherwise). Languages have no indentation setting | Product owner's choice. Refines #66 |
| 76 | Loading | Languages and themes start loading in the background when created; no `PreloadAsync`. Load errors go to Avalonia's log | Apps create languages and themes at startup, so the first editor finds them loaded without an extra call. Product owner's choice |
| 77 | Headless compiling | Replaced by #78 | |
| 78 | Documents and servers | A language is syntax only: no server, no environment. Servers create the documents they serve: `LanguageServer.CreateDocument(language, text, filePath)`, `CSharpScriptServer.CreateDocument(environment, text)`. `CodeDocument.Create` creates documents without a server (#82). Headless compiling takes the environment, and the server loads an environment's references once | A server analyzes documents, and the environment is what one script sees, so both belong to the document. Each factory takes exactly what its server needs, and nothing is hidden in the language. Product owner's choice |
| 79 | Document threads and snapshots | Documents are created, read, compiled and disposed on any thread, and changed only on the UI thread; their events come on the UI thread. An immutable `TextSnapshot` (version, text, positions) replaces every `int` version: `CurrentSnapshot`, `MarkUnmodified(snapshot)`, `CSharpScriptCompilation.Snapshot`. `GetPosition` and `GetOffset` move to the snapshot. `DiagnosticsVersion` is dropped until a use case needs it | AvaloniaEdit's buffer throws on other threads, but its snapshots are immutable and cheap. The user types on the UI thread, so changes from elsewhere would race them. Visual Studio's editor (vs-editor-api) works the same way. Product owner's choice |
| 80 | Sessions | Proposed, to be proven (api.md section 9). A session is one runner process with a bridge of two interfaces, an app side and a session side, and one session object in the runner that implements the session side, holds what the session's scripts share, and owns what outlives a run. Setup is ordinary bridge methods. Runs start with `StartScriptAsync`, with an input of their own, may overlap, and bring their own globals; a run lasts as long as its code, and the session disposes its globals before `Completion`. `StopAsync` and `RunToEndAsync` stop a run, killing the session only after a time the app passes; `KillAsync` ends the process; `DisposeAsync` ends a session in a fixed order and kills after a time limit; `Exited` is a task with the reason. Replaces the runner, per-run services, returned handlers and the shared state type, and decides Q63 | Product owner: scripts may change, but as little as possible; drivers can run in the runner; scripts that work together may share a process; scripts must run in NativeAOT apps. Three reviews found that a run whose cleanup outlives its `Completion` leaves work behind that nobody can await. Measured: naming runner-side types costs a NativeAOT app about 700 bytes; a minimal session about 17 MiB and 80 to 110 ms |
| 81 | Typed kinds of scripts | Replaced by #95. Globals implement `IScriptGlobals<TSelf, TSessionObject, TInput>` with a static `Create` (or `IScriptGlobals<TSelf, TSessionObject>` without input, using `ValueTuple`). `CSharpScriptEnvironment.For<TGlobals, TSessionObject, TInput>()` makes a typed environment, and its types travel through `CSharpScriptDocument`, `CSharpScriptCompilation` and `CSharpScriptAssembly` to a session typed by its object, `CSharpScriptSession<TSessionSide, TSessionObject>`. Globals take the concrete session type and show their part of it through their own members | The compiler rejects a script started in another kind of session or with the wrong input, and no constructor convention is left to get wrong. Replaces `SessionType` and matching constructor parameters by type. Product owner's choice |
| 82 | Documents from factories | `CodeDocument` is abstract with a constructor for the library's own types; `CodeDocument.Create` makes documents without a server, and servers make theirs. A C# script document is a typed `CSharpScriptDocument<TSessionObject, TInput>`, which editors show like any document | The typed kind of script stays on the document without a wrapper or passing the environment twice. Product owner's choice |
| 83 | Starting and stopping runs | `StartScriptAsync` returns once the runner has accepted the run, before the globals' `Create`, whose failures end `Completion`. `RunToEndAsync(stopToken, killSessionAfter)` waits for a run and stops it when the token is cancelled; `StopAsync(killSessionAfter)` stops it now; neither kills unless the app passes a time. A kill for a missed stop reports `StopTimedOut`. `Cancel()` is gone | A `Create` that hangs can be stopped like the script. The name says that cancelling stops the run, unlike `Task.WaitAsync`. Four reviews |
| 84 | The session object and what a run owns | The session object comes from `IScriptSession<TSelf, TAppSide>` (now `IScriptSessionObject`, #93) with a static `Create`, like the globals. A run owns, through its globals, what it registered: disposing them stops new calls and awaits the running ones (replaced by #96). A run's script assembly is unloaded only once nothing in the session references its code (replaced by #92) | No constructor convention is left. A spike showed that only removing a registration let a running callback use disposed globals. Four reviews |
| 85 | The C# server and its documents | The server has its own `State` (bindable), `StartAsync` and `Dispose`, instead of exposing a `LanguageServer`; `CodeDocument.Server` is gone. `CreateDocument(text)` makes a C# document for editing only (dropped by #93). Typed documents compile themselves; the environment comes first in every call. Both servers dispose synchronously too. The script API contracts live in the C# package, not a package of their own (moved to the runner package by #95) | One owner per process, and synchronous app exits work. A contracts package would only have kept Avalonia out of a build where it never loads. Product owner's choice |
| 86 | Editing, positions, names | `CodeDocument.Replace`, `TextSnapshot.GetText(range)`, `DiagnosticsSnapshot` (replaced by #92) with `MapToCurrent`, and `CSharpScriptException.ScriptPosition`. `CodeEditor` gets `Cut`, `Copy`, `Paste`, `SelectAll` and a context menu. Runner types take the `CSharpScript` prefix, and the compiled script becomes `CSharpScriptAssembly`. `AllowUnsafe` and `CodeEditorTheme.FromFile` are dropped; scripts get the SDK's implicit usings; `CSharpScriptReference` copies XML docs; environments compare by content | An error clicked after edits lands where it belongs; an app's Edit menu works. Brings back the diagnostics' snapshot that #79 dropped, now that a use case needs it. Four reviews |
| 87 | Logging and metrics | No logger and no logging package (Q55). Each signal has one purpose: what an app acts on is in the API (`State`, `StartAsync`'s exceptions, `Completion`, `Exited` with `DisposeException`); what people read arrives as text (the servers' `StandardError`, a session's `Output`, `Exited.StandardError`); load errors of languages and themes go to Avalonia's log. When the script and the globals' disposal both fail, `Completion` holds both. No public metrics in v1; the prototype's timings stay internal for tests and benchmarks | Product owner: avoid the dependency, never a logger in the editor package, and nothing untargeted. A logger in the C# packages would have duplicated these signals |
| 88 | View state | The caret, the selection and the scroll position belong to the editor and last as long as it does (Q57). `CodeEditor.SaveViewState()` and `RestoreViewState(state)` keep them across a switch; the state follows the edits made since. Documents hold no view state. Apps keep their editors, or save and restore | Product owner: a list doesn't remember its scroll position either, so the principled way is the editor's, with ways around it. Monaco does the same with `saveViewState` and `restoreViewState` |
| 89 | Settled questions | Documents open in their server at creation (Q56). StreamJsonRpc and PolyType are visible in the script API (Q58). One dependency chain until an app without Avalonia needs scripts (Q59). Storing compiled scripts after v1 (Q60). No `Text` on the editor (Q62). No service-container helper. Language servers over stdio. No bundled third-party servers. Every document is disposed, also one without a server. Run doesn't wait for diagnostics. Sessions don't restart by themselves. Documents don't load or save files. Batching is the script API's, with a library helper once a second app needs one. A full value queue makes the script wait. Scripts of different kinds may share a session. Tabs are the app's. The library ships Light+ and Dark+ only. The three options records take a `TimeProvider` for every time-based wait | Product owner's choices |
| 90 | Run outcomes | `RunToEndAsync` throws what `Completion` throws, also after it stopped the run: a kill is reported as `ConnectionLostException`, not as a stop. A run counts as stopped when its code ends with any `OperationCanceledException` after its stop token was cancelled. Session objects and globals implement `Create` and their disposal explicitly (globals: #95) | Three reviews: a kill reported as a clean stop left the editor holding a dead session; drivers link the stop token into their own timeouts; public members of globals are callable from scripts (shown with Roslyn's script compiler) |
| 91 | Processes and dispatchers | The C# server and every session's process end with the app, even when it is killed. Editors post to their own `Dispatcher` (Avalonia 12); documents and servers use `Dispatcher.UIThread` (refined by #100). Nothing touches Avalonia's dispatcher before Avalonia is set up; until then, events are raised where the change happens | A runner left behind keeps devices open: on Windows, runners of a hard-killed NativeAOT app kept running unless they watched their standard input or sat in a Job Object, and with either they ended within 3 ms. In Avalonia 12, touching `Dispatcher.UIThread` from a pool thread before setup ties it to that thread, and the app's setup then fails (shown on Linux and Windows). Product owner: use the control's dispatcher where possible |
| 92 | Positions carry their text | `Diagnostic` carries its `Snapshot`, and `Start` is computed from it; `DiagnosticsSnapshot` is dropped. `CSharpScriptException.ScriptSnapshot` comes with `ScriptPosition`, also for callbacks from earlier runs. A string compiled without a document gets a snapshot too, so `CSharpScriptCompilation.Snapshot` is never null. A session keeps every script it ran until it ends, instead of unloading | A pair that stayed consistent only when read on the UI thread is gone; a failure in a callback could not say which text it points into. Unloading was unproven, and keeping is honest and testable. Replaces part of #84 and #86 |
| 93 | Smaller API, clearer names | `CSharpScriptServer.CreateDocument(text)` is dropped. `IScriptSession` becomes `IScriptSessionObject`, the type parameter `TSession` becomes `TSessionObject`, and `CodeEditorTheme` becomes `CodeEditorColorTheme`, as the `ColorTheme` property and its resource key | No use case needed editing-only C# documents, and regular C# files are out of v1. `IScriptSession` read as the interface of `CSharpScriptSession`, and `CodeEditorTheme` as an Avalonia control theme |
| 94 | Runner-side deployment | A runner-side project marked `CSharpScriptReference` is deployed as its own untrimmed publish for the app's platform, with its dependency manifest, and loaded through it with `AssemblyDependencyResolver`; the bridge and the contracts load once. The runner starts on the base .NET runtime: another shared framework fails the build. The app's and the runner side's target frameworks must fit | A spike on linux-x64: copying the DLLs that a NativeAOT build names misses the managed closure; a portable publish picks the wrong per-platform code; one fixed runtime configuration can't load ASP.NET Core. On win-x64 the per-platform publish works as well, and a `net10.0` runner loads a `net10.0-windows` runner side |
| 95 | Kinds of scripts and the package split | A kind of script is its globals type, an ordinary class: `CSharpScriptEnvironment<TGlobals>` (made with `new`, no `For`), `CSharpScriptDocument<TGlobals>`, `CSharpScriptCompilation<TGlobals>`, `CSharpScriptAssembly<TGlobals>`. The session object decides what runs in it: it implements `IScriptGlobalsFactory<TGlobals, TInput>` (or `IScriptGlobalsFactory<TGlobals>`) per kind and creates each run's globals. `StartScriptAsync` is an extension method that requires the factory, so the compiler checks the session and the input and infers every type argument. `IScriptSessionObject` and the factories live in the runner package; the C# package knows nothing of sessions. `IScriptGlobals` is gone. Replaces #81 | Product owner: globals named a session even where they didn't need one, and the C# package should hold only the language server and common types. Globals now get only what they use, and editor-only globals are a plain class. Checked with a compiler: a wrong session, a wrong or missing input, an input for a kind without one, and editor-only globals don't compile |
| 96 | Callbacks and the end of a run | Documentation only (Q96). A run ends when its script's code has returned; script code handed to something longer-lived, such as a callback on the session object, can still be called and the library does not track it. The rule: a callback may only use what outlives its run. The sample keeps only callbacks the session owns, and its run-owned trigger gate is gone. In v1, script code does not cross the bridge. A library helper that closes and drains a run's callbacks can come later without breaking anything | Product owner: no special handling, but documentation. Most scripts are sequences that never hand code over. The gate took about 90 lines in every script API, and the sample's version had two defects. Replaces the ownership part of #84 |
| 97 | Exit reasons when causes race | `Exited` completes once, with one reason: the first cause the session acted on wins (`KillAsync` gives `Killed`, a missed `killSessionAfter` `StopTimedOut`, `DisposeTimeout` `DisposeTimedOut`, an orderly end `Disposed`); a process that ended by itself before any of them is `Crashed`, also during disposal. Runs and calls fail only after `Exited` has completed: the session holds back StreamJsonRpc's connection failures until it knows the exit, and kills a process whose connection broke but which does not exit | Disposing, killing, stop deadlines and crashes can meet. StreamJsonRpc fails pending calls on a broken pipe before the operating system reports the exit, so without holding back, code that catches `ConnectionLostException` couldn't learn why. Product owner's choice |
| 98 | Documents belong to one UI thread | Replaced by #100 | |
| 99 | Compiled scripts | A compiled script carries the emitted assembly with an embedded portable PDB, its own identity, the compiled text and its entry point, so it runs and its failures map without the server. The runner keeps, per session, which loaded assembly belongs to which compilation, so a failure in a callback from an earlier run still names its text. `#r`, `#load` and `#line` are compile errors in v1. A script's return value is ignored. Positions are as precise as the debug symbols: a statement | The spike for #52: a separate Roslyn process emitted scripts, and a JIT runner without any Roslyn assembly ran them, 14 compilations side by side. `#line` moved a reported position from line 2 to line 700, so remapping directives would break `ScriptSnapshot`. A runner with Roslyn loaded used about 40 MiB more working set |
| 100 | Documents and editors on the main UI thread | Documents belong to the app's main UI thread, wherever they were created; their events come there, and their changes are checked against it. Editors work only there, and fail at once on another thread. Servers raise `State` and send diagnostics there. Replaces #98 | AvaloniaEdit's editor checks `Dispatcher.UIThread`: its text view on every layout, its text area, caret, line list and segment collections. A fix may go upstream later. Product owner's choice |
| 101 | Tests as NativeAOT | Unit tests run with `dotnet test` and also as a NativeAOT app, from one project: xunit's AOT runner (`xunit.v3.aot`), Shouldly's assertions, and Avalonia's `HeadlessUnitTestSession` for the UI thread. Fixture processes are published as NativeAOT too | Failures that only NativeAOT has, such as trimmed code, reflection, native libraries and resources, show up in tests, not in apps. Shouldly supports NativeAOT from 5.0 on; `Avalonia.Headless.XUnit` doesn't work with the AOT runner. Product owner's choice |
| 102 | Budgets as benchmarks | Latency budgets are measured by benchmarks and compared per pull request, not asserted in unit tests | Product owner: latency depends on the hardware; flaky tests cost more than they protect. On a loaded 2-core machine, single editors took 129–144 ms to their first colored frame where a quiet one takes 23 ms, and a dispatcher stall of 59.5 ms appeared with no GC overlap |
| 103 | The first editor of a JIT app | The highlighting budget holds for every editor in NativeAOT apps, and from the second editor on in JIT apps. The first editor's cost in a JIT app is documented, not budgeted | Measured in 350 fresh processes: about 464 ms to the first colors, of which a plain AvaloniaEdit editor already takes about 440 ms, almost all JIT compilation of Avalonia and AvaloniaEdit. The editor's own TextMate setup takes about 28 ms on a background thread. ReadyToRun leaves about 150 ms after the app's first window. Product owner's choice |
| 104 | Search and replace | The editor's own search bar, of the app's controls, instead of AvaloniaEdit's search panel. It searches in the background, line by line, for text or a .NET regular expression, so matches don't span lines and `^` and `$` match at every line, whatever its line break. A search stops after 1 s and keeps 10,000 matches, and the bar says so; Replace All acts only on a complete result for the current text, query and replacement. Product owner's choice | AvaloniaEdit's panel creates its search strategy itself, searches on the UI thread at every change and keeps every match, so `(a+)+$` would freeze it as it froze the prototype. Measured on 5,000 lines (api.md section 9): 1.2 to 2.3 ms for 5,000 to 10,000 matches, and 0.1 s for Replace All of 10,000 |
| 105 | Indentation sizes | `CodeDocument.Indentation` takes sizes from 1 to 1,000 columns and throws `ArgumentOutOfRangeException` otherwise | AvaloniaEdit's own limits: without the check, an editor fails later, when it shows the document, far from the code that set the value. Product owner's choice |
| 106 | Long lines | The editor shows and edits lines up to 3,000 characters, AvaloniaEdit's default; longer lines collapse their tail. The limit rises once Avalonia's text wrapping and font fallback get faster on long lines | Measured with word wrap: a keystroke on a 5,000-character line took 36 ms in ASCII, 59 ms in Hebrew and 86 ms in CJK, most of it in Avalonia's wrapping and font fallback; without word wrap, 23 ms on 20,000 ASCII characters. Product owner's choice |

## 4. Lessons from the prototype

A performance audit of the prototype (2026-11-04) recommended keeping its foundation: AvaloniaEdit, Roslyn in its
own process, the separate runner, ReadyToRun helpers and the background highlighter. Stock TextMate highlighting
froze the UI for 2.5 s on a 20,000-character line; the background highlighter stayed under 5 ms. The audit's other
findings are contracts to design in from the start:

| Finding | Contract |
|---|---|
| Closing a window kept its editors alive as long as the language service lived | Documents have an owner and an end; editors hold nothing while out of the visual tree |
| The editor and the server split lines differently with CR-only line breaks; a huge column overflowed | One line-break rule (`\r\n`, `\n`, `\r`) and UTF-16 columns everywhere, with tests on both sides |
| A skipped long line passed a wrong lexical state on, so the lines after it were colored wrong | Lines of any length are tokenized within a time limit (50 ms in the background). A line that runs out of time has an unknown state, and the line after it starts from the start of the grammar |
| Outdated tokenizing kept running after an edit | Highlighting work stops between lines when the text changed |
| One grammar call can take longer than the UI thread's budget | Still open: grammar work on the UI thread is cooperative, so the first frame's colors have no hard deadline |
| The regex `(a+)+$` on 29 characters froze search for 21 s | Search has a time limit and a limit on the matches it keeps, and says so when it hits one; replace never acts on a partial result |
| One completion slot for all documents: a small tab waited 146–162 ms behind four large ones | The document an editor shows goes first |
| Unbounded message queues held 63 MB behind a blocked pipe | Queues have limits on both ends. Text changes always arrive in order; outdated feature requests are dropped or merged; responses and cancellations always get through |
| A benchmark wrapper reported success when runs failed; JIT and AOT demos measured different languages | Benchmarks fail loudly and compare JIT and AOT on the same source |
