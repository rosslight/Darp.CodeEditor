# Customizing

This page covers the editor's display options, colors, keys, commands, search and context menu.

## Display options

```csharp
--8<-- "customizing.cs:options"
```

| Property | Default | Effect |
|---|---|---|
| `IsReadOnly` | `false` | The user can only read, select, copy and search |
| `WordWrap` | `false` | Long lines wrap at the editor's width |
| `ShowLineNumbers` | `true` | Line numbers show in the left margin |
| `AutoClosing` | `true` | Typing an opening bracket or quote also types its closing one |
| `FontFamily`, `FontSize` | A monospace font | Set them as for any control |

## Colors

Editors follow your app's theme variant, with VS Code's Light+ and Dark+ themes. To change one editor's variant, wrap it
in a `ThemeVariantScope`, so its popups match:

```csharp
--8<-- "customizing.cs:scope"
```

To use your own colors in every editor, override the `CodeEditorColorTheme` resource with a VS Code color theme file:

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Dark">
    <ce:CodeEditorColorTheme x:Key="CodeEditorColorTheme" Source="avares://MyApp/Themes/brand-dark.json" />
  </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

Completion, hover and signature help use the color theme, and your app's styles don't reach them. The search bar and
the scroll bars use your app's theme. A missing or invalid theme file falls back to Light+ or Dark+, and the error goes
to Avalonia's log.

## Keys

The editor's keys are ordinary Avalonia `KeyBindings`. On macOS, Cmd takes Ctrl's place, except in Ctrl+Space. The
Command column names the members of `CodeEditorCommands`.

| Action | Command | Keys |
|---|---|---|
| Open completion | `TriggerSuggest` | Ctrl+Space, Ctrl+I (also Option+Esc on macOS) |
| Toggle completion details | `ToggleSuggestionDetails` | Ctrl+Space, Ctrl+I or the hover key, while the list shows |
| Accept completion | `AcceptSelectedSuggestion` | Enter, while the list shows |
| Accept, replacing the rest of the word | `AcceptAlternativeSelectedSuggestion` | Tab, Shift+Enter, while the list shows |
| Signature help | `TriggerParameterHints` | Ctrl+Shift+Space |
| Previous or next signature | `ShowPrevParameterHint`, `ShowNextParameterHint` | Alt+Up, Alt+Down, while several show |
| Hover at the caret | `ShowHover` | Ctrl+Q on Windows, F1 on Linux and macOS |
| Next or previous problem | `NextMarker`, `PreviousMarker` | F8, Shift+F8 |
| Find | `Find` | Ctrl+F |
| Replace | `Replace` | Ctrl+H (Cmd+Option+F on macOS) |
| Next or previous match | `FindNext`, `FindPrevious` | F3, Shift+F3 (also Cmd+G, Cmd+Shift+G on macOS) |
| Toggle comment | `CommentLine` | Ctrl+/ |
| Move lines | `MoveLinesUp`, `MoveLinesDown` | Alt+Up, Alt+Down |
| Copy lines | `CopyLinesUp`, `CopyLinesDown` | Shift+Alt+Up, Shift+Alt+Down (with Ctrl on Linux) |
| Delete lines | `DeleteLines` | Ctrl+Shift+K |
| Insert a line below or above | `InsertLineAfter`, `InsertLineBefore` | Ctrl+Enter, Ctrl+Shift+Enter |
| Indent, outdent | `IndentLines`, `OutdentLines` | Ctrl+], Ctrl+[ |
| Select line | `ExpandLineSelection` | Ctrl+L |
| Undo | `Undo` | Ctrl+Z |
| Redo | `Redo` | Ctrl+Y, Ctrl+Shift+Z |
| Cut, copy, paste, select all | `Cut`, `Copy`, `Paste`, `SelectAll` | Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A |

Each default binding names its command in `CommandParameter`. Change a key by setting its `Gesture`:

```csharp
--8<-- "customizing.cs:rebind"
```

Remove a key:

```csharp
--8<-- "customizing.cs:remove"
```

Keys that the editor doesn't use, such as Ctrl+S or F5, go on to your app. So does a key whose command doesn't apply,
such as Ctrl+Z with nothing to undo. Bind app shortcuts on an ancestor:

```csharp
--8<-- "customizing.cs:app-key"
```

The editor handles plain Enter, Tab, Escape and the arrows itself, so don't bind them on its ancestors. Bindings from
your XAML run before the defaults. To add one to the editor from code after it is attached, use
`KeyBindings.Insert(0, binding)`.

## Commands

`RunCommand` runs any of the `CodeEditorCommands`, for your own menus and buttons. It returns `false` when the command
doesn't apply:

```csharp
--8<-- "customizing.cs:command"
```

`Cut()`, `Copy()`, `Paste()` and `SelectAll()` are shorthands for it.

## Search

The search bar searches plain text as the user types, in the background, with Match Case and Match Whole Word. Open it
from code:

```csharp
--8<-- "customizing.cs:search"
```

A search keeps at most 10,000 matches. Beyond that, the bar says so and Replace All is off, so it never replaces only
some of the matches.

## Built-in behavior

These work without setup:

- **Typing:** pairs close as you type unless `AutoClosing` is off. Typing the closing character steps over it, Backspace
  deletes an empty pair, and an opening character surrounds the selection.
- **Undo:** typing undoes by word.
- **Highlights:** the current line, the bracket at the caret with its match, whitespace inside the selection, and the
  occurrences of the symbol at the caret, from the server or else by whole word.
- **Completion snippets:** `$1`, `${1}`, `${1:text}` and `$0` become stops. The first stop is selected, and Tab and
  Shift+Tab move between stops. Other snippet syntax adds no stops: a choice inserts its first option, and a variable
  its default or nothing.
- **Hover:** shows the messages of the diagnostics under the pointer, above the server's Markdown. Code blocks use the
  document's grammar.
- **Long lines:** the full line shows, but squiggles and semantic colors stop after its first 3,000 characters. With the
  caret past that point, typing, Delete and Backspace work, while Paste, Enter and Tab do nothing.

## Context menu

A right click opens the editor's `ContextFlyout`, a `MenuFlyout` with Cut, Copy, Paste and Select All. Change it through
its `Items`, or replace it with your own, for example to translate it.

To run an example with these options:

```sh
dotnet run samples/docs/customizing.cs
```

## Rules

- Use a `ThemeVariantScope` to change one editor's variant, so its popups match.
- Bind app shortcuts on an ancestor, and never plain Enter, Tab or Escape.
- Find a default key by its `CommandParameter`, and change its `Gesture`.
- Use `RunCommand` with `CodeEditorCommands` for menus and buttons.
