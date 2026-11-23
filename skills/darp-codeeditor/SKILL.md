---
name: darp-codeeditor
description: Darp.CodeEditor in Avalonia apps. Use when adding a CodeEditor, or working with its documents, language servers, saving, tabs or themes.
---

# Darp.CodeEditor

The app owns each `CodeDocument`: its text, undo history and diagnostics. A `CodeEditor` is a view of one document,
with its own caret, selection and scroll position.

Every member is documented in `lib/net10.0/Darp.CodeEditor.xml` in this package. Read a type's `<remarks>` before using
it in a way this page does not cover. Task guides for the latest release are at
<https://rosslight.github.io/Darp.CodeEditor/llms.txt>. Where they differ, the XML documentation matches the installed
version.

## Add an editor

Add the styles once, after the app's theme:

```xml
<Application xmlns:ce="using:Darp.CodeEditor" ...>
  <Application.Styles>
    <FluentTheme />
    <ce:CodeEditorStyles />
  </Application.Styles>
</Application>
```

Place the editor in a view:

```xml
<ce:CodeEditor Name="Editor" />
```

Create a document and show it:

```csharp
CodeDocument document = CodeDocument.Create(CodeLanguage.Json, """{ "name": "value" }""");
Editor.Document = document;
```

The built-in languages are `PlainText`, `Json`, `Xml` and `Yaml`. For another language, create one
`new CodeLanguage(id, grammarUri, configurationUri)` from a VS Code extension's files and share it between documents.

## Documents

- **Lifetime:** dispose a document when the user closes it for good, including one without a server. Keep it across
  tab switches, so its undo history survives.
- **Tabs:** when one editor shows several documents, call `SaveViewState()` before switching and `RestoreViewState()`
  after switching back.
- **Threads:** use the editor and change documents on the UI thread. Read `CurrentSnapshot` from any thread.
- **Text changes:** listen to `TextChanged`, then read the text when needed. Each `Text` read copies the whole text.
- **Edits:** use `Replace(range, text)` for a change at a position. Editors keep their scroll position, and a caret
  after the range moves with the text.

The app reads and writes files itself. Save from a snapshot, so edits made during the write aren't marked as saved:

```csharp
TextSnapshot snapshot = document.CurrentSnapshot;
await File.WriteAllTextAsync(path, snapshot.GetText());
document.MarkUnmodified(snapshot);
```

If `MarkUnmodified` returns `false`, the text changed during the write, so save again.

## Language servers

A server provides completion, hover, signature help and diagnostics for the documents it creates:

```csharp
var server = new LanguageServer(new LanguageServerOptions
{
    FileName = serverPath,
    Arguments = ["--stdio"],
    StandardError = line => Trace.WriteLine(line, "language-server"),
});
CodeDocument document = server.CreateDocument(CodeLanguage.Json, text, filePath);
```

- The process starts with the first document and restarts after a crash. Bind `State` for a status indicator.
- Give each open document of a server a different `filePath`, or none.
- Keep one server for as long as the app needs it. Dispose its documents, then the server: `DisposeAsync()` during
  normal use, `Dispose()` in the app's exit handler.

## Look and keys

- Editors follow the app's theme variant. To change one editor, wrap it in a `ThemeVariantScope`, so its popups match.
- The editor handles plain Enter, Tab and Escape itself. Bind app shortcuts to other keys on its ancestors.
