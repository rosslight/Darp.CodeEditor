# API draft

Status: draft for discussion, 2026-11-05. Every public type and member of the v1 packages is declared, with its
documentation, but nothing is implemented. [`samples/Darp.CodeEditor.UseCases`](../samples/Darp.CodeEditor.UseCases)
compiles against the draft: one file per scenario of a host app, each with the questions it raises. Write your own
scenarios there to try the API.

## 1. The model

```csharp
// A language is syntax: a grammar and editing rules. Without a server, a document is just colored text.
CodeDocument config = CodeDocument.Create(CodeLanguage.Json, json);

// A language server creates the documents it serves.
var yamlServer = new LanguageServer(new LanguageServerOptions { FileName = "yaml-language-server", Arguments = ["--stdio"] });
CodeDocument settings = yamlServer.CreateDocument(CodeLanguage.Yaml, yaml, filePath);

// C# scripts: one server for the app; an environment per kind of script says what its scripts see. Its type says
// which session object and which run input the kind's globals need.
var scripts = new CSharpScriptServer();
CSharpScriptEnvironment<Session, RunInput> environment =
    CSharpScriptEnvironment.For<ScriptGlobals, Session, RunInput>() with { Imports = ["MyApp.ScriptApi"] };
CSharpScriptDocument<Session, RunInput> document = scripts.CreateDocument(environment, text);  // a CodeDocument

// The app owns documents; editors show them.
editor.Document = document;

// Run: a session is a runner process with a bridge to the app. Run the version the editor shows in it.
await using CSharpScriptSession<ISessionSide, Session> session =
    await CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(appSide, ct);
await session.Remote.InitializeAsync("bench 1", ct);    // the script API's own setup, before scripts run
CSharpScriptCompilation<Session, RunInput> compilation = await document.CompileAsync();
if (compilation.Succeeded)
{
    // A script for another kind of session, or the wrong input, does not compile here.
    CSharpScriptRun run = await session.StartScriptAsync(compilation.Script, new RunInput("run 1", "bench 1"), ct);
    await run.Completion;                                 // the run has ended; its calculation stays in the session
    double result = await session.Remote.CalculateAsync(21, ct);
}
```

| Package | Public types |
|---|---|
| `Darp.CodeEditor` | `CodeEditor`, `CodeDocument`, `CodeLanguage`, `LanguageServer`, `LanguageServerOptions`, `LanguageServerSettingsRequest`, `LanguageServerState`, `CodeEditorTheme`, `CodeEditorStyles`, `TextSnapshot`, `TextRange`, `TextPosition`, `TextSelection`, `Indentation`, `Diagnostic`, `DiagnosticSeverity` |
| `Darp.CodeEditor.CSharpScript` | `CSharpScriptServer`, `CSharpScriptServerOptions`, `CSharpScriptEnvironment`, `CSharpScriptEnvironment<TSession, TInput>`, `CSharpScriptDocument<TSession, TInput>`, `CSharpScriptCompilation<TSession, TInput>`, `CSharpScriptAssembly<TSession, TInput>`, `CSharpCodeLanguage` (adds `CodeLanguage.CSharp`), and the contracts that runner-side script APIs implement: `IScriptSession<TSelf, TAppSide>`, `IScriptGlobals<TSelf, TSession, TInput>`, `IScriptGlobals<TSelf, TSession>` |
| `Darp.CodeEditor.CSharpScript.Runner` | `CSharpScriptSession`, `CSharpScriptSession<TSessionSide, TSession>`, `CSharpScriptSessionOptions`, `CSharpScriptRun`, `CSharpScriptException`, `CSharpScriptSessionExit`, `CSharpScriptSessionExitReason` |

34 public types in all. Three more things are public without being types of ours:

- **The project file.**
  - `CSharpScriptReference="true"` on a package or project reference: scripts may use its assemblies. Publishing copies their DLLs next to the app, NativeAOT apps included, with their XML docs, which hover and completion show.
  - `PublishReadyToRun=true`: publishing needs it to compile the C# helpers with ReadyToRun (prototype #48).
- **StreamJsonRpc and PolyType** (Q58), for the script API:
  - its attributes: `[JsonRpcContract]` and `[GenerateShape]` on the two interfaces of a session's bridge, `[GenerateShape]` on the data they exchange, and `[assembly: ExportRpcContractProxies]` (section 7 has the checklist);
  - the `IShapeable<T>` constraints on `CSharpScriptSession.StartAsync` and on a run's input. With them, NativeAOT apps call the bridge and copy the input without reflection, and the compiler rejects types without a shape;
  - its exceptions for calls that fail, such as `ConnectionLostException`.
- **The resource key `CodeEditorColorTheme`**: the color theme of editors that don't set `ColorTheme` (section 2).

**Languages.** A `CodeLanguage` says what kind of text a document holds: a TextMate grammar for colors and VS Code's
editing rules (comments, brackets, auto-closing pairs). Servers and script environments are not part of it.

- **Built in:** `CodeLanguage.PlainText`, `Json`, `Xml` and `Yaml`. Packages add theirs as extension members:
  `CodeLanguage.CSharp` from the C# package, and Luau's from its package later. IntelliSense lists them all.
- **The app's own:** made with the same public constructor as the built-in ones, from a VS Code extension's files.
  They can ship as Avalonia resources, so NativeAOT apps need no loose files:

  ```csharp
  var toml = new CodeLanguage(
      "toml",
      grammar: new Uri("avares://MyApp/Languages/toml.tmLanguage.json"),
      configuration: new Uri("avares://MyApp/Languages/toml.language-configuration.json"));
  ```

- **Servers create their documents:** `yamlServer.CreateDocument(CodeLanguage.Yaml, text, filePath)` for any LSP
  server, which may serve several languages. The C# server's `CreateDocument(environment, text)` needs no language:
  it is always `CodeLanguage.CSharp`, and it returns a `CSharpScriptDocument` typed by its kind of script, which
  compiles itself. `CreateDocument(text)` without an environment makes a C# document for editing only.
  `CodeDocument.Create` creates documents without a server. Apps don't derive from `CodeDocument`. Headless compiling
  takes the environment, and needs no grammar.
- **C# 14:** `CodeLanguage.CSharp` is an extension member, so apps need C# 14, the default for .NET 10. XAML's
  `x:Static` can't reach it; script documents are created in code.
- **Loading:** a language starts loading its files in the background when it is created, so there is nothing to
  preload. Themes do the same. Loading doesn't use Avalonia's services, so languages and themes created before the
  app's `AppBuilder` runs load too. A missing or invalid file leaves documents uncolored, and the error goes to
  Avalonia's log, as binding errors do. Apps see it once they set up a log sink, such as `LogToTrace()`.

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
| `Cut()`, `Copy()`, `Paste()`, `SelectAll()` | for an app's Edit menu; the keys and the editor's context menu do the same, and the document's `Undo` and `Redo` the rest |

**Built in, with no settings:** current-line highlight, bracket matching, auto-closing brackets and quotes,
auto-indent, whitespace shown inside the selection, scrolling past the last line, a context menu with Cut, Copy, Paste
and Select All, and the look of errors, hover and completion below.

**Editing in code:** `document.Replace(range, text)` inserts or replaces in one step the user can undo, such as a
template at the caret: `document.Replace(editor.Selection.Range, snippet)`. Setting `Text` replaces everything.

**Keys:** the editor handles only its own: editing, navigation, search (Ctrl+F, Ctrl+H, F3), completion (Ctrl+Space),
comments (Ctrl+/) and indentation (Tab, Shift+Tab). Other keys, such as F5 or Ctrl+S, reach the app's key bindings.

**Indentation** belongs to the document (`CodeDocument.Indentation`), so all editors of one document agree. It starts
as in VS Code, 2 spaces for YAML and 4 otherwise, and is not detected from the text.

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
| `CodeLanguage` | built in, or the app once per language | documents | nothing to dispose |
| `CodeDocument` | the app, usually a view model, one per script or file: with `CodeDocument.Create`, or a server's `CreateDocument` | the editors that show it | `Dispose`, when the user closes it for good |
| `CodeEditor` | the view | | the visual tree; out of it, the editor drops its subscriptions, requests and rendering resources |
| `CSharpScriptSession` | the app, once per isolation boundary | the scripts it runs | `DisposeAsync`, or `KillAsync` |
| The app side of a session's bridge | the app | the session's scripts | the app, once `DisposeAsync` or `KillAsync` has returned |
| The session object | the session, in its process | the app, through `Remote`, and the session's scripts | the session's `DisposeAsync`; lost on a kill or crash |
| A run's globals | the session, per run | that run's script | the end of the run: the session disposes them before `Completion` |
| A run's input | the app | that run's globals | the run; it is copied into the runner |
| `CSharpScriptRun` | `StartScriptAsync` | | nothing to dispose |
| `CSharpScriptAssembly<TSession, TInput>` | `CompileAsync` | runs in sessions of its kind | nothing to dispose; it outlives the server |

## 4. Threads

- **UI thread only:** `CodeEditor`.
- **Documents:** create, read, compile and dispose them on any thread. Change them only on the UI thread, where the user types and editors draw: setting `Text`, `FilePath` or `Indentation`, `Undo`, `Redo` and `MarkUnmodified` throw elsewhere. Their events come on the UI thread, also for documents created elsewhere.
  - `CurrentSnapshot` is the text with its version, immutable. Reading `Text` and the version separately could pair one version's text with the next one's number.
  - The document makes a snapshot after every change, sharing the unchanged parts of the text. In AvaloniaEdit, an edit plus its snapshot takes about 4 µs on 5,000 lines.
  - `document.CompileAsync()` compiles the snapshot current at the call, and the compilation keeps it as `Snapshot`.
- **Immutable, any thread:** `CodeLanguage`, `CodeEditorTheme` and `TextSnapshot`.
- **Any thread:** `LanguageServer`, `CSharpScriptServer`, `CSharpScriptSession` and `CSharpScriptRun`.
  - Both servers raise `PropertyChanged` for `State` on the UI thread, for a status indicator to bind. Nothing waits for it, so servers and compiling also work before the UI runs.
  - Both servers dispose synchronously too: `Dispose` kills the process at once, for app exits where only synchronous disposal runs. Sessions are only async-disposable, because the order in which they end matters.
  - A session's `Output`, and calls on the app side of its bridge, come on any thread. Calls on the session object come on any thread in the runner, from the app and from overlapping scripts.
- **No blocking:** nothing in the API makes the UI thread wait for a server or a script. Creating and disposing documents only queues work for the server.

## 5. Positions

Offsets count UTF-16 code units from 0. Lines end at `\r\n`, `\n` or `\r`, alike in the editor, the LSP client and
the C# server. `TextPosition` counts lines and columns from 1, as editors show them; the LSP client converts to the
protocol's 0-based positions. `TextSnapshot.GetPosition` and `GetOffset` convert, in the text of that snapshot, and
`GetText(range)` reads a part of it. Ranges are not checked when created; members that take them clamp them to the
text.

Ranges refer to the text they were made for: a diagnostic's to `DiagnosticsSnapshot`, a compile error's to the
compilation's `Snapshot`, a failure's `ScriptPosition` to the compiled text. `document.MapToCurrent(snapshot, range)`
follows the edits made since, so clicking an error after typing above it still lands on it.

## 6. Language servers

"Any LSP server" means any server that works with this subset of the protocol, which the client implements:

- **Lifecycle:** `initialize` (with the root folder and initialization options), shutdown and exit, and `$/cancelRequest`.
- **Documents:** incremental `didOpen`, `didChange` and `didClose`.
- **Features:** completion with resolve, hover, signature help, semantic tokens (range, or full), and pushed diagnostics (`publishDiagnostics`).
- **Server requests the client answers:** `workspace/configuration` (through `Settings`) and `client/registerCapability`. Registrations are acknowledged, and only the features above take effect.
- **Not in v1:** pulled diagnostics, workspace edits, file watching, code actions, formatting, rename. The client does not advertise them.

A server that needs pulled diagnostics would add them to v1. The client advertises only what it implements, so
servers fall back where they can.

## 7. Sessions

Draft for review; not proven yet (section 9).

A **session** is one runner process, with a bridge between the app and its scripts, and one session object that the
session's scripts share. The script API defines the bridge as two interfaces, one per direction, both
`[JsonRpcContract]` and `[GenerateShape]`:

- **The app side** (`IAppSide` in the use cases): the app implements it; scripts call it.
- **The session side** (`ISessionSide`): the session object implements it in the runner; the app calls it through
  `session.Remote`.

```csharp
await using CSharpScriptSession<ISessionSide, Session> session =
    await CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(appSide, ct);
await session.Remote.InitializeAsync("bench 1", ct);                        // the script API's own setup

CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("run 1", "bench 1"), ct);  // ct: only the start
await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));       // its end; stop stops it
await run.StopAsync();                                                      // or stop it now, and wait for its end

await session.KillAsync();                                                  // forced: the process and every run
```

**Two sides of a script API.** Only the bridge crosses the process boundary: its methods are async and copy their
data. Everything else runs in the runner as ordinary .NET objects, with no attributes and no copying: the session
object, the globals, and the drivers and helpers they use. Keep the bridge in a project of its own, analysed for
NativeAOT like the app, and the runner side in another, which is not: its drivers may use reflection or native
interop. The runner side references the C# package for `IScriptSession` and `IScriptGlobals`; that brings the editor
and Avalonia into its build, but nothing of them loads in the runner. The app names runner-side types (the session
type, the globals types) without compiling any of their code: in a test, naming them added about 700 bytes of
metadata to a NativeAOT app. Their dependencies must still resolve when the app is published.

**Writing a script API.** The checklist, which the use cases' sample follows:

1. A bridge project, analysed for NativeAOT, referencing StreamJsonRpc: the app side and the session side as
   `partial` interfaces with `[JsonRpcContract]` and `[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]`,
   their data as `[GenerateShape]` records, and `[assembly: ExportRpcContractProxies]`. Without that last line, a JIT
   app works and a NativeAOT app fails on its first call; `StartAsync` therefore checks for it in every build.
2. A runner-side project, not analysed for NativeAOT, referencing the bridge and the C# package: the session type
   with `IScriptSession` and the globals types with `IScriptGlobals`, each with a private constructor and a static
   `Create`. A script API without reflection-heavy drivers may be a single project.
3. In the app, both projects referenced with `CSharpScriptReference="true"`, and an environment per kind of script
   made with `CSharpScriptEnvironment.For`.

**The session object.** `StartAsync` starts the process and creates one instance of the session type there, through
its static `Create` from `IScriptSession<TSelf, TAppSide>`, which gets the app side. The third type argument names
that type, so the compiler checks that it implements the session side and takes this app side. The session object holds what the session's scripts share, such as
connections and state, and owns what must outlive a run. Setup, such as connecting a device, is the script API's own
methods on the session side, which the app calls before it starts scripts.

**Runs.** `StartScriptAsync` returns once the runner has accepted the run, even if its token was cancelled meanwhile,
so no run is left without a handle. Creating the globals is part of the run: a `Create` that hangs is stopped like
the script, and one that throws ends the run's `Completion`. A run:

- **brings its own environment,** so scripts of one session can see different globals. The globals implement
  `IScriptGlobals<TSelf, TSession, TInput>`, whose static `Create` takes the session object, the run's input and its
  stop token; `IScriptGlobals<TSelf, TSession>` is the same without input. `CSharpScriptEnvironment.For<TGlobals,
  TSession, TInput>()` makes the environment, and its type travels through the document, the compilation and the
  compiled script to `StartScriptAsync`. So the compiler rejects a script started in another kind of session or with
  the wrong input, and there is no constructor convention to get wrong. Each kind of globals shows the scripts only
  its part of the session object, through its own members.
- **gets its own input,** such as its identity: data of a `[GenerateShape]` type that the app passes to
  `StartScriptAsync` and the globals receive. Overlapping runs can mark their output with it. Runs without input use
  `ValueTuple` as their input type, and the overload without an input.
- **may overlap** with other runs of the session.
- **lasts as long as its code.** When the code has finished, for any reason, the session cancels the run's stop token,
  disposes its globals if they are disposable, while the bridge is still up, and then completes `Completion`.
- **owns, through its globals, what it started.** Disposing the globals ends it: what the run registered stops taking
  calls, the calls already running are cancelled and awaited, and queued values are sent. The library cannot see
  inside the globals, so this holds as far as the script API keeps it; the use cases' sample shows how. What must
  outlive a run, such as a calculation the app keeps calling, belongs to the session object; scripts that should keep
  answering, such as triggers, wait until they are stopped. A kill runs no cleanup and delivers nothing still
  queued.

**Readiness and threads.** Calls from the app and from overlapping scripts reach the session object on any thread,
and the script API makes it safe for that. The library does not serialise them: a script may wait for the app while
the app calls back. When the app calls before a script has registered what the call needs, the session object
throws. Where the app needs to wait for a script instead, the script API says so, for example with a notification
once the script has registered.

**Stopping.** `run.StopAsync()` cancels the run's stop token and waits for its end; it does not throw for how the run
ended, so the app observes `Completion` for that. `run.RunToEndAsync(stopToken)` waits for the end; before stopping
begins, it throws what `Completion` throws, and once its token has stopped the run, it throws
`OperationCanceledException`, while the run's outcome stays in `Completion`. To only stop waiting, without stopping
the run, use `run.Completion.WaitAsync(token)`. Both wait as long as the script takes, unless the app passes
`killSessionAfter`: then a script that has not stopped by then gets the whole session killed, with every other run in
it, and `Exited` says `StopTimedOut`. When several stops pass a time, the earliest wins. Killing stays the app's
decision:

```csharp
await run.RunToEndAsync(stop, killSessionAfter: TimeSpan.FromSeconds(2));
```

**The end of a session.** `DisposeAsync` goes in this order:

1. New runs throw `ObjectDisposedException`; new calls on `Remote` fail with `ConnectionLostException`.
2. Every run is stopped, and its end awaited: its code, then its globals.
3. Calls on `Remote` that are already running in the session object finish.
4. The session object is disposed. The app side still answers meanwhile.
5. The process ends.
6. Calls on the app side that are still running see their token cancelled, and are awaited.

Steps 2 to 5 have `CSharpScriptSessionOptions.DisposeTimeout`, 5 s by default; past it, the process is killed.
`KillAsync` ends the process at once, then does step 6, and cuts a `DisposeAsync` in progress short. Both return only
after the app side's calls have returned, so the app may drop its app side then; app code that ignores its token
keeps them waiting. `DisposeAsync` never throws for failures in the runner, which go to the logger, so `await using`
keeps the app's own exception. It is safe after a kill or a crash, and to call twice.

**When a session has ended,** `session.Exited` completes with the reason (`Disposed`, `Killed`, `StopTimedOut`,
`Crashed` or `DisposeTimedOut`), the exit code and the end of the process's standard error. It completes before the session's runs
fail because of it, so code that catches `ConnectionLostException` awaits it to learn why. A session cannot be used
again after a kill or a crash, and does not restart by itself: the app starts a new one.

**Isolation.** Each session is its own process, so a kill or a crash takes only its own scripts down. Group scripts
into sessions by what may fail together: scripts that share objects belong in one session, scripts that must survive
each other in separate ones. Sessions share no objects, but may share files and devices. A minimal session took 80 to
110 ms to answer its first call and about 17 MiB of memory, before scripts and drivers. To hide that wait, an app
starts its session ahead of time, for example when a page opens; `StartScriptAsync` then only loads the script. A
runner process isolates accidents, not hostile code.

**Fast data** stays in the runner when drivers read it there. What has to cross goes in batches: bridge methods that
take arrays, filled by helpers that collect single values. In a spike, `double[]` batches of 1,024 through a session
method reached about 12 million items per second, and a sender like the sample's, which collects the next batch
while one is on its way, about 7 million single values per second, with a lone value arriving in about 25 µs.
Streams (`IAsyncEnumerable`) were slower, and one stream in plus one out in a single call failed with StreamJsonRpc
2.26.

**Failures.**

| What happens | `run.Completion` |
|---|---|
| The script ends normally, or catches its cancellation and returns | completes |
| The script ends with `OperationCanceledException` through its stop token | cancelled |
| The script throws, also `OperationCanceledException` from any other token | `CSharpScriptException` |
| Disposing the globals throws after the script succeeded | `CSharpScriptException`; when the script failed too, its exception wins |
| The process ends while the run lasts | `ConnectionLostException` |

| Where | Failure | Result |
|---|---|---|
| `StartAsync` | the bridge has no build-time proxies | `InvalidOperationException`, in every build |
| `StartAsync` | the session type's `Create` throws | `CSharpScriptException`, and the process is ended |
| `StartAsync` | the process does not start or answer | `InvalidOperationException`, with its standard error |
| `run.Completion` | the globals' `Create` throws | `CSharpScriptException` |
| `Remote` | the session object throws | `CSharpScriptException` |
| `Remote` | the process has ended, or the session is being disposed | `ConnectionLostException` |

`CSharpScriptException` derives from StreamJsonRpc's `RemoteInvocationException`; its `ScriptPosition` is the innermost
frame in the script's own code, for jumping to it. The session's connection maps failures in the runner to it through
StreamJsonRpc's error hook, and maps a refused call to `ConnectionLostException` by throwing from that hook.

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
- show line numbers in exceptions;
- unload old scripts.

Roslyn's script assemblies make this possible in principle: they have a factory method that takes the globals.
Nobody has done it here yet.

Sessions (section 7, decisions #80 to #86) were tested in spikes: a NativeAOT host with StreamJsonRpc 2.26 and
generated proxies, and a JIT runner with precompiled classes standing in for scripts. Proven:

- a connection per run with plain `[JsonRpcContract]` interfaces, and calls in both directions while the other side
  waits;
- the end-of-session order of section 7: refusing calls in the runner's dispatch while cleanup still talks to the app,
  mapping the refusal to `ConnectionLostException`, finishing admitted calls, disposing the session object while the
  app answers, and then cancelling and awaiting app-side calls. That needs StreamJsonRpc's
  `CancelLocallyInvokedMethodsWhenConnectionIsClosed`, and awaiting its dispatch, not only its connection;
- a kill, also in the middle of disposing: pending calls fail with `ConnectionLostException` within about 3 ms;
- a run's values reaching the app before its `Completion`: 300 of 300 runs, every value checked;
- the globals' static `Create` called in the runner from the type's name: about 15 ns per run once cached;
- the typed host API published as NativeAOT without warnings, with a run's input copied into the runner;
- naming runner-side types in a NativeAOT app without compiling their code: about 700 bytes of metadata;
- the cost of a minimal session: 80 to 110 ms to the first answered call, about 17 MiB each;
- fast data, with the numbers in section 7.

Still open:

- run-owned registrations that stop taking calls and await the running ones, as the sample now does: a spike found
  that only removing a registration let a running callback use disposed globals;
- loading runner-side dependencies: native libraries, packages with code per platform under `runtimes/`, and
  dependencies built for one operating system, in a published NativeAOT app;
- unloading a run's script assembly once nothing in the session references its code any more, at the latest when the
  session ends: a calculation the session keeps holds on to it;
- concurrent `DisposeAsync` and `KillAsync`, and the order of `Exited` and failing runs, in the real implementation.

## 10. Open questions

Question numbers continue the decision log in [design.md](design.md). Decisions taken in the API discussion are
recorded there too (#61, #64–#86).

- **Q55 – Logging.**
  - **Draft:** takes `Microsoft.Extensions.Logging.ILogger`.
  - **Apps that log with Serilog:** they pass `new SerilogLoggerFactory(logger).CreateLogger(...)` from Serilog.Extensions.Logging.
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
  - **Draft:** script API assemblies reference StreamJsonRpc and use its attributes, its shape constraints and its exceptions (sections 1 and 7). Only the two interfaces of a session's bridge carry them; the session object and the globals are plain .NET.
  - **Alternative:** hiding it would take our own attributes, a source generator and our own proxies for the bridge.
  - **Recommended:** accept it for v1.
- **Q59 – Running scripts without the editor's UI.**
  - **Today:** the runner package depends on the C# package, and through it on the editor package and Avalonia. A console app can reference those, so it would work.
  - **What matters:** nothing in compiling and running may wait for Avalonia's UI thread. The draft makes sure of that.
  - **Recommended:** keep one dependency chain until an app without Avalonia needs scripts. Then the compiler moves into a package of its own.
- **Q60 – Storing compiled scripts.** A `CSharpScriptAssembly` that can be saved and loaded would let later runs skip Roslyn entirely, for scripts that rarely change. **Recommended:** after v1.
- **Q62 – No `Text` property on the editor.** Text goes through the document only, so no binding copies the whole text on every keystroke. **Recommended:** as drafted.
