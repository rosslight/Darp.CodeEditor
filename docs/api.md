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

// C# scripts: one server for the app; an environment per kind of script says what its scripts see. A kind of script
// is its globals type, an ordinary class of the script API.
var scripts = new CSharpScriptServer();
var environment = new CSharpScriptEnvironment<ScriptGlobals> { Imports = ["MyApp.ScriptApi"] };
CSharpScriptDocument<ScriptGlobals> document = scripts.CreateDocument(environment, text);  // a CodeDocument

// The app owns documents; editors show them.
editor.Document = document;

// Run: a session is a runner process with a bridge to the app. Run the version the editor shows in it.
await using CSharpScriptSession<ISessionSide, Session> session =
    await CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(appSide, ct);
await session.Remote.InitializeAsync("bench 1", ct);    // the script API's own setup, before scripts run
CSharpScriptCompilation<ScriptGlobals> compilation = await document.CompileAsync();
if (compilation.Succeeded)
{
    // Compiles only if the session object creates ScriptGlobals from a RunInput.
    CSharpScriptRun run = await session.StartScriptAsync(compilation.Script, new RunInput("run 1", "bench 1"), ct);
    await run.Completion;                                 // the run has ended; its calculation stays in the session
    double result = await session.Remote.CalculateAsync(21, ct);
}
```

| Package | Public types |
|---|---|
| `Darp.CodeEditor` | `CodeEditor`, `CodeDocument`, `CodeLanguage`, `LanguageServer`, `LanguageServerOptions`, `LanguageServerSettingsRequest`, `LanguageServerState`, `CodeEditorColorTheme`, `CodeEditorViewState`, `CodeEditorStyles`, `TextSnapshot`, `TextRange`, `TextPosition`, `TextSelection`, `Indentation`, `Diagnostic`, `DiagnosticSeverity` |
| `Darp.CodeEditor.CSharpScript` | `CSharpScriptServer`, `CSharpScriptServerOptions`, `CSharpScriptEnvironment<TGlobals>`, `CSharpScriptDocument<TGlobals>`, `CSharpScriptCompilation<TGlobals>`, `CSharpScriptAssembly<TGlobals>`, `CSharpCodeLanguage` (adds `CodeLanguage.CSharp`) |
| `Darp.CodeEditor.CSharpScript.Runner` | `CSharpScriptSession` (starts sessions, and scripts in them), `CSharpScriptSession<TSessionSide, TSessionObject>`, `CSharpScriptSessionOptions`, `CSharpScriptRun`, `CSharpScriptException`, `CSharpScriptSessionExit`, `CSharpScriptSessionExitReason`, and the contracts that runner-side script APIs implement: `IScriptSessionObject<TSelf, TAppSide>`, `IScriptGlobalsFactory<TGlobals, TInput>`, `IScriptGlobalsFactory<TGlobals>` |

34 public types in all. Three more things are public without being types of ours:

- **The project file.**
  - `CSharpScriptReference="true"` on a package or project reference: scripts may use its assemblies. Publishing copies their DLLs next to the app, NativeAOT apps included, with their XML docs, which hover and completion show.
  - `PublishReadyToRun=true`: publishing needs it to compile the C# helpers with ReadyToRun (prototype #48).
- **StreamJsonRpc and PolyType** (#89), for the script API:
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
  compiles itself. `CodeDocument.Create` creates documents without a server. Apps don't derive from `CodeDocument`. Headless compiling
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
| `SaveViewState()`, `RestoreViewState(state)` | the caret, the selection and the scroll position, for an app that switches one editor between documents |

**Built in, with no settings:** current-line highlight, bracket matching, auto-closing brackets and quotes,
auto-indent, whitespace shown inside the selection, scrolling past the last line, a context menu with Cut, Copy, Paste
and Select All, and the look of errors, hover and completion below.

**Caret and scroll position** belong to the editor, as in Monaco, and last as long as it does, like a list's scroll
position. Setting `Document` shows the other document from its start, and a tab control may create a new editor each
time a tab shows. An app that wants them kept does one of two things:

- **It keeps its editors,** such as one per open tab, in an `ItemsControl` that shows only the selected one.
- **It saves and restores them,** as Monaco's `saveViewState` and `restoreViewState` do: `editor.SaveViewState()`
  before the switch, and `editor.RestoreViewState(state)` once the editor shows the document again. A state follows
  the edits made since it was saved. Use case 05 shows a helper for one editor that shows every tab.

A document holds no view state, so several editors of one document never fight over it.

**Editing in code:** `document.Replace(range, text)` inserts or replaces in one step the user can undo, such as a
template at the caret: `document.Replace(editor.Selection.Range, snippet)`. Setting `Text` replaces everything.

**Keys:** the editor handles only its own: editing, navigation, search (Ctrl+F, Ctrl+H, F3), completion (Ctrl+Space),
comments (Ctrl+/) and indentation (Tab, Shift+Tab). On macOS, Cmd takes Ctrl's place, except for completion, and
Cmd+Alt+F opens the replace bar, as AvaloniaEdit decides by the operating system. While completion's list shows, Up,
Down, Page Up, Page Down, Enter, Tab and Escape are its; while signature help shows several signatures, Up and Down
are its. Other keys, such as F5 or Ctrl+S, reach the app's key bindings.

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
    <ce:CodeEditorColorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-dark.json" />
  </ResourceDictionary>
  <ResourceDictionary x:Key="Light">
    <ce:CodeEditorColorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-light.json" />
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
- **Completion:** a list of up to 12 items at once below the word, or above it when only that side has room, in the
  app's flyout, with rows in the app's look of list items as dense as the editor's lines and labels in the editor's
  font; the selected item's details in a flyout of their own, beside the list where the window has room for them
  right of the word, narrower if need be, and below the list otherwise, so that the list stays at the word.
- **Completion icons:** VS Code's codicons, drawn as vector shapes in the theme's `symbolIcon.*` colors, as in VS
  Code, and in VS Code's colors where the theme sets none. Their designs are licensed CC BY 4.0: the package lists
  them in its third-party notices, and apps that show notices of their own include that entry.
- **Signature help:** in the app's tooltip above the caret's line, so that completion's list shows below it; the
  signature as code, in the editor's font, colors and background.

**Before v1, additive:** marks beside the scroll bar for errors, warnings and search matches, as Monaco's overview
ruler shows them. Scripts of 5,000 lines need them to find errors outside the view. They add to the API without
changing it, so they wait until the core works.

**Later, when asked for:** whitespace display modes, rulers, folding, a minimap, placeholder text for empty
documents, indentation detected from the text, markers that the app adds, and a view state that can be stored across
app starts.

## 3. Who owns what

| Object | Created by | Shared by | Ends with |
|---|---|---|---|
| `LanguageServer`, `CSharpScriptServer` | the app, once (a singleton) | all documents of its languages | `DisposeAsync`, at app exit |
| `CodeLanguage` | built in, or the app once per language | documents | nothing to dispose |
| `CodeDocument` | the app, usually a view model, one per script or file: with `CodeDocument.Create`, or a server's `CreateDocument` | the editors that show it | `Dispose`, when the user closes it for good |
| `CodeEditor` | the view | | the visual tree; out of it, the editor drops its subscriptions, requests and rendering resources |
| `CodeEditorViewState` | `SaveViewState` | the app, until it restores it | nothing to dispose |
| `CSharpScriptSession` | the app, once per isolation boundary | the scripts it runs | `DisposeAsync`, or `KillAsync` |
| The app side of a session's bridge | the app | the session's scripts | the app, once `DisposeAsync` or `KillAsync` has returned |
| The session object | the session, in its process | the app, through `Remote`, and the session's scripts | the session's `DisposeAsync`; lost on a kill or crash |
| A run's globals | the session object's factory, per run | that run's script | the end of the run: the session disposes them before `Completion` |
| A run's input | the app | that run's globals | the run; it is copied into the runner |
| `CSharpScriptRun` | `StartScriptAsync` | | nothing to dispose |
| `CSharpScriptAssembly<TGlobals>` | `CompileAsync` | runs in sessions whose object creates its globals | nothing to dispose; it outlives the server |

## 4. Threads

- **Editors:** on the app's main UI thread only, because AvaloniaEdit's editor checks `Dispatcher.UIThread`. An editor on another thread fails at once. It posts its own work to its `Dispatcher`, which Avalonia 12 gives every control.
- **Documents:** create, read, compile and dispose them on any thread. Change them only on the UI thread, where the user types and editors draw: setting `Text`, `FilePath` or `Indentation`, `Undo`, `Redo` and `MarkUnmodified` throw elsewhere. Their events come on the UI thread, also for documents created elsewhere.
  - Documents and editors work on the app's main UI thread only, because AvaloniaEdit requires it. A document belongs to that thread wherever it was created: its events come there, and its changes are checked against it.
  - Servers raise `State` there too, and send the documents' diagnostics there.
  - `CurrentSnapshot` is the text with its version, immutable. Reading `Text` and the version separately could pair one version's text with the next one's number.
  - The document makes a snapshot after every change, sharing the unchanged parts of the text. In AvaloniaEdit, an edit plus its snapshot takes about 1 µs on 5,000 lines (section 9).
  - `document.CompileAsync()` compiles the snapshot current at the call, and the compilation keeps it as `Snapshot`.
- **Immutable, any thread:** `CodeLanguage`, `CodeEditorColorTheme` and `TextSnapshot`.
- **Any thread:** `LanguageServer`, `CSharpScriptServer`, `CSharpScriptSession` and `CSharpScriptRun`.
  - Both servers raise `PropertyChanged` for `State` on the UI thread, for a status indicator to bind. Nothing waits for it, so servers and compiling also work before the UI runs.
  - Both servers dispose synchronously too: `Dispose` kills the process at once, for app exits where only synchronous disposal runs. Sessions are only async-disposable, because the order in which they end matters.
  - A session's `Output`, and calls on the app side of its bridge, come on any thread. Calls on the session object come on any thread in the runner, from the app and from overlapping scripts.
- **Before Avalonia is set up,** the library leaves Avalonia's dispatcher alone: in Avalonia 12, touching `Dispatcher.UIThread` from another thread ties it to that thread, and the app's own setup then fails. Until then, documents and servers raise their events where the change happens, and documents check no thread. Languages, themes, documents, servers, compiling and sessions all work before the app's `AppBuilder` runs.
- **No blocking:** nothing in the API makes the UI thread wait for a server or a script. Creating and disposing documents only queues work for the server.

## 5. Positions

Offsets count UTF-16 code units from 0. Lines end at `\r\n`, `\n` or `\r`, alike in the editor, the LSP client and
the C# server. `TextPosition` counts lines and columns from 1, as editors show them; the LSP client converts to the
protocol's 0-based positions. `TextSnapshot.GetPosition` and `GetOffset` convert, in the text of that snapshot, and
`GetText(range)` reads a part of it. Ranges are not checked when created; members that take them clamp them to the
text.

Positions come with the text they refer to. A diagnostic carries its `Snapshot`: the text the server analyzed, or the
compiled text. A failure's `ScriptPosition` comes with its `ScriptSnapshot`, also for a callback that an earlier run
left in the session. A string compiled without a document gets a snapshot too. `document.MapToCurrent(snapshot, range)`
follows the edits made since, so clicking an error after typing above it still lands on it.

## 6. Language servers

"Any LSP server" means any server that works with this subset of the protocol, which the client implements:

- **Lifecycle:** `initialize` (with the root folder and initialization options), shutdown and exit, and `$/cancelRequest`.
- **Documents:** incremental `didOpen`, `didChange` and `didClose`.
- **Features:** completion with resolve, hover, signature help, semantic tokens (range, or full), and pushed diagnostics (`publishDiagnostics`).
- **Settings:** `workspace/didChangeConfiguration` with all settings, when the server starts and on `NotifySettingsChanged`.
- **Server requests the client answers:** `workspace/configuration` (through `Settings`) and `client/registerCapability`. Registrations are acknowledged, and only the features above take effect.
- **Not in v1:** pulled diagnostics, workspace edits, file watching, code actions, formatting, rename. The client does not advertise them.

A server that needs pulled diagnostics would add them to v1. The client advertises only what it implements, so
servers fall back where they can: the lifecycle, documents, settings, pushed diagnostics, hover (Markdown and plain
text), semantic tokens (range and full, in the relative format, with the protocol's standard token types and modifiers),
completion and signature help. Completion items may have documentation in Markdown or plain text, commit characters,
a deprecated flag or tag, preselection, an edit that inserts or replaces, and any of the protocol's 25 kinds; their
detail, documentation and additional edits may come when they are resolved, and the request says how completion was
asked for. Items are inserted as their text is: the client advertises no snippets. Signatures may have documentation
in Markdown or plain text, parameters labelled by offsets, and an active parameter of their own, and the request says
how it was asked for.

How the client talks to a server, where the protocol leaves a choice:

- **Documents** open with their language's `Id` and, as their URI, their file's `file:` URI, or `untitled:Untitled-1`,
  `-2` and so on without a file. Setting `FilePath` closes the old URI and opens the new one. Versions are the
  snapshots' versions. Edits go out as incremental changes, in order; a server that asks for the full text with every
  change gets it. While a server does not read, a document's changes wait as one message; past 1,000 changes or a
  million characters, its full text replaces them.
- **Diagnostics** with a version are about the text sent with that version; the client keeps the last 64 versions it
  sent, and drops diagnostics of an older one as outdated. Diagnostics without a version are about the text last
  written to the server when they arrive. Diagnostics find their document by its file URI in whatever form the
  server writes it: decoded, with the drive letter and the host in any case. Servers built on VS Code's URI library
  write `file:///C:/a.txt` as `file:///c%3A/a.txt`; the same rule finds two documents with one file path. A
  diagnostic without a severity is an error, as in VS Code. Diagnostics belong to the process that
  sent them: when it ends, by a crash, a hang or disposal, the documents' diagnostics are cleared.
- **Server requests** run on the thread pool, and reading never waits for them, so responses and cancellations always
  get through: `workspace/configuration` calls `Settings` once per item, with the scope's path for a `file:` URI.
  Registrations are acknowledged; other requests get `MethodNotFound`. Notifications other than `publishDiagnostics`,
  such as `window/logMessage`, are ignored.
- **Settings** go both ways. After `initialized`, on every start and restart, and on `NotifySettingsChanged`, the
  client sends `workspace/didChangeConfiguration` with all settings: the answer of `Settings` for a null section, or
  `{}` without one, never `null`. Some servers read their settings only there, such as VS Code's JSON server with its
  `json.schemas`; others ask with `workspace/configuration`, which `Settings` answers per section. `Settings` runs on
  the thread pool for both. At the start, the server runs once it answered `initialize`, and `StartAsync` completes
  then; its documents open once the settings went out, so a callback that blocks holds back the documents' opening
  and the settings, and later only the settings. Disposal and restarts never wait for it, and its late answer is
  dropped. A callback that throws drops its push. One computation runs at a time: changes meanwhile make one more.
- **Not reading:** when more than 10,000 messages wait to be written to a server, counting its requests that still
  run, the server does not read what it is sent, and its process ends as a crash. A document closed before its
  `didOpen` was written sends nothing, and a closed document keeps no text while its messages wait.
- **Ending with the app:** disposing the server ends its process. When the app is killed, Windows ends the process
  with it, through a Job Object. On Linux and macOS the process ends once the server notices that its standard input
  closed or that the app's process, whose id it gets in `initialize`, is gone, as the protocol asks servers to do.
- **Capabilities** of the server that have another JSON type than the protocol says count as not given.
- **Editors ask, documents don't.** Only an editor that shows a document asks for its hover, semantic tokens,
  completion and signature help; a document that no editor shows costs the server nothing but its text, so the
  documents that editors show go first. Editors never wait for an answer, and keep no queue of their own: each editor
  asks at once, and an editor that loses the keyboard focus cancels what it asked for completion and signature help.
  Their requests take the path of every request, with its hang limit, and a server's error or an answer of another
  shape shows nothing. An answer counts only for the text that was current when the editor asked: the document's
  changes reach the server before the request, and an answer that comes after an edit is dropped, except
  completion's, which the typing since filters. Editors ask only once the document is open in the server's process,
  after the start's settings went out: until then they get nothing, and when it opens there, not when the server
  runs, they learn that the server serves it, so a hover, completion and signature help end, and semantic tokens are
  asked for again.
- **Hover** is asked when the pointer rests on a character for 400 ms, as long as Avalonia's tooltips wait (it is
  AvaloniaEdit's hover time), at that character's position, or at a line's end within a space's width after it, where
  an empty diagnostic's squiggle is. A `MarkedString` with a language becomes a Markdown code
  block. The popup stays while the pointer is over the word, the range that the answer names, or the popup itself.
  The word reaches at most 1,000 characters to either side of the pointer, as in VS Code, so a line of millions of
  word characters costs the pointer no more; the popup still stays while the pointer moves on along such a word,
  which the view shows only up to its 3,000th character. How far the word goes on its line is found once, when the
  hover starts, so moving the pointer reads no text.
  The hover ends when the pointer rests on another word, on typing, scrolling and Escape, when the editor loses the
  keyboard focus, when a server starts or stops serving the document, and with new colors; also while it still
  waits for its answer. Ending it cancels the request still under way with `$/cancelRequest`, stops reading the
  answer between lines and paragraphs and highlighting its code between lines, and drops what they would show. The
  popup is placed again when the window or the text it is next to moves, as when the window narrows.
- **The hover's look:** text in the app's tooltip; code blocks in the editor's font, colors and background,
  highlighted with the document's grammar; inline code as a small span in the editor's font family, foreground and
  background, at the size of the text around it, so that it fits its line.
- **The hover's size:** a hover shows at most 2,000 characters on 50 lines, in 250 runs of one style or color, the
  diagnostics' messages first, the most severe first and at most 50 of them; "…" marks where it was cut. The UI thread builds and lays out every run one by one, at
  about 10 µs a run and 1 µs a character, so the largest hover takes it about 2 ms, with the frame about 7 ms
  (BenchmarkDotNet, i7-13700), and a tooltip that scrolls gains nothing from more. Every piece of a hover's text, a
  diagnostic's message and code as much as the server's answer, is cut in one place before anything else reads it,
  so the work, in the background too, is bounded by these limits and not by what the server sends. Lines are counted
  at `\r\n`, `\n` and `\r`, and also where the layout of text breaks them, such as the Unicode line separator; a cut
  never splits a surrogate pair. Finding the diagnostics under the pointer puts in order only those it keeps: with the
  hover's text, 0.75 ms for 10,000 on one character in the worst order, where each one found beats those before
  (BenchmarkDotNet, i7-13700).
- **The shown part of a line.** The editor's views work only on the part of each line that the view shows, its first
  3,000 characters: AvaloniaEdit collapses the rest of a longer line into "...", which shows none of it (design.md
  #106). Squiggles are drawn only in the shown part, the hover finds no text over "..." and its word stops where the
  tail starts, semantic tokens that start in a tail are dropped as they are read, and those that an edit moves past
  the shown part, as when lines join, go. So their work per line is bounded by 3,000 characters, whatever the line's
  length or what a server puts in its tail; an edit that removes a hidden tail reads none of it either.
- **Diagnostics shown.** Editors show at most 10,000 of a document's diagnostics, the most severe first, then in the
  server's order, which is also their order in a hover (design.md #109); `Diagnostics` keeps them all. As a batch
  arrives, those shown are chosen and put in the latest text on the server's thread: 0.9 ms for 10,000, and 4.0 ms
  when they are about text 100 edits old. The UI thread then follows only the edits made since, usually none: taking
  the batch in costs it 13 µs, and 79 µs after 100 edits. From then on they move with the text as the user types,
  once for all of the document's editors, also while none shows it.
- **Squiggles** are drawn only for what is on screen: in the rows of each line between the view's top and bottom
  edges, in the shown part of the line, whole rows, as right-to-left text puts their characters in another order across
  the screen than along the text. A range over a change of direction is drawn on each side of it, not over the text of
  the other direction between, and each wave is cut to the view's width, so a line scrolled sideways costs what the
  view shows of it. Each diagnostic is read once a frame, also one over many lines, and those of one
  severity that overlap are drawn as one, and where they go is measured once per row of text, not once for each
  of them, as a text line's own hit tests would walk the row from its start for each. With all 10,000 apart on the
  screen of a 4K window, drawing them takes the UI thread 2.9 ms, where measuring each on its own took 15 to 19 ms;
  with each of them over all of the text, 3.2 ms, where reading them for each line in view took 30 ms. One error over
  100 lines of 3,000 characters, unwrapped in an 800 by 2,160 window, takes 3.7 ms, where waves the length of each
  row took 41 ms.
  The headless frame, which also rasterizes them, takes 15 ms more than without them; an app rasterizes on its render
  thread (BenchmarkDotNet, i7-13700).
- **Semantic tokens** are asked for the lines in view with `semanticTokens/range` when the server offers it, otherwise
  for the whole text with `semanticTokens/full`; never as deltas. An editor asks once it shows its first lines, 300 ms
  after the last edit, 100 ms after scrolling stops on lines without current tokens (VS Code's shortest pauses for a
  document and for the view), and when a server starts or stops serving the document. An edit, or a newer request,
  cancels the request under way. Tokens stay with their unchanged text until new ones arrive: an edit keeps the tokens
  before it, moves those after it with their text, also onto another line, and drops those it touches. After an edit
  every line's tokens count as outdated and are asked for again when in view, as an edit can change the meaning of
  text elsewhere. An answer that fails leaves the lines in view as they are until the next edit, or until scrolling
  shows others. Tokens of a type that the server's legend does not name, outside the lines asked for, or empty are
  ignored, and a process's tokens go when it ends, as its diagnostics do. A line keeps at most one token per character
  of its shown part: tokens that overlap the one kept before them, or start in a long line's hidden tail, are dropped
  as they are read. An answer is read, and its lines built, off the UI thread; showing it compares and draws only the
  lines in view, also when it is for the whole text. A line draws at most 250 of its tokens in their colors, and the
  rest of it keeps its TextMate colors (design.md #109): each colored token is a run of text that the layout costs
  about 6 µs, as each TextMate token is. The 32 lines of an 800 by 600 window with a token on each of their 3,000
  characters take 57 ms to take and draw, where all of the tokens took 984 ms; with 300 tokens a line, 45 ms; TextMate
  colors alone take 55 ms for 300 tokens a line. Of that, the UI thread's own work, taking the tokens and laying out
  the lines with their colors, takes 35 ms, and 28 ms with 300 tokens a line, about 4.4 µs a colored token: the limit
  keeps it bounded, not within a frame (BenchmarkDotNet, i7-13700).
- **Completion** behaves as VS Code's by default. It is asked for at the caret on Ctrl+Space, also on macOS, and when
  typing starts a word outside a comment, as invoked; and on the server's trigger characters, as a trigger character.
  VS Code also asks in strings for JSON and YAML, and the editor does not tell strings apart. Typing on in a word does
  not ask again, also after Escape, until the next word or Ctrl+Space. The word before the caret goes by VS Code's word
  characters and reaches back at most 1,000 of them, as a hovered word does, and items without an edit replace it up
  to the caret with their `insertText`, or their label. An item whose edit is not on the caret's line, spans lines, or
  does not contain the caret, is left out, as VS Code leaves it out, and one without a kind, or with a kind the
  protocol does not name, shows as a property.
  - **Filtering and ranking** are on the client, as the user types, with VS Code's fuzzy matching (a port of its
    `fuzzyScore`, which the C# server will share): the typed text from where an item's edit starts is matched against
    its `filterText`, or its label, in order and ignoring case, and its first character must match at a word start, a
    capital or after a separator. Before anything is typed, items are in the order of their `sortText`, or their
    label, ignoring case, then of their label and kind; equal scores keep that order. The characters that matched show
    in bold in the label, as in VS Code: the filter text's, when the whole filter text is the label, ignoring case,
    and otherwise the label's own, by VS Code's more lenient `anyScore`. The first item is selected, or the first that
    the server preselects among those that match best. There is no typo tolerance. Matching runs off the UI thread,
    whatever the list's length and the typed text's: each change cancels the filter under way, and the list shows the
    newest result, the previous one meanwhile. The UI thread reads no text for it, and matching reads the first 128
    characters of an item's typed text, as VS Code's does: an edit that starts further before the caret, also when
    the caret is in a line's collapsed tail, where only code puts it, matches by those, and costs no more.
  - **Asking again:** a list marked `isIncomplete` is asked for again on each character that the user types on in a
    word, for incomplete completions, and shows meanwhile. Moving back before where completion was asked asks again
    while a word is left, as invoked, as VS Code does, and so does an edit before it, as by another editor of the
    document: the list's positions no longer fit the text. A newer request cancels the one under way with
    `$/cancelRequest`, and so does ending completion; the answer of a request that is no longer the latest is
    dropped. An answer that comes after typing is filtered with what was typed since.
  - **Committing:** Enter, Tab, a click, or one of the item's commit characters, or the server's `allCommitCharacters`
    for an item that names none, which then goes in. While the filter of the current text still runs, they commit the
    item that shows if it matches the current text, which is checked for that item alone, on the first 128 characters
    of its typed text; otherwise Enter and Tab commit what that filter selects once it has run, unless the text, by
    any editor of the document or by code, or the caret changes before, and a commit character goes in as text, as
    when nothing matches: deciding later would hold back or reorder what the user types. A read-only editor neither
    completes nor commits. The item's `textEdit` applies: a text edit, or the insert range of an insert and replace
    edit, as VS Code's default insert mode; its range grows by what was typed since it was asked, as in VS Code. Its
    `additionalTextEdits`, such as an import, apply with it, as one step that the user undoes; ones that overlap
    another edit are left out.
  - **Resolving:** the selected item is resolved when it is selected, and the resolve of an item that is no longer
    selected is cancelled. Its detail, as code highlighted with the document's grammar, and its documentation show
    beside the list, as much of both as a hover shows: they share one budget of the hover's size, cut before anything
    reads them. A row shows at most 256 characters of a label, with "…". Additional edits that the resolve brings only after the item was committed join the commit's
    undo step while it is the last one; once the user moved on, they are dropped. Items resolve only in the process
    that sent them.
  - **Ending:** Escape, the caret leaving the line or moving back past the word, a selection, the editor becoming
    read-only, the list's line scrolling out of view, the editor losing the keyboard focus, a server that starts or stops serving the document,
    new colors, nothing left that matches a complete list, and the editor no longer showing the document.
- **Signature help** is asked for on the server's trigger characters, and again at once on its retrigger characters
  while it is open. While it is open, each edit and caret move restarts a pause of 120 ms, VS Code's, after which it
  asks again for a change of the content; the pause runs on the options' `TimeProvider`. Asking again says that it is
  a retrigger and sends the signature help that showed, with the signature the user chose. A newer request, and each
  edit and caret move, cancels the one under way, and an answer about older text is dropped. An answer without
  signatures, as outside a call, closes it. It shows the active signature, or the first, with the active parameter in
  bold: the signature's own, or the answer's, or the first; one outside the parameters is none, as in VS Code. A
  parameter labelled by a string is found as a whole word in the signature. The parameter's documentation shows
  below, then the signature's, all of it as much as a hover shows, the signature first; with several signatures, which one of how many. Up and Down show the others while
  completion's list is closed. It ends on Escape, after completion's list, with no signature, when the editor becomes
  read-only, when the editor loses the keyboard focus, when a server starts or stops serving the document, with new colors, and when its line scrolls
  out of view.
- **Token colors** come from the color theme as in VS Code: its `semanticTokenColors`, matched by type, modifiers and
  language with VS Code's scores; otherwise its TextMate color of the scopes that VS Code's standard mapping gives the
  type and modifiers, such as `entity.name.type` for a class or `variable.other.constant` for a read-only variable.
  Tokens without either keep their TextMate color. Only the color is drawn, not bold, italic or underline: the TextMate
  colors draw no font styles either. Only the first 1,000 types of a legend are colored, and modifiers that no selector
  names count as none, so the UI thread finds a color once per type and named modifiers, whatever a server sends.
- **Messages** from a server that are not valid JSON get an error response. A message over 64 MiB, or broken framing,
  ends the process as a crash: skipping it would lose a request or a response without notice.

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
interop. The runner side references the runner package for `IScriptSessionObject` and `IScriptGlobalsFactory`; that
brings the editor and Avalonia into its build, but nothing of them loads in the runner. The C# package knows nothing
of sessions: the editor and the compiler need only the globals type. The app names runner-side types (the session
type, the globals types) without compiling any of their code: in a test, naming them added about 700 bytes of
metadata to a NativeAOT app.

**Deploying the runner side.** Publishing deploys a runner-side project marked `CSharpScriptReference` as its own
untrimmed publish for the app's platform, next to the app: its DLLs, its dependencies with their native libraries and
per-platform code under `runtimes/`, and its dependency manifest (`.deps.json`). The runner loads it through that
manifest, with .NET's `AssemblyDependencyResolver`, and loads the bridge and the library's contracts only once, so
their types are the same on both sides. A NativeAOT app's own output can't serve for this: it holds no managed DLLs.
Two limits come with it:

- **Base .NET only.** The runner starts on the base .NET runtime. A runner-side project that needs another shared
  framework, such as ASP.NET Core, fails the build with a clear error.
- **Compatible frameworks.** The app references the runner-side project, so their target frameworks must fit: a
  `net10.0` app can't reference a `net10.0-windows` project (NU1201). The runner itself loads a `net10.0-windows`
  runner side on Windows, so a Windows-only app targets `net10.0-windows` itself.

**Writing a script API.** The checklist, which the use cases' sample follows:

1. A bridge project, analysed for NativeAOT, referencing StreamJsonRpc: the app side and the session side as
   `partial` interfaces with `[JsonRpcContract]` and `[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]`,
   their data as `[GenerateShape]` records, and `[assembly: ExportRpcContractProxies]`. Without that last line, a JIT
   app works and a NativeAOT app fails on its first call; `StartAsync` therefore checks for it in every build.
2. A runner-side project, not analysed for NativeAOT, referencing the bridge and the runner package: the session type
   with `IScriptSessionObject`, a private constructor and a static `Create`, and one `IScriptGlobalsFactory` per kind
   of script it runs; the globals types as ordinary classes with internal constructors. Implement `Create`, the
   factories and the disposal explicitly: the globals' public members are what scripts see, so a public
   `DisposeAsync` would be a script's to call. Callbacks that the session object keeps must not use a run's globals.
   Keep the factories cheap: acquire devices in the session's setup or later in the run, so that a factory that
   throws leaves nothing open. Report expected failures of the app side, such as a declined prompt, as result
   records: a script sees an app-side exception only as `RemoteInvocationException` with its message. A script API
   without reflection-heavy drivers may be a single project.
3. In the app, both projects referenced with `CSharpScriptReference="true"`, an environment per kind of script,
   `new CSharpScriptEnvironment<TGlobals> { … }`, and a one-line wrapper around `StartAsync`, like the use cases'
   `Scripting.StartSessionAsync`: C# can't infer its three type arguments.

**The session object.** `StartAsync` starts the process and creates one instance of the session type there, through
its static `Create` from `IScriptSessionObject<TSelf, TAppSide>`, which gets the app side. The third type argument names
that type, so the compiler checks that it implements the session side and takes this app side. The session object holds what the session's scripts share, such as
connections and state, and owns what must outlive a run. Setup, such as connecting a device, is the script API's own
methods on the session side, which the app calls before it starts scripts.

**Runs.** `StartScriptAsync` returns once the runner has accepted the run, even if its token was cancelled meanwhile,
so no run is left without a handle. Creating the globals is part of the run: a factory that hangs is stopped like the
script, and one that throws ends the run's `Completion`. A run:

- **brings its own kind of script,** so scripts of one session can see different globals. A kind of script is its
  globals type: an ordinary class, which the environment, the document, the compilation and the compiled script carry
  as their one type argument. The session object decides which kinds run in its session: it implements
  `IScriptGlobalsFactory<TGlobals, TInput>` once per kind, or `IScriptGlobalsFactory<TGlobals>` for runs without
  input, and creates each run's globals from the input and the stop token. It passes the globals only what they use,
  such as itself, or one channel. `StartScriptAsync` is an extension method that requires the factory, so the
  compiler rejects a script started in a session that can't create its globals, or with the wrong input, and infers
  every type argument:

  ```csharp
  // Runner side: the session object says what runs in it.
  public sealed class Session : ISessionSide, IScriptSessionObject<Session, IAppSide>,
      IScriptGlobalsFactory<ScriptGlobals, RunInput>, IScriptGlobalsFactory<ReaderGlobals>
  {
      ScriptGlobals IScriptGlobalsFactory<ScriptGlobals, RunInput>.CreateGlobals(RunInput input, CancellationToken stop) =>
          new(this, input, stop);
      ReaderGlobals IScriptGlobalsFactory<ReaderGlobals>.CreateGlobals(CancellationToken stop) =>
          new(App, Messages.Reader, stop);
      …
  }

  // App: compiles only if Session creates ScriptGlobals from a RunInput.
  CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("run 1", "bench 1"), ct);
  ```
- **gets its own input,** such as its identity: data of a `[GenerateShape]` type that the app passes to
  `StartScriptAsync` and the session object's factory receives. Overlapping runs can mark their output with it. Runs
  without input use the overload without an input.
- **may overlap** with other runs of the session.
- **lasts as long as its code.** When the code has finished, for any reason, the session cancels the run's stop token,
  disposes its globals if they are disposable, while the bridge is still up, and then completes `Completion`. Tasks
  that the code started and did not await, such as a timer, are not part of the run: they keep running in the
  session.
- **ends what it owns when its globals are disposed,** such as sending its queued values. A kill runs no cleanup and
  delivers nothing still queued.

**Callbacks and the end of a run.** A run ends when its script's code has returned: the session cancels the run's
stop token and disposes its globals. Script code that the script handed to something longer-lived, such as a callback
set on the session object, can still be called afterwards; the library does not track it. A callback may outlive its
run, so it may only use what outlives its run: the session object and what it owns, not the run's globals, its input,
or what they own. Passing the run's stop token along is a safety net: once the run has ended it is cancelled, so a
callback that breaks the rule fails loudly instead of working on a closed device. Callbacks that should end with their
run are removed by the globals when they are disposed; a call already running may still finish shortly after. A
script API whose callbacks use what the run owns, such as its devices, gives scripts an awaited scope instead: when
its body ends, the scope refuses new calls, stops what produces them, and waits for the calls already running, all
before the script's code returns. The run then ends only after them, and the library needs to know nothing of it. In
v1, script code does not cross the bridge to the app: the bridge's methods copy data only.

**Readiness and threads.** Calls from the app and from overlapping scripts reach the session object on any thread,
and the script API makes it safe for that. The library does not serialise them: a script may wait for the app while
the app calls back. When the app calls before a script has registered what the call needs, the session object
throws. Where the app needs to wait for a script instead, the script API says so, for example with a notification
once the script has registered.

**Stopping.** `run.StopAsync()` cancels the run's stop token and waits for its end; it does not throw for how the run
ended, so the app observes `Completion` for that. `run.RunToEndAsync(stopToken)` waits for the end, stops the run when
its token is cancelled, and throws what `Completion` throws, also after stopping: `OperationCanceledException` when
the script stopped, `ConnectionLostException` when the session was killed, `CSharpScriptException` when the script
failed while stopping. So a page that catches the cancellation for its Stop button still sees a kill, and drops the
dead session. A run counts as stopped when its code ends with an `OperationCanceledException` after its stop token was
cancelled, also one from a token that the script linked to it, such as a timeout. To only stop waiting, without
stopping the run, use `run.Completion.WaitAsync(token)`. Both wait as long as the script takes, unless the app passes
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
keeps them waiting. `DisposeAsync` never throws for failures in the runner, so `await using` keeps the app's own
exception: runs report theirs in `Completion`, and the session object's own is `Exited`'s `DisposeException`. It is
safe after a kill or a crash, and to call twice. A session keeps every script it ran loaded until it ends, so that
callbacks a run left in the session object keep working. A session that runs edited scripts again and again grows
with each, so an app that keeps one open through a long editing session replaces it at an idle moment.

**When a session has ended,** `session.Exited` completes with the reason (`Disposed`, `Killed`, `StopTimedOut`,
`Crashed` or `DisposeTimedOut`), the exit code and the end of the process's standard error. It completes once, with
one reason: when several causes meet, such as a kill during disposal, the first cause the session acted on wins, and a
process that ended by itself before any of them is `Crashed`, also while it was being disposed. It completes before
the session's runs and calls fail because of it: StreamJsonRpc notices a broken pipe before the operating system
reports the exit, so the session holds those failures back until it knows the exit, and kills a process whose
connection broke but which does not exit. So code that catches `ConnectionLostException` awaits `Exited` to learn
why. A session cannot be used
again after a kill or a crash, and does not restart by itself: an app that keeps a session checks
`session.Exited.IsCompleted` before it starts a run, and starts a new session then.

**Children of the app.** The C# server and every session's process end when the app ends, even when the app is
killed, so no runner keeps a device open after a crash. Both watch their standard input and exit when it closes, and
on Windows they also run in a Job Object that ends them with the app; the Job Object covers a runner that is stuck,
and standard input covers the moment before the runner is assigned to the Job Object. In a test on Windows, runners
that did neither kept running after the app was killed; with either, they ended 2 to 3 ms after it, also while busy in
a loop or blocked in a sleep.

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
| The script ends with `OperationCanceledException` after its stop token was cancelled, from any token | cancelled |
| The script throws, also `OperationCanceledException` before its stop token was cancelled | `CSharpScriptException` |
| Disposing the globals throws | `CSharpScriptException`; when the script failed too, `Completion` holds both, the script's first |
| The process ends while the run lasts | `ConnectionLostException` |

| Where | Failure | Result |
|---|---|---|
| `StartAsync` | the bridge has no build-time proxies | `InvalidOperationException`, in every build |
| `StartAsync` | the session type's `Create` throws | `CSharpScriptException`, and the process is ended |
| `StartAsync` | the process does not start or answer | `InvalidOperationException`, with its standard error |
| `run.Completion` | the globals' `Create` throws | `CSharpScriptException` |
| `Remote` | the session object throws | `CSharpScriptException` |
| `Remote` | the process has ended, or the session is being disposed | `ConnectionLostException` |
| `DisposeAsync` | the session object's disposal throws | nothing thrown; `Exited`'s `DisposeException` has it |
| the session | code that a script left running, such as a timer, throws | the process ends, as any .NET app does: `Exited` says `Crashed`, and the session's runs end with `ConnectionLostException` |

What scripts see when they call the app side:

| Failure | In the script |
|---|---|
| The app side throws | `RemoteInvocationException`, with the app's message |
| The run is stopped while the script waits, also by disposing the session | `OperationCanceledException`; the app side's token is cancelled |
| The session is killed while the script waits | nothing: the script ends with the process |

`CSharpScriptException` derives from StreamJsonRpc's `RemoteInvocationException`; its `ScriptPosition` is the innermost
frame in a script's own code, and `ScriptSnapshot` the compiled text it points into, for jumping to it. The session's connection maps failures in the runner to it through
StreamJsonRpc's error hook, and maps a refused call to `ConnectionLostException` by throwing from that hook.

## 8. Not public, on purpose

- **The LSP layer.** Protocol types, JSON-RPC and process plumbing are internal. Languages plug in through
  `LanguageServerOptions`: a command line, initialization options and settings answers.
- **A language service interface.** The prototype's public `ILanguageService` is gone: every language comes through a
  language server. An in-process service could come back later, if a language needs one.
- **AvaloniaEdit.** `CodeEditor` wraps AvaloniaEdit's editor instead of deriving from it, so AvaloniaEdit's API is not
  part of ours. `CodeEditorStyles` includes its styles.
- **Popups and policies.** Completion, hover and signature help have no settings yet, and their parts have no style
  classes for apps to restyle. They take the app's look instead. Restart and hang timeouts keep the prototype's
  defaults: a restart waits 0.5 s, doubling up to 30 s; a request unanswered for 30 s counts as a hang, a new server
  gets twice that before its first answer, and each hang doubles the limit; no start or request waits longer than 2
  minutes. 30 s of running forgives
  the failures and hangs before. After 5 failures in a row, which take about 8 s for a server that fails at once, the
  server is `Failed`. `DisposeAsync` waits 2 s for `shutdown` and `exit`, as VS Code does, then kills. Their clock,
  like a session's time limits, is the options' `TimeProvider`, so tests can pass their own.
- **Logging.** The library takes no logger and no logging package. Each signal has one purpose:
  - what an app acts on is in the API: `State`, the exceptions of `StartAsync`, a run's `Completion`, a session's
    `Exited`;
  - what people read arrives as text, line by line: the servers' `StandardError`, a session's `Output`, and the end of
    a runner's standard error in `Exited`;
  - languages and themes that fail to load go to Avalonia's log, as binding errors do.
- **Metrics.** None in v1. The prototype's timing properties and events stay internal, for the library's own tests and
  benchmarks. A public `Meter` comes with a use case, with one instrument per budget of the design's section 1.2.

## 9. Not proven yet

Decision #52 has the C# server compile and the runner run the result without Roslyn. A spike proved it on
linux-x64 with .NET 10 and Roslyn 5.9 (#99): a separate Roslyn process emitted scripts with portable PDBs, and a JIT
runner with no Roslyn assembly loaded ran them:

- synchronous and awaited scripts, return values, local functions, classes, and the globals' members without
  qualification, through the script's `Task<object> <Factory>(object[])` entry with the globals, created by the
  session object's factory;
- 14 compilations side by side in one runner, the same text compiled twice included;
- failures mapped to their line and column in the compiled text, also one thrown by a callback that an earlier run
  left in the session object, after that run's globals were disposed;
- compiling, warm: about 5 ms for 100 lines and 170 ms for 5,000 lines, cold about 0.6 to 0.75 s; the assembly and
  PDB of 5,000 lines take about 150 KB; loading and first call about 1 ms, or 14 ms for 5,000 lines;
- a runner with Roslyn loaded used about 40 MiB more working set after the same scripts.

`#line` moved a reported position, so v1 rejects it, like `#r` and `#load`. The real bridge carrying the compiled
scripts and failures is part of the implementation.

Sessions (section 7, decisions #80 to #86) were tested in spikes: a NativeAOT host with StreamJsonRpc 2.26 and
generated proxies, and a JIT runner with precompiled classes standing in for scripts. Proven:

- one connection per session, with several runs, plain `[JsonRpcContract]` interfaces, and calls in both directions
  while the other side waits;
- the end-of-session order of section 7: refusing calls in the runner's dispatch while cleanup still talks to the app,
  mapping the refusal to `ConnectionLostException`, finishing admitted calls, disposing the session object while the
  app answers, and then cancelling and awaiting app-side calls. That needs StreamJsonRpc's
  `CancelLocallyInvokedMethodsWhenConnectionIsClosed`, and awaiting its dispatch, not only its connection;
- a kill, also in the middle of disposing: pending calls fail with `ConnectionLostException` within about 3 ms;
- a run's values reaching the app before its `Completion`: 300 of 300 runs, every value checked;
- creating the globals in the runner from the types' names: about 15 ns per run once cached, measured with a static
  `Create` on the globals;
- the typed host API published as NativeAOT without warnings, with a run's input copied into the runner;
- naming runner-side types in a NativeAOT app without compiling their code: about 700 bytes of metadata;
- the cost of a minimal session: 80 to 110 ms to the first answered call, about 17 MiB each;
- fast data, with the numbers in section 7;
- loading runner-side dependencies next to a NativeAOT app, on linux-x64 and win-x64: SQLite with its native library
  and System.IO.Ports with its per-platform code, from an untrimmed publish for the platform, through its dependency
  manifest, with the contracts shared across the load contexts. On Windows a portable publish worked too; on Linux it
  picked the wrong per-platform code, so the rule stays a publish per platform. Avalonia's DLLs were in the payload,
  and none of them loaded;
- runners ending with a NativeAOT app that is killed hard, on win-x64: with standard input watched, a Job Object, or
  both, 27 of 27 runners ended 2 to 3 ms after the app; without either, 9 of 9 kept running;
- touching `Dispatcher.UIThread` from a pool thread before Avalonia is set up breaks the app's setup, on Linux and
  Windows; `Dispatcher.FromThread` returns null then, without creating a dispatcher.

Documents, languages and themes (sections 1 and 4) are implemented and tested. Proven:

- nothing in them touches Avalonia's dispatcher before Avalonia is set up: a test process loads YAML and Dark+, and
  creates, edits, undoes and maps a document on a pool thread, then sets up Avalonia on its main thread. The setup
  succeeds, and the document then belongs to the UI thread: it changes there and throws elsewhere;
- `avares://` files load before setup, with Avalonia's standard asset loader;
- TextMateSharp 2.0.5, without its grammar package, tokenizes VS Code's JSON grammar, comments included, its YAML
  grammar with the five grammars it includes, and its XML grammar, also in a NativeAOT app published without
  warnings. Reading a grammar tolerates two things that vscode-textmate tolerates and TextMateSharp does not:
  comments among YAML's captures, and XML's begin rules without an end;
- on linux-x64 (BenchmarkDotNet): an edit plus its snapshot takes about 0.9 µs on 5,000 lines, and allocates
  3.5 KB; the first position in a snapshot reads its line breaks in about 60 µs, later ones take about 12 ns; mapping
  a range takes about 50 ns after one edit and 0.7 µs after 100; loading a built-in language or theme takes about
  18 ms as the first load in a process, and 30 to 80 µs after that; YAML's included grammars take about 28 ms and
  0.8 ms.

The editor (section 2) shows documents with background TextMate highlighting, search and the editing helpers, and a
server's diagnostics, hover, semantic tokens, completion and signature help (below). Proven on linux-x64, with `dotnet test` and as NativeAOT, in headless windows rendered
with Skia:

- the budgets of the design's section 1.2 without a language server, on a quiet machine. Benchmarks measure them
  (BenchmarkDotNet, i7-13700, a warm process), and the comparison of each pull request tracks them; tests assert no
  latency (#102). A new editor of 5,000 lines of JSON or YAML shows syntax colors in a rendered frame 24 to 25 ms
  after it is created. With the layout and the frame after it, a keystroke takes the UI thread 11 ms in 5,000 lines
  and 15 ms on a wrapped line of 2,900 characters, and a page down 13 ms. The background's results lay out again only
  the lines whose tokens changed, so typing inside a long string builds its line once, not twice;
- without a clock, tests check that such an editor shows syntax colors in its pixels, and that typing on a line of
  20,000 characters edits it and colors it by the background's time limit of 50 ms per line: a JSON string, which
  tokenizes in about 2 ms, fully, and an array, which takes seconds, not at all. Typing inside a long string builds
  its line once, and a quote that ends the string colors the line anew;
- tokenizing 5,000 lines in the background takes about 62 ms for JSON and YAML (BenchmarkDotNet); one screen of 50
  lines takes the UI thread about 0.6 ms of the 4 ms it may spend per frame;
- an editor out of the visual tree is collected while its document lives on, also one that had the focus and showed
  edits. That needs AvaloniaEdit 12.0.999-ci3-alpha, a preview with fixes not yet released (see CONTRIBUTING.md):
  its undo steps no longer keep the text areas that showed an edit, and its commands hold the last focused element
  weakly;
- an editor created on another thread than the app's main UI thread fails at once with `InvalidOperationException`;
- the editing helpers follow VS Code's rules, read from the language's `language-configuration.json`: Enter keeps the
  indentation, indents after an opening bracket and by the increase pattern, with its flags, and puts the closing
  bracket of `{|}` on a line of its own; typing a line that matches the decrease pattern outdents it; pairs close
  unless the token types where the user types are a string or comment that the pair's `notIn` excludes, typed closing
  characters step over, Backspace deletes an empty pair the editor closed, and opening characters surround the
  selection; Ctrl+/ toggles line comments, or block comments in XML, as one change of the text whose cost grows with
  the selection, as Replace All's does: 41 ms for 5,000 lines and about 0.15 s for 100,000, with the frame after it.
  Each is one step that the user undoes. The token types come from tokenizing the line up to the caret on the UI
  thread, within 2 ms, from the state at its start. Matching the indentation patterns for one new or typed line stops
  after 10 ms, and a match after 5 ms, as with a pattern that backtracks on the lines above: the line then keeps its
  indentation;
- the document's `Indentation` reaches every editor that shows it at once: Tab, Shift+Tab, new lines and the width of
  tabs;
- the bracket next to the caret and its match are boxed in the theme's colors, ignoring brackets in strings and
  comments by the lines' tokens, also when an edit changes them out of view. The search for a match reads at most
  100,000 characters, in blocks without a string per line: about 0.5 ms with six brackets on every line, the most a
  caret move or keystroke spends on it (BenchmarkDotNet). In 5,000 lines, from text that an undo just changed and with
  the frame after it, typing a bracket that closes takes 28 ms where a digit takes 27 ms, Backspace between the pair
  13 ms, and commenting all 5,000 lines 41 ms;
- spaces and tabs show inside the selection only, in the theme's `editorWhitespace.foreground`, in a layer of the text
  view above the selection;
- search runs in the background, so a regular expression such as `(a+)+$`, which froze the prototype for 21 s, leaves
  the UI thread free; it stops after the time limit of 1 s with a message. A search keeps 10,000 matches at most. In
  5,000 lines of JSON, a word on every line takes 1.2 ms and a regular expression with 10,000 matches 2.3 ms, so the
  time limit leaves about 400 times that; Replace All of 10,000 matches takes the UI thread about 0.1 s with its
  frame. Tests check that a search at either limit says so, and that Replace All then changes nothing. AvaloniaEdit's
  own search panel could not keep this contract: it creates its search strategy itself, searches on the UI thread at
  every change of the text, and keeps every match. The editor removes it each time AvaloniaEdit's editor is
  templated, and AvaloniaEdit's search commands open the editor's bar instead.

The client for any language server (sections 4 and 6) is implemented: `LanguageServer`, its documents and their
diagnostics, on an internal JSON-RPC layer in its own assembly, which the C# server will share. Proven on linux-x64,
with `dotnet test` and as NativeAOT, against a fake server that speaks the protocol with framing and messages of its
own, and with a clock that tests advance:

- `State` from start through a crash, the restart after its delay and disposal, raised once per change on the UI
  thread; `StartAsync`'s failure with the end of standard error, also for an executable that does not start; delays of
  0.5, 1, 2 and 4 s, then `Failed` after 5 failures, and `StartAsync` after `Failed` starting anew; an unanswered
  `initialize` restarted after 60 s, the next one after 120 s, and the third after 120 s too, the longest wait; a
  capability of another JSON type read as not given, and an unexpected failure during a start counted as a failed
  start;
- documents open at creation, with `file:` or untitled URIs; edits reproduced on the server in order, as incremental
  changes, with `\r\n`, `\r` alone, line breaks split by an edit, surrogate pairs, undo and redo, and 200 edits at once;
  documents opened again with their current text after a restart, closed when disposed, moved by `FilePath`, one per
  file path;
- diagnostics about the snapshot the server analyzed, by version or the text last written, not one still being
  written, mapped with `MapToCurrent`,
  set on the UI thread with `PropertyChanged`, and positions outside the text clamped, `int.MaxValue` included;
- `workspace/configuration` answered by `Settings` off the UI thread; all settings sent at the start, after a restart
  and on `NotifySettingsChanged`, also off the UI thread, and `{}` without settings; registrations, unknown
  requests, malformed JSON and an oversized message; framing split across reads and merged into one, cancellation
  both ways, responses and cancellations getting through while 20 of the server's requests run, a server that sends
  without reading the answers ending the connection, and one that stops reading while documents come and go or move,
  answers with parts of another JSON type, an id this end never used, the server's requests cancelled at the end,
  diagnostics for a file URI in VS Code's form, a message that cannot be built failing alone, and a failed write
  ending the connection;
- `DisposeAsync` with `shutdown` and `exit`, its kill after 2 s, and `Dispose` killing at once;
- a server and its document before Avalonia is set up, in the test process of documents: events where the change
  happens, and nothing touching the dispatcher; after setup, the server's `State` and the diagnostics arrive on the UI
  thread;
- the package carries the internal assembly in its `lib` folder and depends on no package of it;
- on win-x64, on CI: a server ends when the app that started it is killed, through the Job Object.

The look of a server's diagnostics, its hover and its semantic tokens (sections 2 and 6) is implemented. Proven on
linux-x64, with `dotnet test` and as NativeAOT, in headless windows rendered with Skia, against the fake server:

- squiggles in the color theme's error, warning and information colors, and in VS Code's where a theme sets none, in
  the rendered pixels under each character of their text and nowhere else; a hint as three dots under its first two
  characters. They follow typing, with text typed at their edges outside them, and go when the diagnostics clear.
  Squiggles of one severity that overlap all show, also an even number of them on one text, and a frame with 10,000
  on one word allocates about what a frame with one does, and one with 10,000 hints in a long line's hidden tail what
  a frame without them does. Editors take the 10,000 most severe of 100,000 diagnostics, while `Diagnostics` keeps
  them all; a batch that arrives from another thread costs the UI thread under 100 KB to take in, where putting the
  10,000 in the text there took 0.9 MB; and squiggles stay on their text through edits made while a batch arrives and
  while no editor shows the document. Where squiggles go matches where the text layout draws each character, with
  tabs, wide and right-to-left text, surrogate pairs, accents, control characters, wrapped rows and a collapsed tail,
  and squiggles show in the pixels under each character of right-to-left words and of a line that mixes directions,
  and a range across a change of direction leaves the characters of the other direction between without one; the
  squiggle over a line wider than the view is shaped no wider than the view and a wave on either side, also scrolled
  sideways;
  a frame with 10,000 errors over all of the text allocates under 1 MB more than one with one such error, where
  reading them for each line in view took 8.4 MB;
- hover: the diagnostics' messages, with their codes, above the server's Markdown, whose code block is highlighted with
  the document's grammar in the editor's font, colors and background; in the app's tooltip, in the window's overlay
  layer, inside a window narrower than the tooltip. An editor in a dark `ThemeVariantScope` of a light window shows a
  dark popup, in its pixels. Inline code shows in the editor's font, foreground and background, and code without a color
  of its own in the editor's foreground, in the pixels. The popup stays while the pointer is over its word or over the
  popup, and closes when the
  pointer leaves both, rests on another word under the same diagnostic, and on typing, Escape, scrolling and focus
  loss. Resting on another word cancels the request still under way with `$/cancelRequest`, and so does Escape
  before any popup showed. A server that stops serving the document and new colors end the hover, and reading its
  Markdown and highlighting its code stop part-way once it ended. A popup stays inside a window that narrows. An empty
  diagnostic at a line's end shows when the pointer rests where its squiggle is. A hover shows at most its characters,
  lines and runs, also for a diagnostic message of 100,000 characters, an answer of 1,000 lines and a paragraph of
  800 runs, and the messages leave the server's hover only what they do not use. A diagnostic of 10 million
  characters with a code allocates under 1 MB to be shown; lines count at `\r\n`, `\n`, `\r` and the line
  separator; a cut keeps surrogate pairs whole; and a popup whose space is not a whole number of pixels stays inside
  the window;
- the Markdown subset: emphasis, also nested, paired as CommonMark pairs it, also in text with thousands of
  delimiters that never close, inline code, escapes, links that open only http and https pages, nested
  lists, hard line breaks, headings, rules and fenced code blocks; and hover answers as `MarkupContent`, `MarkedString`
  and arrays of them;
- semantic tokens in the rendered pixels, with the server's own legend: a type that Light+'s `semanticTokenColors`
  names, and types and modifiers that it colors through VS Code's scopes; asked by range from a server that offers
  ranges, in full from one that does not; asked for again after an edit, whose next edit cancels the request, and when
  scrolling shows lines without them; a token that an edit touches loses its color at once, and one that it moves
  keeps it, also onto a new line;
- a hover takes the 50 most severe of 10,000 diagnostics on one word, in the server's order of each severity, and
  stays while the pointer moves past where the hovered word was cut, in the same word; the hovered word stops where a
  long line's hidden tail starts, and the pointer over its "..." is over no text, so the hover ends there, as it does
  when the pointer goes on to the next line; the pointer may move along the word only in the shown part of its line;
- a line keeps at most one semantic token per character of its shown part: one of 100,000 tokens that overlap, and
  3,000 of 10,000 tokens of one character, also when 30 such lines join into one; an edit that removes a line break
  and a hidden tail of ten million characters reads 3,000 of them; a line of 3,000 tokens is drawn in 250 colored
  runs and one more; and finding the colors of 100,000 combinations of modifiers that no selector names, and of
  100,000 types past the first 1,000, allocates nothing;
- the client advertises exactly what it implements, and only editors that show a document ask for its hover and
  tokens, not an editor out of the visual tree; while the start's settings block, a running server gets no hover or
  token request for a document not yet open in it, editors get nothing, and they learn that the server serves the
  document once it opens there, not when the server runs;
- on linux-x64 (BenchmarkDotNet, i7-13700): taking a server's tokens for all 5,000 lines of JSON with a warning under
  every key, its lines built beforehand off the UI thread, and drawing the frame after it, takes the UI thread 7.0 ms;
  every line in view draws again. The largest batch of diagnostics, 10,000 one-character errors apart, all of them on
  the screen of a 4K window: preparing it takes the server's thread 0.9 ms, 4.0 ms when it is about text 100 edits
  old; taking it in takes the UI thread 13 µs, 79 µs after 100 edits since; drawing the squiggles 2.9 ms, and 3.2 ms
  with each error over all of the text; and the
  headless frame, which also rasterizes, 107 ms against 92 ms without diagnostics. Finding what a hover shows among
  10,000 on one character, each one found beating those before, takes 0.75 ms. Taking a server's tokens on every
  character of the 32 lines on screen of an 800 by 600 window, 3,000 a line, and drawing the frame after it takes
  57 ms, and 300 a line 45 ms; taking them and laying out the lines alone, 35 ms and 28 ms. One error over 100
  unwrapped lines of 3,000 characters in an 800 by 2,160 window takes 3.7 ms to draw. Showing the
  largest hover in its worst shape, a code block of 50 lines, 249 runs that alternate colors and 2,000 characters,
  takes 6.8 ms with its frame; the frame alone about 5 ms. Without the limit on runs, 2,000 runs of one character took
  24 ms.

Completion and signature help (sections 2 and 6) are implemented. Proven on linux-x64, with `dotnet test` and as
NativeAOT, in headless windows rendered with Skia, against the fake server:

- completion opens on Ctrl+Space, pressed with Control, on a trigger character, which the request names, and when
  typing starts a word; not on typing on in a word, nor at the start of one that goes on, nor in a comment;
- the ranking that the C# server will share gives the scores and the matched characters of VS Code's `fuzzyScore`
  and `anyScore`: compared with VS Code 1.145's own code, without its emoji separators, on 25,000 random texts, also
  beyond 128 characters, they agreed on all of them. Tests check prefixes before capitals inside words, equal scores
  in the server's order, a filter text that decides while the label shows what matched, also one that differs from
  the label only after its 128th character, the order of sort texts, and that ranking stops once cancelled; in the
  editor, typing filters on the client without asking again, off the UI thread: right after keystrokes that the UI
  thread did nothing else in between, the list still shows what it showed, and then the
  newest result. The matched characters show in bold, and a preselected item is selected among the best;
- Enter, Tab, a click, the item's own commit character and the server's commit the selected item, each as one step
  that the user undoes, and a commit character then goes in; an item's own commit characters replace the server's.
  A text edit replaces its range, also grown by what was typed since; an insert and replace edit inserts; `insertText`
  replaces the word. While a filter runs, Enter commits the item that shows at once when it still matches, otherwise
  what the filter selects; a commit character then goes in as text. An edit before that filter has run drops the
  commit, also one that leaves the caret where it was, by code or by another editor of the document. With an item
  whose edit starts at the start of a line of two million characters, moving the caret on in the line's collapsed
  tail and filtering again takes the UI thread 2 KB, and Enter, which checks the item while that filter runs, about
  0.8 MB with the commit's edit of the line, where a copy of the line takes 4 MB; an item without an edit replaces
  the last 1,000 characters of a word of two million;
- an editor that becomes read-only closes completion and signature help, and Tab and Ctrl+Space then change
  nothing;
- a detail of 2,000 lines with long documentation shows 50 lines, its budget's, and no documentation; a label of
  5,000 characters shows 256 with "…"; a signature of 5,000 characters shows 2,000 with "…", and its documentation
  not;
- the selected item's resolved detail, as code in the editor's foreground, and its Markdown show beside the list, or
  below it in a window of 420 pixels, narrower beside it in one of 640, wrapped inside the window, with the list at
  the word it completes;
  additional edits apply with the item in one step, and ones that come after the commit join its step, and are
  dropped once the user typed on;
- an incomplete list is asked for again on each character typed, for incomplete completions; a newer request and
  Escape cancel the one under way with `$/cancelRequest`, and selecting another item cancels the resolve under way,
  also when it is an item resolved already;
- an edit before where completion was asked, as by another editor of the document, asks again, and the item then
  replaces the word where it is now;
- documents that no editor shows ask for nothing, and an editor that loses the keyboard focus cancels its request,
  while the editor that has it gets its list;
- icons in Light+ and Dark+ in VS Code's light and dark colors, and in a theme's own `symbolIcon.methodForeground`, in
  the rendered pixels; the list in a dark `ThemeVariantScope` of a light window as a dark flyout, in its pixels,
  inside a small window, and above its line when there is no room below;
- completion ends on Escape, on leaving the word, on focus loss, when its line scrolls out of view, and when the
  server stops serving the document;
- signature help opens on "(" with the active parameter in bold and its Markdown, moves the parameter at once on ",",
  asks again only after 120 ms without an edit, with a clock that the test advances, also after an edit that leaves
  the caret where it was, shows the other signatures on
  Up and Down, tells the server the one that shows, which keeps it, and leaves Up and Down to an open completion
  list; it shows above the line while completion's list shows below, and ends on Escape, on leaving the call, on
  focus loss and when the server stops serving the document;
- the client advertises exactly what it implements, snippets not;
- on linux-x64 (BenchmarkDotNet, i7-13700, warm), in 5,000 lines of JSON, against the fake server answering at once
  with 1,000 items: from Ctrl+Space until a rendered frame shows the list takes 18 ms (median 17.5 ms, p95 21.1 ms),
  within the budget of 50 ms; from typing a character until a rendered frame shows the list filtered to 125 items
  takes 21 ms (median 20.8 ms, p95 33.7 ms), of which the keystroke takes about 10 ms without completion. In the
  worst case, 10,000 items of 128 characters and a typed word of 64 characters that each holds in order, matching
  takes 235 ms off the UI thread (the matcher alone, warm), while a keystroke with its frame takes the UI thread
  10 ms (p95 10.3 ms), no more than with the list closed (12 ms in that run).

Still open:

- hover, semantic tokens, completion and signature help against a real server, such as yaml-language-server, and the
  C# server's once it exists, with the completion budget measured there;
- a server answers the requests of the documents that editors show in its own order: the client cancels what an
  editor no longer needs and asks for nothing that no editor shows, but cannot put one document's request ahead of
  another's in the server. The C# server will take the shown document first;
- the list itself moves left of the word it completes when the word is within the list's width of the window's right
  edge; VS Code would show its details on the list's other side too;
- opening a link through the app's launcher: headless windows have none, so tests check only which links open;
- code blocks are highlighted with the document's grammar whatever language their fence names, and the hover is as
  wide as the app's tooltips allow, 320 pixels in Fluent; both wait for a use case that needs more;

- runner-side dependencies on arm64 and macOS, the real bridge and contracts loaded once for both sides, two versions
  of one package in the app and the runner side, and the build step that deploys them;
- the library's own processes ending with a killed app on Linux and macOS; third-party language servers end there
  only as the protocol asks them to (section 6);
- nothing touching Avalonia's dispatcher before Avalonia is set up, for compiling: the test of documents extended
  with a compilation;
- the exit reason rule and `Exited` completing before failures, when causes race: tests that kill the runner at every
  step of disposing, and call `DisposeAsync` and `KillAsync` together;
- calls that must stay quick, such as prompts, callback replies and stopping a run, while the bridge carries bulk data
  in both directions: a test measures their latency under load;
- integration tests on GitHub Actions for Windows, Linux and macOS, once the API is decided;
- lines over 3,000 characters: the editor shows and edits lines of up to 3,000 characters in full, AvaloniaEdit's
  default, and a longer line collapses its tail (#106). The limit rises once Avalonia's text wrapping and font
  fallback get faster on long lines;
- the parts of VS Code's editing rules that the editor does not read yet: `onEnterRules`, such as JSON's line comment
  continued on Enter, `indentNextLinePattern` and `unIndentedLinePattern`, brackets of several characters for
  matching, and the outdent of a closing bracket typed on an empty line by its opening bracket's line, without a
  decrease pattern. VS Code also strips strings and comments from a line before matching its indentation patterns;
  the editor matches the line as it is;
- the editor's own text, before v1: its context menu and its search bar are in English only, also in an app that
  shows another language, such as the sample app in German. How apps get them in their language is not decided yet.

## 10. Open questions

Question numbers continue the decision log in [design.md](design.md). Decisions taken in the API discussion are
recorded there too (#61, #64–#106).

None right now. Section 9 lists what still needs proving.
