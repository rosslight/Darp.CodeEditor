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
| Highlighted text | within 100 ms of creating the editor, without waiting for a language server |
| UI thread | no language work over 16 ms; tests fail above 50 ms |
| C# ready | within 1.5 s of a cold start, in JIT apps too |
| Completion | within 50 ms (p95) once warm, on scripts of 5,000 lines |

### 1.3 Scope of v1

**In:** highlighting, find and replace, editing helpers (auto-indent, bracket matching, auto-closing pairs, toggle
comment, Tab and Shift+Tab), completion, signature help, hover, diagnostics, any LSP server, C# scripts with a
host-defined environment, running scripts. Marks beside the scroll bar for errors and search matches, added once the
core works (#73).

**Not in v1:** Luau, rename, quick fixes, refactorings, find usages, formatting, analyzers, go-to-line, `#r` and
`#load`, scripts across files, regular C# files.

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
| `Darp.CodeEditor.CSharpScript` | package | C# scripts: environment, language server, compiling. Opt-in, since it brings Roslyn and a .NET runtime |
| `Darp.CodeEditor.CSharpScript.Protocol` | internal assembly, in `Darp.CodeEditor.CSharpScript` | What the app, the server and the runner exchange: the script environment, compiled scripts, the runner's calls |
| `Darp.CodeEditor.CSharpScript.Server` | executable, in `Darp.CodeEditor.CSharpScript` | Roslyn: the C# language server, and the compiler for the runner |
| `Darp.CodeEditor.CSharpScript.Runner` | package | Running compiled scripts: handlers, the app's services, deadlines, batching |
| `Darp.CodeEditor.CSharpScript.Runner.Process` | executable, in `Darp.CodeEditor.CSharpScript.Runner` | The process scripts run in. It holds no compiler |
| `Darp.CodeEditor.Luau` | package, after v1 | luau-lsp's native builds, opt-in |

Folders, not projects: highlighting, themes, popups, search and the LSP client inside `Darp.CodeEditor`.

The executables' names are placeholders. `Runner` is the package's name, so the runner's executable needs another one.

### 2.2 Processes

```
App (JIT or NativeAOT)
├─ Darp.CodeEditor ─────────────── LSP ───────────► any language server: yaml-language-server, luau-lsp, …
├─ Darp.CodeEditor.CSharpScript ── LSP, compile ──► C# script server (Roslyn), one per app
└─ Darp.CodeEditor.CSharpScript.Runner ── RPC ────► runner process (no Roslyn): loads compiled scripts, runs them

The C# server and the runner run on one .NET runtime that the app ships, with ReadyToRun.
```

### 2.3 Tests, samples and benchmarks

- **Tests:** one test project per package. Fixture projects only where a separate DLL or process is what a test checks.
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
| 52 | Who compiles | The C# server compiles scripts; the runner only loads and runs them, without Roslyn. Compiling runs beside the editor's requests on Roslyn's immutable snapshots: completion keeps answering while a script compiles. Both wait while the server starts or restarts. Needs a proof before the runner is built ([api.md](api.md#9-not-proven-yet)) | Roslyn ships once instead of twice (the runner's copy was 28 MiB); what runs is what the editor checked (R7); the runner restarts quickly after a kill. Replaces the compiling part of #33 and #36 |
| 53 | Benchmarks | Most benchmarks in a BenchmarkDotNet project | Product owner's choice |
| 54 | Restart | The prototype stays on the `draft` branch. `main` starts a new history | Product owner's choice |
| 64 | Editor properties | A setting is a property of `CodeEditor` when apps would set it differently per editor; one good value for every editor is built in; settings of the text belong to the document. Line numbers and the search bar are part of the editor's API without a use case asking for them | Product owner: a good editor experience counts even before a use case asks, taking the best from CodeMirror and Monaco |
| 65 | Selection | One `TextSelection` value with an anchor and a caret, not a range plus a caret offset | Product owner's choice; keeps a backward selection's direction, as Monaco and CodeMirror do |
| 66 | Indentation | On the document, with the language's default. Not detected from the text in v1 | Product owner: detection is easy to add later |
| 67 | Markers and view state | No markers from the app and no storable view state in v1 | Product owner's choice |
| 68 | Line and column numbers | `TextPosition` counts lines and columns from 1; offsets stay 0-based. The LSP client converts | Status bars and error messages show positions from 1, as Monaco and AvaloniaEdit count them |
| 69 | Color theme | One `ColorTheme` property instead of a light and a dark theme. Its default comes from the resource `CodeEditorColorTheme`, which the library defines per theme variant (Light+, Dark+) and apps override in their theme dictionaries. `CodeEditorTheme` is created in XAML with `Source` | Avalonia's own way: editors follow the variant through resource lookup, so `ThemeVariantScope` and custom variants need no API of ours. `Theme` is taken by Avalonia's control themes. Product owner's choice |
| 70 | What the color theme styles | The color theme styles the text area only. Completion, hover, signature help, the search bar and scroll bars take the app's look, from its flyout, tooltip and input styles; code inside popups keeps the editor's colors and font. One include, `CodeEditorStyles`, brings the control theme and AvaloniaEdit's styles | Token colors belong to their theme's background, as in AvaloniaEdit, Monaco and CodeMirror; popups then match Fluent and FluentAvalonia, font, padding and corners included. Product owner's choice |
| 71 | Errors and hover | Squiggles in the theme's colors; hover shows the diagnostics' messages above the server's Markdown, with code blocks highlighted by the editor's grammar | As in Monaco and CodeMirror. The prototype dropped emphasis and links |
| 72 | Completion icons | VS Code's codicons as vector shapes, in the theme's `symbolIcon.*` colors. Their CC BY 4.0 credit goes in the package's third-party notices | One icon set made for code symbols, with a clear license. Visual Studio's image library has no clear license to redistribute. Product owner's choice |
| 73 | Scroll bar marks | Marks for errors, warnings and search matches beside the scroll bar are in v1, but designed and added after the core | They add to the API without changing it. Product owner's choice |

## 4. Lessons from the prototype

A performance audit of the prototype (2026-11-04) recommended keeping its foundation: AvaloniaEdit, Roslyn in its
own process, the separate runner, ReadyToRun helpers and the background highlighter. Stock TextMate highlighting
froze the UI for 2.5 s on a 20,000-character line; the background highlighter stayed under 5 ms. The audit's other
findings are contracts to design in from the start:

| Finding | Contract |
|---|---|
| Closing a window kept its editors alive as long as the language service lived | Documents have an owner and an end; editors hold nothing while out of the visual tree |
| The editor and the server split lines differently with CR-only line breaks; a huge column overflowed | One line-break rule (`\r\n`, `\n`, `\r`) and UTF-16 columns everywhere, with tests on both sides |
| A skipped long line passed a wrong lexical state on, so the lines after it were colored wrong | Skipped lines have an unknown state, and highlighting recovers after them |
| Outdated tokenizing kept running after an edit | Highlighting work stops between lines when the text changed |
| One grammar call can take longer than the UI thread's budget | Still open: grammar work on the UI thread is cooperative, so the first frame's colors have no hard deadline |
| The regex `(a+)+$` on 29 characters froze search for 21 s | Search has a time limit and a limit on the matches it keeps, and says so when it hits one; replace never acts on a partial result |
| One completion slot for all documents: a small tab waited 146–162 ms behind four large ones | The document an editor shows goes first |
| Unbounded message queues held 63 MB behind a blocked pipe | Queues have limits on both ends. Text changes always arrive in order; outdated feature requests are dropped or merged; responses and cancellations always get through |
| A benchmark wrapper reported success when runs failed; JIT and AOT demos measured different languages | Benchmarks fail loudly and compare JIT and AOT on the same source |
