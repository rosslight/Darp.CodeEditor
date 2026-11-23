# Languages

A `CodeLanguage` gives a document its colors and editing rules. This page covers the built-in languages and how to add
your own. Completion, hover and diagnostics come from a [language server](language-servers.md), not from the language.

## Built-in languages

| Language | Property | Notes |
|---|---|---|
| Plain text | `CodeLanguage.PlainText` | No colors or editing rules |
| JSON | `CodeLanguage.Json` | Also with comments |
| XML | `CodeLanguage.Xml` | |
| YAML | `CodeLanguage.Yaml` | Enter after a key doesn't indent |

## Add a language

A language uses the files of a VS Code extension: a TextMate grammar (`.tmLanguage.json`) and editing rules
(`language-configuration.json`). Create it once and share it between documents:

```csharp
--8<-- "languages.cs:custom"
```

- `id` is the language identifier that a language server receives, such as `toml`.
- `grammar` and `configuration` are absolute `avares://` or `file://` URIs.
- Without a grammar, the text has no colors. Without a configuration, there is no comment toggling, bracket matching or
  auto-closing.
- A grammar may include the scopes of the built-in grammars. Anything else it includes must be in the file itself.

In a shipped app, include the files as Avalonia resources, which also works with NativeAOT:

```xml
<AvaloniaResource Include="Languages\*.json" />
```

Then use URIs such as `avares://MyApp/Languages/toml.tmLanguage.json`.

Languages load in the background. When a file is missing or invalid, documents show plain text without editing rules
and the error goes to Avalonia's log. Call `LogToTrace()` on your `AppBuilder` to see it.

To run an example with a small custom language:

```sh
dotnet run samples/docs/languages.cs
```

## Adjust a built-in language

Built-in languages are immutable. To change one, create a language from its files:

```csharp
--8<-- "languages.cs:adjust"
```

`AutoIndent` sets how far the editor indents by itself. Each level includes the one before it:

| Value | A new line |
|---|---|
| `None` | Starts at the line's start |
| `Keep` | Keeps the indentation of the line before it |
| `Brackets` | Also indents after an opening bracket |
| `Advanced` | Also follows the language's rules for Enter |
| `Full` | Also follows the indentation rules, on Enter and while typing. The default |

`QuickSuggestions` sets where typing a word opens completion: in code, comments or strings. By default it opens in code
only, and JSON and YAML also open it in strings. Ctrl+Space and the server's trigger characters work everywhere.

## Rules

- Create each language once and share it between documents.
- Use absolute `avares://` or `file://` URIs.
- Ship grammars and editing rules as Avalonia resources in a published app.
- Set up an Avalonia log sink, or a failed language file just looks like plain text.
