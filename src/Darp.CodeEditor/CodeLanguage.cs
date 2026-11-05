namespace Darp.CodeEditor;

/// <summary>
/// What kind of text a document holds: its TextMate grammar for colors, its editing rules (comments, brackets,
/// auto-closing pairs, indentation rules) and, optionally, the language server that provides completion, hover,
/// signature help and diagnostics.
/// </summary>
/// <remarks>
/// <para>
/// The editor's built-in languages are static properties: <see cref="PlainText"/>, <see cref="Json"/>,
/// <see cref="Xml"/> and <see cref="Yaml"/>. Packages add theirs as extension members, such as <c>CodeLanguage.CSharp</c>
/// from Darp.CodeEditor.CSharpScript. Built-in languages are made with the public constructor, as an app makes its own.
/// </para>
/// <para>
/// Grammars and editing rules use the formats of VS Code extensions (<c>.tmLanguage.json</c> and
/// <c>language-configuration.json</c>), so any language with a VS Code extension can be added from its files. A
/// language starts loading them in the background when it is created; built-in languages when first used. When a file
/// is missing or invalid, documents of the language show plain text without editing rules, and the error goes to
/// Avalonia's log, as binding errors do.
/// </para>
/// <para>Create a language once and share it between documents. It is immutable and safe to use from any thread.</para>
/// </remarks>
public sealed class CodeLanguage
{
    /// <summary>Creates a language from the files of a VS Code extension, and starts loading them.</summary>
    /// <param name="id">The language identifier that the language server receives, such as <c>toml</c>.</param>
    /// <param name="grammar">
    /// The TextMate grammar, a <c>.tmLanguage.json</c> file, as an absolute <c>avares://</c> or <c>file://</c> URI.
    /// It may include the scopes of the built-in grammars; anything else it includes must be in the file itself.
    /// Null: no colors.
    /// </param>
    /// <param name="configuration">
    /// The editing rules, a <c>language-configuration.json</c> file. Null: no comment toggling, bracket matching or
    /// auto-closing pairs.
    /// </param>
    public CodeLanguage(string id, Uri? grammar = null, Uri? configuration = null)
    {
        Id = id;
        Grammar = grammar;
        Configuration = configuration;
    }

    /// <summary>Text without colors or editing rules, such as logs.</summary>
    public static CodeLanguage PlainText { get; } = new("plaintext");

    /// <summary>JSON, also with comments, from VS Code's JSON extension.</summary>
    public static CodeLanguage Json { get; } =
        new(
            "json",
            new Uri("avares://Darp.CodeEditor/Languages/json.tmLanguage.json"),
            new Uri("avares://Darp.CodeEditor/Languages/json.language-configuration.json")
        );

    /// <summary>XML, from VS Code's XML extension.</summary>
    public static CodeLanguage Xml { get; } =
        new(
            "xml",
            new Uri("avares://Darp.CodeEditor/Languages/xml.tmLanguage.json"),
            new Uri("avares://Darp.CodeEditor/Languages/xml.language-configuration.json")
        );

    /// <summary>YAML, from VS Code's YAML extension.</summary>
    public static CodeLanguage Yaml { get; } =
        new(
            "yaml",
            new Uri("avares://Darp.CodeEditor/Languages/yaml.tmLanguage.json"),
            new Uri("avares://Darp.CodeEditor/Languages/yaml.language-configuration.json")
        );

    /// <summary>The language identifier that the language server receives, such as <c>json</c>.</summary>
    public string Id { get; }

    /// <summary>The TextMate grammar; null for none.</summary>
    public Uri? Grammar { get; }

    /// <summary>The editing rules; null for none.</summary>
    public Uri? Configuration { get; }

    /// <summary>The language server of the language's documents, if any.</summary>
    public LanguageServer? Server { get; private init; }

    /// <summary>
    /// A copy of the language whose documents <paramref name="server"/> serves, such as
    /// <c>CodeLanguage.Yaml.WithServer(yamlServer)</c>. The copy shares the grammar and editing rules, loaded once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The language belongs to the server that created it, as C# script languages do.
    /// </exception>
    public CodeLanguage WithServer(LanguageServer server) => throw new NotImplementedException();
}
