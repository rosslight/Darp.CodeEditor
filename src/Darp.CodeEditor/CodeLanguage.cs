using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor;

/// <summary>
/// What kind of text a document holds: its TextMate grammar for colors and its editing rules (comments, brackets,
/// auto-closing pairs, indentation rules, rules for Enter). Language servers are not part of it: a server creates the
/// documents it serves, see <see cref="LanguageServer.CreateDocument"/>.
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
    /// JSON and YAML complete in strings too, where their keys and values are: VS Code's extensions for them set
    /// <c>editor.quickSuggestions</c> so.
    /// </summary>
    private static readonly QuickSuggestions s_alsoInStrings = QuickSuggestions.Default with { Strings = true };

    private static readonly Lazy<CodeLanguage> s_json = new(() => BuiltIn("json", quickSuggestions: s_alsoInStrings));

    private static readonly Lazy<CodeLanguage> s_xml = new(() => BuiltIn("xml"));

    /// <summary>
    /// YAML indents by brackets and the rules for Enter, not by its indentation rules, which indent the line after every
    /// key: VS Code's YAML extension sets <c>editor.autoIndent</c> so for the language.
    /// </summary>
    private static readonly Lazy<CodeLanguage> s_yaml = new(() =>
        BuiltIn("yaml", AutoIndent.Advanced, s_alsoInStrings)
    );

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
    /// How far the editor indents by itself in the language's documents. Default: <see cref="AutoIndent.Full"/>.
    /// <see cref="Yaml"/> takes <see cref="AutoIndent.Advanced"/>, so Enter after a key does not indent. To change a
    /// built-in language's, create the language from its files:
    /// <c>new CodeLanguage("yaml", CodeLanguage.Yaml.Grammar, CodeLanguage.Yaml.Configuration) { AutoIndent = … }</c>.
    /// </summary>
    public AutoIndent AutoIndent { get; init; } = AutoIndent.Full;

    /// <summary>
    /// Where typing a word opens completion in the language's documents. Default: <see cref="QuickSuggestions.Default"/>,
    /// in code only. <see cref="Json"/> and <see cref="Yaml"/> also open it in strings. To change a built-in language's,
    /// create the language from its files, as for <see cref="AutoIndent"/>.
    /// </summary>
    public QuickSuggestions QuickSuggestions { get; init; } = QuickSuggestions.Default;

    /// <summary>The grammar and editing rules, once read; <see cref="LoadedLanguage.None"/> when a file failed.</summary>
    internal Task<LoadedLanguage> Loaded { get; }

    private static CodeLanguage BuiltIn(
        string id,
        AutoIndent autoIndent = AutoIndent.Full,
        QuickSuggestions? quickSuggestions = null
    ) =>
        new(
            id,
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.tmLanguage.json"),
            new Uri($"avares://Darp.CodeEditor/Languages/{id}.language-configuration.json")
        )
        {
            AutoIndent = autoIndent,
            QuickSuggestions = quickSuggestions ?? QuickSuggestions.Default,
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
