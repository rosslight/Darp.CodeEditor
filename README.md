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

```
Darp.CodeEditor.Lsp        NativeAOT-compatible; only the shared framework
  JsonRpcConnection        JSON-RPC 2.0 with Content-Length framing, for both ends of a connection
  LanguageServer           the server lifecycle: initialize, shutdown, exit
  Protocol/                source-generated DTOs for the subset of LSP the editor and its servers use
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
| `src/Darp.CodeEditor.Lsp` | JSON-RPC and LSP layer for language servers and the editor's LSP client (`IsAotCompatible`). |
| `tests/Darp.CodeEditor.Lsp.Tests` | Tests of the LSP layer, a toy server and client, and an interop test against luau-lsp. |
| `tests/Darp.CodeEditor.Lsp.AotSmoke` | NativeAOT build of the toy server and client, and of the luau-lsp check. |

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
   the state that all earlier messages left behind, and then runs alongside later messages.
3. A request whose token was cancelled by the time its handler returned answers `RequestCancelled`, whatever the
   handler returned. Handlers throw `JsonRpcException`, for example with `ContentModified`, to answer other errors.
4. Sending only queues a message. One write loop serializes and writes messages in the order they were sent, so
   callers never wait for serialization or a slow peer. A request cancelled before it went out is left out; otherwise
   `$/cancelRequest` follows it. Disposing drops what was not written yet.
5. `JsonRpcLimits` caps headers and messages; a longer message ends the connection. Null results are written as
   `"result": null`, and document URIs are kept as sent.

## Third-party content

`samples/Shared/Grammars/Luau.tmLanguage.json` is from
[JohnnyMorganz/Luau.tmLanguage](https://github.com/JohnnyMorganz/Luau.tmLanguage), MIT license; see the license file
next to it.
