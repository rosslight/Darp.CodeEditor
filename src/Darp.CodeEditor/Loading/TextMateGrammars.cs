using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;

namespace Darp.CodeEditor.Loading;

/// <summary>Reads TextMate grammars, and finds the built-in ones by scope name for grammars that include them.</summary>
internal static class TextMateGrammars
{
    /// <summary>The built-in grammars, loaded when first included. YAML's grammar includes the versions and embedded YAML.</summary>
    private static readonly Dictionary<string, Lazy<IRawGrammar?>> s_builtIn = new()
    {
        ["source.json"] = BuiltIn("json.tmLanguage.json"),
        ["text.xml"] = BuiltIn("xml.tmLanguage.json"),
        ["source.yaml"] = BuiltIn("yaml.tmLanguage.json"),
        ["source.yaml.1.0"] = BuiltIn("yaml-1.0.tmLanguage.json"),
        ["source.yaml.1.1"] = BuiltIn("yaml-1.1.tmLanguage.json"),
        ["source.yaml.1.2"] = BuiltIn("yaml-1.2.tmLanguage.json"),
        ["source.yaml.1.3"] = BuiltIn("yaml-1.3.tmLanguage.json"),
        ["source.yaml.embedded"] = BuiltIn("yaml-embedded.tmLanguage.json"),
    };

    /// <summary>The built-in grammar of <paramref name="scopeName"/>; null when there is none or it failed to load.</summary>
    public static IRawGrammar? GetBuiltIn(string scopeName) => s_builtIn.GetValueOrDefault(scopeName)?.Value;

    /// <summary>Reads a <c>.tmLanguage.json</c> file.</summary>
    /// <exception cref="InvalidDataException">The file has no scope name.</exception>
    public static IRawGrammar Read(Uri file)
    {
        using var reader = new StreamReader(AssetFiles.Open(file));
        IRawGrammar grammar = GrammarReader.ReadGrammarSync(reader);
        return string.IsNullOrEmpty(grammar.GetScopeName())
            ? throw new InvalidDataException("The grammar has no scopeName.")
            : grammar;
    }

    private static Lazy<IRawGrammar?> BuiltIn(string fileName)
    {
        var file = new Uri($"avares://Darp.CodeEditor/Languages/{fileName}");
        return new Lazy<IRawGrammar?>(() =>
        {
            try
            {
                return Read(file);
            }
            catch (Exception e)
            {
                AssetFiles.LogError(typeof(TextMateGrammars), file.ToString(), e);
                return null;
            }
        });
    }
}
