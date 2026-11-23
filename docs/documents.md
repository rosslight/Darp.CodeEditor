# Documents and editors

This page explains who owns what, and how to read, change and save text, switch tabs and work across threads.

## The model

A `CodeDocument` is the text of one script or file. A `CodeEditor` is a view of one document. Several editors can show
the same document, each with its own caret.

| Belongs to | State |
|---|---|
| `CodeDocument` | Text, language, undo history, indentation, file path, diagnostics |
| `CodeEditor` | Caret, selection, scroll position, display options |
| Your app | Creating and disposing documents, reading and writing files |

## Create and dispose

`CodeDocument.Create(language, text)` creates a document without a language server. A
[language server](language-servers.md) creates the documents it serves.

Keep a document for as long as the user has it open, including while another tab shows, so its undo history survives.
Dispose it when the user closes it for good. An editor that still shows a disposed document becomes empty.

## Read and change text

Use `Replace` for a change at a position. It is one undo step, editors keep their scroll position, and a caret after
the range moves with the text:

```csharp
--8<-- "documents.cs:replace"
```

- `Text` returns or replaces the whole text. Each read copies it, so avoid reading it on every keystroke.
- `TextChanged` fires after every change, by the user or by the app. Read the text only when you need it.
- `CurrentSnapshot` is an immutable `TextSnapshot` of the text and its version, and costs nothing to take.
- `Undo()`, `Redo()`, `CanUndo` and `CanRedo` work on the document's history.
- `Indentation` sets tabs or spaces. It starts as the text indents, and changing it leaves existing text alone.

`PropertyChanged` reports `IsModified`, `CanUndo`, `CanRedo`, `Indentation` and `Diagnostics`, so you can bind them. It
doesn't report `Text`.

## Save a file

The editor never reads or writes files. Save from a snapshot, so that edits made during the write aren't marked as
saved:

```csharp
--8<-- "documents.cs:save"
```

When the text changed after the snapshot, `MarkUnmodified` returns `false` and keeps the previous unmodified point. The
current text then still needs saving, even if `IsModified` is `false`, as after undoing during the write. Set `FilePath`
after "Save as". The document keeps its text and undo history.

## Switch tabs

Setting `Document` shows the other document from its start. To return to the same place, save the view state before the
switch and restore it afterwards:

```csharp
--8<-- "documents.cs:tabs"
```

A view state follows the edits made since it was saved. It lives in memory only, so it can't be stored across app
starts. If you keep one editor per tab instead, each editor keeps its own place.

## Positions

- `TextRange` and `TextSelection` use offsets, in UTF-16 code units from the start of the text.
- `TextPosition` uses a line and a column, both counted from 1, as editors show them.
- `TextSnapshot.GetPosition` and `GetOffset` convert between them.
- Members that take offsets or ranges clamp them to the text.

## Threads

Use editors, and change documents, on the UI thread. Other threads get an `InvalidOperationException`. You can create,
read and dispose a document on any thread, and its events are raised on the UI thread.

For background work, take a snapshot on the UI thread and pass it along:

```csharp
--8<-- "documents.cs:background"
```

## Rules

- Your app owns each document: keep it across tab switches, and dispose it when it is closed.
- Change documents and use editors on the UI thread only.
- Save from a `CurrentSnapshot`, then call `MarkUnmodified` with that snapshot and check its result.
- Listen to `TextChanged` instead of reading `Text` on every change.
