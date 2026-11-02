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

Darp.CodeEditor.LanguageServices   the ILanguageService contract on its own (plain data, no UI)
```

A second editor surface is built for comparison (see [docs/webview-bakeoff.md](docs/webview-bakeoff.md)):

```
Darp.CodeEditor.Web        NativeAOT-compatible; Avalonia's NativeWebView hosting Monaco or CodeMirror 6
  WebCodeEditor            serves the pages from the app (app:// on WebKitGTK, a virtual host on WebView2), relays LSP
  web/                     the editor pages (TypeScript, bundled with esbuild); their LSP clients talk to the host

Darp.CodeEditor.Lsp        NativeAOT-compatible; serves an ILanguageService as an LSP server (in-process for now)
```

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
| `src/Darp.CodeEditor.CSharp` | C# script language service on Roslyn (JIT only). |
| `samples/Darp.CodeEditor.Demo` | JIT desktop app: C# script with completion, diagnostics, semantic colors, quick info, signature help; Luau and JSON with highlighting. |
| `samples/Darp.CodeEditor.Demo.Aot` | `PublishAot=true`, references only `Darp.CodeEditor`: Luau, JSON and C# highlighting, search, and a small AOT-safe JSON language service (word completion, parse errors). |
| `src/Darp.CodeEditor.LanguageServices` | The language service contract. |
| `src/Darp.CodeEditor.Lsp` | LSP server adapter over `ILanguageService` (`IsAotCompatible`). |
| `src/Darp.CodeEditor.Web` | WebView editor control and the Monaco and CodeMirror pages (`IsAotCompatible`). |
| `samples/Darp.CodeEditor.WebDemo` | JIT WebView demo: the C# script through the LSP adapter, Luau and JSON with highlighting. |
| `samples/Darp.CodeEditor.WebDemo.Aot` | NativeAOT WebView demo without Roslyn: Luau, C#, and JSON with the small JSON service over LSP. |
| `samples/Shared` | Sample documents, measurement scenarios and the Luau grammar shared by the demos. |
| `samples/WebShared` | Window and measurement orchestration of the WebView demos. |
| `tools/Darp.CodeEditor.Probe` | Measures from outside the app: input-to-screen latency, startup on screen, process-tree memory. |
| `tools/bakeoff` | Scripts that run the whole comparison on Linux and Windows, and the integration checks. |
| `tests/Darp.CodeEditor.Tests` | Contract tests for the C# service and the completion matcher. |

## Run

Requires the .NET 10 SDK. The WebView projects also need Node 22 with npm: the build runs `npm ci` and `npm run build`
in `src/Darp.CodeEditor.Web/web` when the page sources change. At run time they need the platform web engine:
WebKitGTK 4.1 on Linux (`libwebkit2gtk-4.1-0`), the WebView2 runtime on Windows.

```bash
# JIT demo
dotnet run --project samples/Darp.CodeEditor.Demo -c Release

# NativeAOT demo
dotnet publish samples/Darp.CodeEditor.Demo.Aot -c Release -r linux-x64
samples/Darp.CodeEditor.Demo.Aot/bin/Release/net10.0/linux-x64/publish/Darp.CodeEditor.Demo.Aot

# WebView demos (Monaco by default, --editor codemirror for CodeMirror)
dotnet run --project samples/Darp.CodeEditor.WebDemo -c Release -- --editor codemirror
dotnet publish samples/Darp.CodeEditor.WebDemo.Aot -c Release -r linux-x64

# Tests
dotnet test --project tests/Darp.CodeEditor.Tests
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
| `--e2e <file>` | Writes where line 1 of a C# document without language features ends on screen, for `Probe latency`, and stays open. |

The WebView demos take `--smoke`, `--bench`, `--out`, `--hold`, `--e2e`, `--no-language-service` and
`--skip-highlighting-bench` too, and:

| Flag | Effect |
| --- | --- |
| `--editor monaco\|codemirror` | Which browser editor to run. |
| `--light` | Starts with the light theme. |
| `--close-after <ms>` | Closes the window after that time, while the page may still be loading. |
| `--devtools` | Allows the web inspector. |
| `--webview-info` | Prints the web engine's own GPU and rendering report and exits. |
| `--page-query <a=b>` | Extra query parameters for the editor pages, for experiments. |

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.
