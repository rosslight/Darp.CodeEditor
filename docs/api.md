# API draft

Status: draft for discussion, 2026-11-05. Every public type and member of the v1 packages is declared, with its
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

// Run: compile the version the editor shows, run it, keep calling its handler.
var runner = new CSharpScriptRunner(new CSharpScriptRunnerOptions { Services = [CSharpScriptService.Create<IOutputLog>(log)] });
CSharpScriptCompilation compilation = await scripts.CompileAsync(document);
if (compilation.Succeeded)
{
    IPacketHandler handler = await runner.RunAsync<IPacketHandler>(compilation.Script);
    Reading reading = await handler.HandleAsync(packet);
}
```

| Package | Public types |
|---|---|
| `Darp.CodeEditor` | `CodeEditor`, `CodeDocument`, `CodeLanguage`, `LanguageServer`, `LanguageServerOptions`, `LanguageServerSettingsRequest`, `LanguageServerState`, `CodeEditorTheme`, `CodeEditorStyles`, `TextRange`, `TextPosition`, `TextSelection`, `Indentation`, `Diagnostic`, `DiagnosticSeverity` |
| `Darp.CodeEditor.CSharpScript` | `CSharpScriptServer`, `CSharpScriptServerOptions`, `CSharpScriptEnvironment`, `CSharpScriptCompilation`, `CompiledScript` |
| `Darp.CodeEditor.CSharpScript.Runner` | `CSharpScriptRunner`, `CSharpScriptRunnerOptions`, `CSharpScriptService`, `ScriptBatcher<TItem, TResult>`, `ScriptBatcherOptions`, `ScriptException`, `ScriptRunnerExitedEventArgs`, `ScriptRunnerExitReason` |

28 public types in all. Three more things are public without being types of ours:

- **The project file.**
  - `CSharpScriptReference="true"` on a package or project reference: scripts may use its assemblies. Publishing copies their DLLs next to the app, NativeAOT apps included.
  - `PublishReadyToRun=true`: publishing needs it to compile the C# helpers with ReadyToRun (prototype #48).
  - Later: a switch that copies XML docs for hover (prototype #38, #40).
- **StreamJsonRpc and PolyType** (Q58), for the script API:
  - its attributes: `[JsonRpcContract]` for the app's services, `[RpcMarshalable]` for handlers, `[GenerateShape]` on both and on the data they exchange;
  - the `IShapeable<T>` constraints on `RunAsync<T>` and `CSharpScriptService.Create<T>`. With them, NativeAOT apps call and copy these types without reflection, and the compiler rejects types without a shape;
  - its exceptions for failed handler calls.
- **The resource key `CodeEditorColorTheme`**: the color theme of editors that don't set `ColorTheme` (section 2).

## 2. The editor

A setting is a property of `CodeEditor` when apps would set it differently for different editors, such as a read-only
viewer and a script editor. Where one good value fits every editor, it is built in. Settings that describe the text
rather than its view belong to the document, as in Monaco and CodeMirror.

| Member | What it is |
|---|---|
| `Document` | the document the editor shows |
| `IsReadOnly`, `WordWrap`, `ShowLineNumbers` | view settings; line numbers are on by default |
| `ColorTheme` | the colors of the text area; by default from the `CodeEditorColorTheme` resource, so it follows the theme variant |
| `FontFamily`, `FontSize` | inherited from Avalonia; the default font is a monospace one, not the app's |
| `Selection` | a `TextSelection`: anchor and caret, so a backward selection keeps its direction |
| `CaretPosition` | the caret's line and column, read-only and bindable, for status bars |
| `ScrollTo(TextRange)` | brings a range into view: not at all when it is visible, centered when it is not |
| `OpenSearch(replace)` | the search or replace bar, for a Find button; Ctrl+F and Ctrl+H open them too |

**Built in, with no settings:** current-line highlight, bracket matching, auto-closing brackets and quotes,
auto-indent, whitespace shown inside the selection, scrolling past the last line, and the look of errors, hover and
completion below.

**Keys:** the editor handles only its own: editing, navigation, search (Ctrl+F, Ctrl+H, F3), completion (Ctrl+Space),
comments (Ctrl+/) and indentation (Tab, Shift+Tab). Other keys, such as F5 or Ctrl+S, reach the app's key bindings.

**Indentation** belongs to the document (`CodeDocument.Indentation`), so all editors of one document agree. It starts
with the language's default, such as 2 spaces for YAML, and is not detected from the text.

**Look.** The text and everything around it are styled differently, as in Monaco and CodeMirror:

- **The text area** takes its colors from `ColorTheme`, a VS Code color theme: syntax and language-server colors,
  background, selection, caret, line numbers, current line, search and bracket matches, squiggles. Token colors are
  made for their theme's background, so they come together.
- **Everything around the text** takes the app's look: completion, hover and signature help look like the app's
  flyouts and tooltips, and the search bar and scroll bars are the app's controls. They match Avalonia's Fluent
  theme, FluentAvalonia or any other theme, font, padding and corners included. Code inside popups keeps the editor's
  colors, background and font.
- **Popups stay inside the window**, as in the prototype (#19).

Apps add one line to `App.axaml`, after their theme. It brings AvaloniaEdit's styles along:

```xml
<Application.Styles>
  <FluentTheme />
  <ce:CodeEditorStyles />
</Application.Styles>
```

`ColorTheme` comes from the resource `CodeEditorColorTheme`, which `CodeEditorStyles` defines as Light+ for light
theme variants and Dark+ for dark ones. An app with its own colors overrides it where Avalonia's theme resources go:

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Dark">
    <ce:CodeEditorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-dark.json" />
  </ResourceDictionary>
  <ResourceDictionary x:Key="Light">
    <ce:CodeEditorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-light.json" />
  </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

So editors follow the app's theme variant through Avalonia's resource lookup, inside a `ThemeVariantScope` too. A
custom variant gets the colors of the variant it inherits from, unless the app adds an entry for it: FluentAvalonia's
high contrast inherits from Light, so it shows Light+ without one. To make one editor dark in a light app, wrap it in a
`ThemeVariantScope`: its popups turn dark too. Setting `ColorTheme` on the editor changes only the text area.

- **Errors:** squiggles in the theme's error, warning and info colors. Hovering shows the messages above the
  server's information.
- **Hover:** Markdown, with emphasis, inline code, lists and links. Code blocks are highlighted with the editor's
  grammar and colors.
- **Completion icons:** VS Code's codicons, drawn as vector shapes in the theme's `symbolIcon.*` colors, as in VS
  Code. Their designs are licensed CC BY 4.0: the package lists them in its third-party notices, and apps that show
  notices of their own include that entry.

**Before v1, additive:** marks beside the scroll bar for errors, warnings and search matches, as Monaco's overview
ruler shows them. Scripts of 5,000 lines need them to find errors outside the view. They add to the API without
changing it, so they wait until the core works.

**Later, when asked for:** whitespace display modes, rulers, folding, a minimap, placeholder text for empty
documents, indentation detected from the text, markers that the app adds, and a view state the app can store.

## 3. Who owns what

| Object | Created by | Shared by | Ends with |
|---|---|---|---|
| `LanguageServer`, `CSharpScriptServer` | the app, once (a singleton) | all documents of its languages | `DisposeAsync`, at app exit |
| `CodeLanguage` | the app, once per language or script environment | documents | nothing to dispose |
| `CodeDocument` | the app, usually a view model, one per script or file | the editors that show it | `Dispose`, when the user closes it for good |
| `CodeEditor` | the view | | the visual tree; out of it, the editor drops its subscriptions, requests and rendering resources |
| `CSharpScriptRunner` | the app, once per isolation boundary | the scripts and handlers in its process | `DisposeAsync` |
| The runner's services | the app | all runs of the runner | the runner's `DisposeAsync` |
| A handler from `RunAsync<T>` | the runner | | `Dispose`, or the end of its runner's process |
| `CompiledScript` | `CompileAsync` | runs | nothing to dispose; it outlives the server |

## 4. Threads

- **UI thread only:** `CodeEditor` and `CodeDocument`, which raise their events there. The same goes for `CompileAsync(CodeDocument)`, because it takes the document's version when called.
- **Immutable, any thread:** `CodeLanguage` and `CodeEditorTheme`.
- **Any thread:** `LanguageServer`, `CSharpScriptServer`, `CSharpScriptRunner` and `ScriptBatcher`.
  - `LanguageServer.StateChanged` is posted to the UI thread for status indicators. Nothing waits for it, so servers and compiling also work before the UI runs.
  - The runner's `Exited` and `Output`, and calls into the app's services, come on any thread.
- **No blocking:** nothing in the API makes the UI thread wait for a server or a script. Creating and disposing documents only queues work for the server.

## 5. Positions

Offsets count UTF-16 code units from 0. Lines end at `\r\n`, `\n` or `\r`, alike in the editor, the LSP client and
the C# server. `TextPosition` counts lines and columns from 1, as editors show them; the LSP client converts to the
protocol's 0-based positions. Ranges are not checked when created; members that take them clamp them to the text.

## 6. Language servers

"Any LSP server" means any server that works with this subset of the protocol, which the client implements:

- **Lifecycle:** `initialize` (with the root folder and initialization options), shutdown and exit, and `$/cancelRequest`.
- **Documents:** incremental `didOpen`, `didChange` and `didClose`.
- **Features:** completion with resolve, hover, signature help, semantic tokens (range, or full), and pushed diagnostics (`publishDiagnostics`).
- **Server requests the client answers:** `workspace/configuration` (through `Settings`) and `client/registerCapability`. Registrations are acknowledged, and only the features above take effect.
- **Not in v1:** pulled diagnostics, workspace edits, file watching, code actions, formatting, rename. The client does not advertise them.

A server that needs pulled diagnostics would add them to v1. The client advertises only what it implements, so
servers fall back where they can.

## 7. Failures in the runner

| What happens | `RunAsync` | A call on a handler |
|---|---|---|
| The script throws | `ScriptException` | `ScriptException` |
| The caller's token is cancelled | `OperationCanceledException`; the process is killed if the script does not stop within the grace period | `OperationCanceledException` |
| The call takes longer than `CallTimeout` | no limit for runs | `TimeoutException`; the process is killed if the script does not stop within the grace period |
| The process ends (crash or kill) | `ConnectionLostException` | `ConnectionLostException`; `Exited` says why |

`ScriptException` derives from StreamJsonRpc's `RemoteInvocationException`. Handler calls go through StreamJsonRpc's
proxies, and those can only turn a script's failure into one of these.

## 8. Not public, on purpose

- **The LSP layer.** Protocol types, JSON-RPC and process plumbing are internal. Languages plug in through
  `LanguageServerOptions`: a command line, initialization options and settings answers.
- **A language service interface.** The prototype's public `ILanguageService` is gone: every language comes through a
  language server. An in-process service could come back later, if a language needs one.
- **AvaloniaEdit.** `CodeEditor` wraps AvaloniaEdit's editor instead of deriving from it, so AvaloniaEdit's API is not
  part of ours. `CodeEditorStyles` includes its styles.
- **Popups and policies.** Completion, hover and signature help have no settings yet, and their parts have no style
  classes for apps to restyle. They take the app's look instead. Restart and hang timeouts keep
  the prototype's defaults.
- **Timings.** The prototype's timing properties and events become `ActivitySource` and `Meter` instruments named
  `Darp.CodeEditor`, which benchmarks and `dotnet-counters` read. They carry document versions, queue ages and
  waits, so that delays can be traced.

## 9. Not proven yet

Decision #52 has the C# server compile and the runner run the result without Roslyn. The prototype's runner ran
scripts through Roslyn Scripting, so this is new. It needs a spike before the runner is built:

- emit from the server's snapshot;
- load the assembly, with a unique identity per compilation;
- call the script's entry point with its globals;
- await its result, for synchronous and awaited scripts;
- return handlers;
- show line numbers in exceptions;
- unload old scripts.

Roslyn's script assemblies make this possible in principle: they have a factory method that takes the globals.
Nobody has done it here yet.

## 10. Open questions

Question numbers continue the decision log in [design.md](design.md). Decisions taken in the API discussion are
recorded there too (#64–#73).

- **Q55 – Logging.**
  - **Draft:** takes `Microsoft.Extensions.Logging.ILogger`.
  - **Your apps:** they log with Serilog's own `ILogger`, so they'd pass `new SerilogLoggerFactory(logger).CreateLogger(...)` from Serilog.Extensions.Logging.
  - **Alternatives:** a plain `Action<string>`, or Serilog's interface.
  - **Recommended:** Microsoft's `ILogger`, the usual choice for libraries.
- **Q56 – When a document opens in its server.**
  - **Draft:** at creation, until `Dispose`, whether shown or not. Then error counts across tabs work, and so does Run without showing the script.
  - **Cost:** the server also analyzes hidden documents. The editor's focused document goes first, and hidden documents get their diagnostics later.
  - **Alternative:** open a document only while an editor shows it.
  - **Recommended:** at creation.
- **Q57 – Caret and scroll per document.**
  - **Draft:** a document remembers the caret, selection and first visible line of the editor that showed it last. The next editor starts there.
  - **Several editors at once:** each keeps its own state while it shows the document.
  - **Alternative:** the app keeps that state.
  - **Recommended:** as drafted.
- **Q58 – StreamJsonRpc in the script API.**
  - **Draft:** script API assemblies reference StreamJsonRpc and use its attributes, its shape constraints and its exceptions (sections 1 and 7).
  - **Alternative:** hiding it would take our own attributes, source generator and a wrapper around every handler.
  - **Recommended:** accept it for v1.
- **Q59 – Running scripts without the editor's UI.**
  - **Today:** the runner package depends on the C# package, and through it on the editor package and Avalonia. A console app can reference those, so it would work.
  - **What matters:** nothing in compiling and running may wait for Avalonia's UI thread. The draft makes sure of that.
  - **Recommended:** keep one dependency chain until an app without Avalonia needs scripts. Then the compiler moves into a package of its own.
- **Q60 – Storing compiled scripts.** A `CompiledScript` that can be saved and loaded would let later runs skip Roslyn entirely, for scripts that rarely change. **Recommended:** after v1.
- **Q61 – Bundled grammars.**
  - **The problem:** VS Code's grammars come in one 6.5 MB DLL, and NativeAOT can't trim parts of it.
  - **Options:** ship it whole, or only a few grammars (C#, JSON, XML, YAML, Lua) and take the rest from files.
  - **Recommended:** a few, plus `CodeLanguage.FromFiles` for the others.
- **Q62 – No `Text` property on the editor.** Text goes through the document only, so no binding copies the whole text on every keystroke. **Recommended:** as drafted.
- **Q63 – Services per runner or per run.**
  - **Draft:** services are registered once per runner, as in the prototype. Scripts in one runner share them, so a runner per device gives each device's scripts their own station.
  - **Alternative:** services per run. StreamJsonRpc's analyzer then requires every service interface to extend `IDisposable`, and the runner releases a run's services with its handler.
  - **Recommended:** per runner, unless your apps need different services within one runner.
