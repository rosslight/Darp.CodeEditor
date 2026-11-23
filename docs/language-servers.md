# Language servers

A language server adds completion, signature help, hover, diagnostics, semantic colors and occurrence highlights. This
page shows how to connect one, configure it, follow its state and shut it down. The editor connects to servers over
stdio and uses the features each one offers. Diagnostics must be pushed with `textDocument/publishDiagnostics`. Your app
ships or finds the server itself.

## Connect a server

This example uses VS Code's JSON server:

```sh
npm install -g vscode-langservers-extracted
```

On Windows, npm installs a `.cmd` script, so set `FileName` to its full path.

Describe the server, then let it create the document:

```csharp
--8<-- "language-server.cs:connect"
```

The process starts with the first document, and the UI never waits for it. Text and colors show at once, and language
features follow when the server answers. To start it earlier, call `StartAsync`, which throws with the reason if the
process doesn't start.

One server can serve several documents and languages. Give each open document of a server a different `filePath`, or
none. Servers use the path to find nearby files and configuration. The document never reads or writes that file.

To run the complete example:

```sh
dotnet run samples/docs/language-server.cs
```

## Configure a server

`LanguageServerOptions` describes the process and what to tell it:

| Option | Use |
|---|---|
| `FileName`, `Arguments` | The executable and its arguments, usually `--stdio` |
| `WorkingDirectory`, `EnvironmentVariables` | The process environment |
| `RootPath` | The workspace folder, if the server needs one |
| `InitializationOptions` | The `initializationOptions` of the `initialize` request |
| `Settings` | The app's settings, per section |
| `StandardError` | Each line that the server writes to standard error |

`Settings` receives a section, or `null` for all settings. It can run on any thread:

```csharp
--8<-- "language-server.cs:settings"
```

Answer both forms, because servers differ: VS Code's JSON server reads its settings only when the client sends all of
them, and the YAML server asks for its section. The JSON server also validates only when a setting turns that on.

Call `NotifySettingsChanged()` when the answers change.

## Follow its state

`State` is a `LanguageServerState`, for a status indicator. Changes are raised on the UI thread:

```csharp
--8<-- "language-server.cs:state"
```

| State | Meaning |
|---|---|
| `NotStarted` | No document needed the server yet |
| `Starting` | The process hasn't answered `initialize` yet |
| `Running` | The server answers requests |
| `Restarting` | It crashed or stopped answering, and a new process starts shortly |
| `Failed` | It failed too often in a row. `StartAsync` tries again |
| `Stopped` | The server was disposed |

Documents keep working when a server fails. They only lose language features until it runs again. Pass `StandardError`
to your log, because servers explain their failures there.

## Use diagnostics

`document.Diagnostics` holds the server's latest diagnostics, and `PropertyChanged` reports when they change. Each
one refers to the snapshot the server analyzed, so map its range before you use it in the current text:

```csharp
--8<-- "language-server.cs:diagnostic"
```

Each diagnostic has a `DiagnosticSeverity`: `Error`, `Warning`, `Information` or `Hint`. F8 skips hints. Use the severity
to summarize problems:

```csharp
--8<-- "language-server.cs:problems"
```

`Diagnostics` holds everything the server reported. Editors show at most 10,000 per document, the most severe first.

## Shut down

Keep one server for as long as your app needs it. Dispose its documents first, then the server. During normal use, stop
it gracefully:

```csharp
--8<-- "language-server.cs:stop"
```

In the app's exit handler, where only synchronous code runs, use `Dispose()`, which kills the process at once:

```csharp
--8<-- "language-server.cs:exit"
```

## Rules

- Create server documents with `server.CreateDocument`, not `CodeDocument.Create`.
- Give each open document of a server a different `filePath`, or none.
- Answer the `null` section in `Settings` as well as named sections.
- Map a diagnostic's range with `MapToCurrent` before you select it.
- Dispose documents before their server: `DisposeAsync()` in normal use, `Dispose()` at app exit.
