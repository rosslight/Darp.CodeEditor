# Darp.CodeEditor sample app

An Avalonia app that uses Darp.CodeEditor: a few documents of a made-up app, one editor, and settings. It is built on
the Avalonia template, with FluentAvalonia for its look. It shows how to use the API, nothing else.

```sh
dotnet run --project samples/Darp.CodeEditor.Sample
```

## What it shows

| Where | API |
|---|---|
| `App.axaml` | `CodeEditorStyles` after the app's theme |
| `App.axaml.cs`, `Documents/Workspace.cs` | the app owns its documents and disposes them: on closing, and when it exits |
| `Documents/FileType.cs` | the built-in languages, and Python as a language of the app's own, from VS Code's files shipped as Avalonia resources |
| `Documents/Workspace.cs` | documents in memory (`CodeDocument.Create`), and documents from files, with `FilePath` |
| `Documents/OpenDocument.cs` | saving the text of a `TextSnapshot`, then `MarkUnmodified(snapshot)`; `IsModified` for a document's state: no file, unsaved changes, or saved |
| `MainWindow.axaml.cs` | opening, saving and closing documents, from the title bar, a document's right-click menu and the shortcuts; the status bar, with the document's `Indentation` |
| `Documents/DocumentView.axaml.cs` | one editor for every document, with `SaveViewState` and `RestoreViewState`; `Undo` and `Redo`, with `CanUndo` and `CanRedo` in the title bar; `OpenSearch`; go to line with `GetOffset`, `Selection` and `ScrollTo`; `Replace` at the selection; `CaretPosition` for the status bar |
| `Documents/DocumentView.axaml`, `Settings/` | `WordWrap`, `ShowLineNumbers` and `FontSize`, from the title bar's View menu; the app's theme variant, which editors follow, and a `ThemeVariantScope` that gives the editor and its popups another one |

Cut, Copy, Paste and Select All are the editor's own right-click menu. The app's own text is in English and German
(`Localization/`); the editor's context menu and search bar are in English only for now.

## Publishing

One project, published both ways:

```sh
dotnet publish samples/Darp.CodeEditor.Sample -c Release -r linux-x64                      # JIT
dotnet publish samples/Darp.CodeEditor.Sample -c Release -r linux-x64 -p:PublishAot=true   # NativeAOT
```

CI publishes it as NativeAOT with `/warnaserror`. FluentAvalonia's theme styles the DataGrid, so the DataGrid's code
stays in the app with trim and AOT warnings, although the app shows none; the project file ignores those, and only
those.

Third-party notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
