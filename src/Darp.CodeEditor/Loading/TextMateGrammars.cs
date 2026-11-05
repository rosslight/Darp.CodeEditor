using System.Collections;
using TextMateSharp.Internal.Grammars.Parser;
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
        if (string.IsNullOrEmpty(grammar.GetScopeName()))
            throw new InvalidDataException("The grammar has no scopeName.");
        if (grammar is Raw raw)
            TolerateAsVsCode(raw);
        return grammar;
    }

    private static readonly string[] s_captureKeys = ["captures", "beginCaptures", "endCaptures", "whileCaptures"];

    /// <summary>
    /// Tolerates in <paramref name="rule"/> and the rules below it what vscode-textmate tolerates and TextMateSharp
    /// does not. The grammar files stay as VS Code ships them.
    /// </summary>
    private static void TolerateAsVsCode(Raw rule)
    {
        // VS Code's YAML grammars put comment strings among captures. vscode-textmate skips them; TextMateSharp takes
        // every entry for a rule and fails on them.
        foreach (string key in s_captureKeys)
        {
            if (rule.TryGetValue(key, out object? value) && value is Raw captures)
            {
                foreach (string comment in captures.Keys.Where(k => captures[k] is not Raw).ToList())
                    captures.Remove(comment);
            }
        }
        // VS Code's XML grammar has begin rules without an end or a while. vscode-textmate ends them with \uFFFF, which
        // text does not contain; TextMateSharp throws on the first line.
        if (rule.ContainsKey("begin") && !rule.ContainsKey("end") && !rule.ContainsKey("while"))
            rule["end"] = "\uFFFF";
        foreach (object value in rule.Values)
        {
            if (value is Raw child)
                TolerateAsVsCode(child);
            else if (value is IList list)
            {
                foreach (Raw item in list.OfType<Raw>())
                    TolerateAsVsCode(item);
            }
        }
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
