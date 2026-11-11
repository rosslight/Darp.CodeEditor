using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor;

/// <summary>
/// What kind of text a document holds: its TextMate grammar for colors and its editing rules (comments, brackets,
/// auto-closing pairs, indentation rules). Language servers are not part of it: a server creates the documents it
/// serves, see <see cref="LanguageServer.CreateDocument"/>.
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
/// language starts loading them in the background when it is created; built-in languages when first used. Loading
/// reads <c>avares://</c> files without Avalonia's services, so it works before the app's <c>AppBuilder</c> runs. When
/// a file is missing or invalid, documents of the language show plain text without editing rules, and the error goes
/// to Avalonia's log, as binding errors do; apps see it once they set up a log sink, such as <c>LogToTrace()</c>.
/// </para>
/// <para>Create a language once and share it between documents. It is immutable and safe to use from any thread.</para>
/// </remarks>
public sealed class CodeLanguage
{
    private static readonly Lazy<CodeLanguage> s_plainText = new(() => new CodeLanguage("plaintext"));

    /// <summary>
    /// Quick suggestions also in strings, as VS Code's JSON and YAML extensions set them: values are strings there,
    /// and need completion.
    /// </summary>
    private static readonly QuickSuggestions s_alsoInStrings = new(Other: true, Comments: false, Strings: true);

    private static readonly Lazy<CodeLanguage> s_json = new(() => BuiltIn("json", s_alsoInStrings));

    private static readonly Lazy<CodeLanguage> s_xml = new(() => BuiltIn("xml", QuickSuggestions.Default));

    private static readonly Lazy<CodeLanguage> s_yaml = new(() => BuiltIn("yaml", s_alsoInStrings));

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
        Loaded = grammar is null && configuration is null ? Task.FromResult(LoadedLanguage.None) : Task.Run(Load);
    }

    /// <summary>Text without colors or editing rules, such as logs.</summary>
    public static CodeLanguage PlainText => s_plainText.Value;

    /// <summary>JSON, also with comments, from VS Code's JSON extension.</summary>
    public static CodeLanguage Json => s_json.Value;

    /// <summary>XML, from VS Code's XML extension.</summary>
    public static CodeLanguage Xml => s_xml.Value;

    /// <summary>YAML, from VS Code's YAML extension.</summary>
    public static CodeLanguage Yaml => s_yaml.Value;

    /// <summary>The language identifier that the language server receives, such as <c>json</c>.</summary>
    public string Id { get; }

    /// <summary>The TextMate grammar; null for none.</summary>
    public Uri? Grammar { get; }

    /// <summary>The editing rules; null for none.</summary>
    public Uri? Configuration { get; }

    /// <summary>
    /// Where typing opens completion by itself in the language's documents, as VS Code's <c>editor.quickSuggestions</c>
    /// for the language. Default: <see cref="QuickSuggestions.Default"/>, in code only. <see cref="Json"/> and
    /// <see cref="Yaml"/> open it in strings too, as VS Code's JSON and YAML extensions set it for their languages. To
    /// override a built-in language's, create the language from its files with another value:
    /// <c>new CodeLanguage("json", CodeLanguage.Json.Grammar, CodeLanguage.Json.Configuration) { QuickSuggestions = … }</c>.
    /// </summary>
    public QuickSuggestions QuickSuggestions { get; init; } = QuickSuggestions.Default;

    /// <summary>The grammar and editing rules, once read; <see cref="LoadedLanguage.None"/> when a file failed.</summary>
    internal Task<LoadedLanguage> Loaded { get; }

    private static CodeLanguage BuiltIn(string id, QuickSuggestions quickSuggestions) =>
        new(
            id,
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.tmLanguage.json"),
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.language-configuration.json")
        )
        {
            QuickSuggestions = quickSuggestions,
        };

    private LoadedLanguage Load()
    {
        try
        {
            return new LoadedLanguage(
                Grammar is null ? null : TextMateGrammars.Read(Grammar),
                Configuration is null ? LanguageConfiguration.None : LanguageConfiguration.Read(Configuration)
            );
        }
        catch (Exception e)
        {
            AssetFiles.LogError(this, $"the language '{Id}' from {Grammar} and {Configuration}", e);
            return LoadedLanguage.None;
        }
    }
}
