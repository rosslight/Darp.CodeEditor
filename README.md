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
| `samples/Shared` | Sample documents, measurement scenarios and the Luau grammar shared by both demos. |
| `tests/Darp.CodeEditor.Tests` | Contract tests for the C# service and the completion matcher. |

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

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.
