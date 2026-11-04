# API draft

Status: draft for discussion, 2026-11-04. Every public type and member of the v1 packages is declared, with its
documentation, but nothing is implemented. [`samples/Darp.CodeEditor.UseCases`](../samples/Darp.CodeEditor.UseCases)
compiles against the draft: one file per scenario of a host app, each with the questions it raises. Write your own
scenarios there to try the API.

## 1. The model

```csharp
// Any language: a grammar, editing rules and, optionally, a language server.
var yamlServer = new LanguageServer(new LanguageServerOptions { FileName = "yaml-language-server", Arguments = ["--stdio"] });
CodeLanguage yaml = CodeLanguage.Bundled("yaml", yamlServer);

// C# scripts: one server for the app, one language per kind of script.
var scripts = new CSharpScriptServer();
CodeLanguage packetScripts = scripts.CreateLanguage(new CSharpScriptEnvironment
{
    GlobalsType = typeof(PacketScriptGlobals),
    Imports = ["System", "System.Linq", "MyApp.ScriptApi"],
});

// The app owns documents; editors show them.
var document = new CodeDocument(packetScripts, text);
editor.Document = document;

// Run: compile what the editor shows, run it in the runner, keep calling its handler.
CSharpScriptCompilation compilation = await scripts.CompileAsync(document);
if (compilation.Succeeded)
{
    IPacketHandler handler = await runner.RunAsync<IPacketHandler>(compilation.Script, [CSharpScriptService.Create(log)]);
    Reading reading = await handler.HandleAsync(packet);
}
```

| Package | Public types |
|---|---|
| `Darp.CodeEditor` | `CodeEditor`, `CodeDocument`, `CodeLanguage`, `LanguageServer`, `LanguageServerOptions`, `LanguageServerState`, `CodeEditorTheme`, `TextRange`, `TextPosition`, `Diagnostic`, `DiagnosticSeverity` |
| `Darp.CodeEditor.CSharpScript` | `CSharpScriptServer`, `CSharpScriptServerOptions`, `CSharpScriptEnvironment`, `CSharpScriptCompilation`, `CompiledScript` |
| `Darp.CodeEditor.CSharpScript.Runner` | `CSharpScriptRunner`, `CSharpScriptRunnerOptions`, `CSharpScriptService`, `ScriptBatcher<TItem, TResult>`, `ScriptBatcherOptions`, `ScriptException`, `ScriptRunnerExitedException`, `ScriptRunnerExitedEventArgs`, `ScriptRunnerExitReason` |

25 public types in all. The project file is part of the API too:

- `CSharpScriptReference="true"` on a package or project reference: scripts may use its assemblies, and publish copies
  their DLLs next to the app, NativeAOT apps included.
- `PublishReadyToRun=true`: needed so that publish can compile the C# helpers with ReadyToRun (prototype #48).
- Later: a switch that copies XML docs for hover (prototype #38, #40).

## 2. Who owns what

| Object | Created by | Shared by | Ends with |
|---|---|---|---|
| `LanguageServer`, `CSharpScriptServer` | the app, once (a singleton) | all documents of its languages | `DisposeAsync`, at app exit |
| `CodeLanguage` | the app, once per language or script environment | documents | nothing to dispose |
| `CodeDocument` | the app, usually a view model, one per script or file | the editors that show it | `Dispose`, when the user closes it for good |
| `CodeEditor` | the view | | the visual tree; out of it, the editor holds nothing |
| `CSharpScriptRunner` | the app, once per isolation boundary | the scripts and handlers in its process | `DisposeAsync` |
| A handler from `RunAsync<T>` | the runner | | `Dispose`, or the end of its runner's process |

## 3. Threads

- `CodeEditor` and `CodeDocument`: UI thread only. They raise their events on the UI thread.
- `CodeLanguage` and `CodeEditorTheme`: immutable, any thread.
- `LanguageServer`, `CSharpScriptServer`, `CSharpScriptRunner` and `ScriptBatcher`: any thread.
  `LanguageServer.StateChanged` comes on the UI thread, for status indicators; the runner's `Exited` and `Output`
  come on any thread.
- Nothing in the API makes the UI thread wait for a server or a script.

## 4. Positions

Offsets count UTF-16 code units. Lines end at `\r\n`, `\n` or `\r`. Lines and columns count from 0. The same rule holds
in the editor, in the LSP client and in the C# server.

## 5. Not public, on purpose

- **The LSP layer.** Protocol types, JSON-RPC and process plumbing are internal. Languages plug in through
  `LanguageServerOptions`: a command line, initialization options and settings answers.
- **A language service interface.** The prototype's public `ILanguageService` is gone: every language comes through a
  language server. An in-process service could come back later, if a language needs one.
- **AvaloniaEdit.** `CodeEditor` wraps AvaloniaEdit's editor instead of deriving from it, so AvaloniaEdit's API is not
  part of ours.
- **Popups and policies.** Completion, hover and signature help have no settings yet. Restart and hang timeouts keep
  the prototype's defaults.
- **Timings.** The prototype's timing properties and events become `ActivitySource` and `Meter` instruments named
  `Darp.CodeEditor`, which benchmarks and `dotnet-counters` read.

## 6. Open questions

Question numbers continue the decision log in [design.md](design.md).

- **Q55 – Logging.** The draft takes `Microsoft.Extensions.Logging.ILogger`. Your apps log with Serilog's own `ILogger`;
  they would pass `new SerilogLoggerFactory(logger).CreateLogger(...)` from Serilog.Extensions.Logging. Alternatives: a
  plain `Action<string>`, or Serilog's interface. **Recommended:** Microsoft's `ILogger`, the usual choice for
  libraries.
- **Q56 – When a document opens in its server.** The draft opens it at creation, until `Dispose`, shown or not. Error
  counts across tabs and Run without showing the script then work. The cost: the server analyzes hidden documents
  too, so it must put shown ones first. Alternative: open only while an editor shows it. **Recommended:** at creation.
- **Q57 – Caret and scroll per document.** The draft's editor brings back a document's caret, selection and scroll
  position when it shows that document again, kept inside the document. Alternative: the app keeps that state, or
  each tab keeps its own editor. **Recommended:** as drafted.
- **Q58 – The script API depends on StreamJsonRpc.** Script API assemblies mark their interfaces with StreamJsonRpc's
  `[JsonRpcContract]` and `[RpcMarshalable]` and PolyType's `[GenerateShape]`, and reference StreamJsonRpc. Hiding it
  would take our own attributes and source generator. **Recommended:** accept it for v1.
- **Q59 – Avalonia for running scripts.** The runner package depends on the C# package, which depends on the editor
  package and so on Avalonia. An app without Avalonia, such as a console tool, could not run scripts then.
  **Recommended:** accept it until such an app exists. Then the compiler moves into an Avalonia-free package.
- **Q60 – Storing compiled scripts.** A `CompiledScript` that can be saved and loaded would let later runs skip Roslyn
  entirely, for scripts that rarely change. **Recommended:** after v1.
- **Q61 – Bundled grammars.** VS Code's grammars come in one 6.5 MB DLL, and NativeAOT cannot trim parts of it. The
  editor could ship it whole, or only a few grammars (C#, JSON, XML, YAML, Lua) and take the rest from files.
  **Recommended:** a few, plus `CodeLanguage.FromFiles` for the others.
- **Q62 – No `Text` property on the editor.** Text goes through the document only, so no binding copies the whole
  text on every keystroke. **Recommended:** as drafted.
