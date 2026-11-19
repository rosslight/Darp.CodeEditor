using Avalonia.Logging;
using Darp.CodeEditor.Loading;
using TextMateSharp.Internal.Types;

namespace Darp.CodeEditor.Tests;

/// <summary>Languages load their grammar and editing rules from VS Code's files, or fail into plain text.</summary>
public sealed class CodeLanguageTests
{
    [Fact]
    public async Task Json_loads_its_grammar_and_editing_rules()
    {
        LoadedLanguage json = await CodeLanguage.Json.Loaded;

        json.Grammar.ShouldNotBeNull().GetScopeName().ShouldBe("source.json");
        json.Configuration.LineComment.ShouldBe("//");
        json.Configuration.BlockComment.ShouldBe(new CharacterPair("/*", "*/"));
        json.Configuration.Brackets.ShouldBe([new CharacterPair("{", "}"), new CharacterPair("[", "]")]);
        json.Configuration.AutoClosingPairs.ShouldContain(new AutoClosingPair("\"", "\"", true, true));
        // Without surrounding pairs of its own, the auto-closing pairs surround, as in VS Code.
        json.Configuration.SurroundingPairs.ShouldContain(new CharacterPair("(", ")"));
        json.Configuration.IndentationRules.ShouldNotBeNull().StartMatching().Decreases("  },").ShouldBeTrue();
    }

    [Fact]
    public void Quick_suggestions_are_VS_Codes_in_code_only_and_also_in_strings_for_JSON_and_YAML()
    {
        var code = new QuickSuggestions(Other: true, Comments: false, Strings: false);
        var alsoStrings = new QuickSuggestions(Other: true, Comments: false, Strings: true);

        QuickSuggestions.Default.ShouldBe(code);
        new CodeLanguage("toml").QuickSuggestions.ShouldBe(code);
        CodeLanguage.PlainText.QuickSuggestions.ShouldBe(code);
        CodeLanguage.Xml.QuickSuggestions.ShouldBe(code);
        CodeLanguage.Json.QuickSuggestions.ShouldBe(alsoStrings);
        CodeLanguage.Yaml.QuickSuggestions.ShouldBe(alsoStrings);
        new CodeLanguage("toml") { QuickSuggestions = alsoStrings }.QuickSuggestions.ShouldBe(alsoStrings);
    }

    /// <summary>Each built-in language, with a line that its grammar gives scopes of its own.</summary>
    public static TheoryData<string, string> BuiltInSamples =>
        new()
        {
            { "json", "{ \"key\": [1, true] }" },
            { "xml", "<root a=\"v\"><!-- note --></root>" },
            { "yaml", "key: value # note" },
        };

    [Theory]
    [MemberData(nameof(BuiltInSamples))]
    public async Task Each_built_in_language_tokenizes_into_scopes_below_its_root(string id, string sample)
    {
        CodeLanguage language = id switch
        {
            "json" => CodeLanguage.Json,
            "xml" => CodeLanguage.Xml,
            _ => CodeLanguage.Yaml,
        };
        IRawGrammar grammar = (await language.Loaded).Grammar.ShouldNotBeNull();

        IReadOnlyList<IReadOnlyList<string>> tokens = TextMate.Tokenize(grammar, sample);

        tokens.ShouldContain(scopes => scopes.Any(scope => scope != grammar.GetScopeName()));
    }

    [Fact]
    public async Task Json_colors_comments()
    {
        IRawGrammar grammar = (await CodeLanguage.Json.Loaded).Grammar.ShouldNotBeNull();

        TextMate.Tokenize(grammar, "{ // note").ShouldContain(scopes => scopes.Any(s => s.StartsWith("comment.line")));
    }

    [Fact]
    public async Task Xml_loads_its_grammar_and_editing_rules()
    {
        LoadedLanguage xml = await CodeLanguage.Xml.Loaded;

        xml.Grammar.ShouldNotBeNull().GetScopeName().ShouldBe("text.xml");
        xml.Configuration.LineComment.ShouldBeNull();
        xml.Configuration.BlockComment.ShouldBe(new CharacterPair("<!--", "-->"));
        xml.Configuration.SurroundingPairs.ShouldContain(new CharacterPair("<", ">"));
        xml.Configuration.AutoClosingPairs.ShouldContain(new AutoClosingPair("<!--", "-->", true, true));
    }

    [Fact]
    public async Task Yaml_loads_its_grammar_and_the_grammars_it_includes()
    {
        LoadedLanguage yaml = await CodeLanguage.Yaml.Loaded;

        yaml.Configuration.LineComment.ShouldBe("#");
        yaml.Configuration.AutoClosingPairs.ShouldContain(new AutoClosingPair("'", "'", false, false));
        yaml.Configuration.IndentationRules.ShouldNotBeNull();
        // The entry grammar only delegates: keys and comments come from the included YAML 1.2 grammar.
        IReadOnlyList<IReadOnlyList<string>> tokens = TextMate.Tokenize(
            yaml.Grammar.ShouldNotBeNull(),
            "key: value # note"
        );
        tokens.ShouldContain(scopes => scopes.Any(s => s.StartsWith("entity.name.tag")));
        tokens.ShouldContain(scopes => scopes.Any(s => s.StartsWith("comment.line")));
    }

    [Theory]
    [InlineData("avares://Darp.CodeEditor/Languages/missing.tmLanguage.json")]
    [InlineData("file:///missing/missing.tmLanguage.json")]
    public async Task A_missing_grammar_leaves_the_language_uncolored_and_logs_an_error(string grammar)
    {
        TestLog.Install();

        var language = new CodeLanguage(
            "broken",
            new Uri(grammar),
            new Uri("avares://Darp.CodeEditor/Languages/json.language-configuration.json")
        );

        (await language.Loaded).ShouldBe(LoadedLanguage.None);
        TestLog.EntriesOf(language).ShouldBe([(LogEventLevel.Error, "CodeEditor")]);
    }

    [Fact]
    public async Task An_invalid_configuration_leaves_the_language_without_rules_and_logs_an_error()
    {
        TestLog.Install();
        string file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.language-configuration.json");
        await File.WriteAllTextAsync(file, "{ \"brackets\": [[\"(\"", TestContext.Current.CancellationToken);

        var language = new CodeLanguage("broken", configuration: new Uri(file));

        (await language.Loaded).ShouldBe(LoadedLanguage.None);
        TestLog.EntriesOf(language).ShouldBe([(LogEventLevel.Error, "CodeEditor")]);
        File.Delete(file);
    }

    [Fact]
    public async Task Indentation_rules_keep_their_flags()
    {
        string file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.language-configuration.json");
        await File.WriteAllTextAsync(
            file,
            """
            { "indentationRules": {
                "increaseIndentPattern": { "pattern": "^\\s*begin\\b", "flags": "i" },
                "decreaseIndentPattern": "^\\s*end\\b" } }
            """,
            TestContext.Current.CancellationToken
        );

        IndentationRules rules = (
            await new CodeLanguage("pascal", configuration: new Uri(file)).Loaded
        ).Configuration.IndentationRules.ShouldNotBeNull();

        IndentationMatching matching = rules.StartMatching();
        matching.Increases("BEGIN").ShouldBeTrue();
        matching.Decreases("end").ShouldBeTrue();
        matching.Decreases("END").ShouldBeFalse();
        File.Delete(file);
    }
}
