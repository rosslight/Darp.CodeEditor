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

The editor's keys are ordinary Avalonia `KeyBindings`. On macOS, Cmd takes Ctrl's place, except in Ctrl+Space.

| Action | Keys |
|---|---|
| Completion | Ctrl+Space, Ctrl+I (also Option+Esc on macOS) |
| Completion details, while the list shows | Ctrl+Space, Ctrl+I, or the hover key |
| Signature help | Ctrl+Shift+Space |
| Hover at the caret | Ctrl+Q on Windows, F1 on Linux and macOS |
| Next or previous problem | F8, Shift+F8 |
| Find, replace | Ctrl+F, Ctrl+H (Cmd+Option+F on macOS) |
| Next or previous match | F3, Shift+F3 (also Cmd+G, Cmd+Shift+G on macOS) |
| Toggle comment | Ctrl+/ |
| Move lines | Alt+Up, Alt+Down |
| Copy lines | Shift+Alt+Up, Shift+Alt+Down (with Ctrl on Linux) |
| Delete lines | Ctrl+Shift+K |
| Insert a line below or above | Ctrl+Enter, Ctrl+Shift+Enter |
| Indent, outdent | Ctrl+], Ctrl+[ |
| Select line | Ctrl+L |
| Undo, redo | Ctrl+Z, Ctrl+Y, Ctrl+Shift+Z |
| Clipboard, select all | Ctrl+X, Ctrl+C, Ctrl+V, Ctrl+A |

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
