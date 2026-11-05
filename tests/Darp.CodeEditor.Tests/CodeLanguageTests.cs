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

        Assert.NotNull(json.Grammar);
        Assert.Equal("source.json", json.Grammar.GetScopeName());
        Assert.Equal("//", json.Configuration.LineComment);
        Assert.Equal(new CharacterPair("/*", "*/"), json.Configuration.BlockComment);
        Assert.Equal([new CharacterPair("{", "}"), new CharacterPair("[", "]")], json.Configuration.Brackets);
        Assert.Contains(new AutoClosingPair("\"", "\"", true, true), json.Configuration.AutoClosingPairs);
        Assert.NotNull(json.Configuration.IndentationRules);
        Assert.Equal(
            new IndentationPattern("^\\s*[}\\]],?\\s*$", null),
            json.Configuration.IndentationRules.DecreaseIndentPattern
        );
    }

    [Fact]
    public async Task Json_colors_comments()
    {
        IRawGrammar? grammar = (await CodeLanguage.Json.Loaded).Grammar;

        Assert.NotNull(grammar);
        Assert.Contains(
            TextMate.Tokenize(grammar, "{ // note"),
            scopes => scopes.Any(s => s.StartsWith("comment.line"))
        );
    }

    [Fact]
    public async Task Xml_loads_its_grammar_and_editing_rules()
    {
        LoadedLanguage xml = await CodeLanguage.Xml.Loaded;

        Assert.NotNull(xml.Grammar);
        Assert.Equal("text.xml", xml.Grammar.GetScopeName());
        Assert.Null(xml.Configuration.LineComment);
        Assert.Equal(new CharacterPair("<!--", "-->"), xml.Configuration.BlockComment);
        Assert.Contains(new CharacterPair("<", ">"), xml.Configuration.SurroundingPairs);
        Assert.Contains(new AutoClosingPair("<!--", "-->", true, true), xml.Configuration.AutoClosingPairs);
    }

    [Fact]
    public async Task Yaml_loads_its_grammar_and_the_grammars_it_includes()
    {
        LoadedLanguage yaml = await CodeLanguage.Yaml.Loaded;

        Assert.Equal("#", yaml.Configuration.LineComment);
        Assert.Contains(new AutoClosingPair("'", "'", false, false), yaml.Configuration.AutoClosingPairs);
        Assert.NotNull(yaml.Configuration.IndentationRules);
        // The entry grammar only delegates: keys and comments come from the included YAML 1.2 grammar.
        Assert.NotNull(yaml.Grammar);
        IReadOnlyList<IReadOnlyList<string>> tokens = TextMate.Tokenize(yaml.Grammar, "key: value # note");
        Assert.Contains(tokens, scopes => scopes.Any(s => s.StartsWith("entity.name.tag")));
        Assert.Contains(tokens, scopes => scopes.Any(s => s.StartsWith("comment.line")));
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

        Assert.Equal(LoadedLanguage.None, await language.Loaded);
        Assert.Equal([(LogEventLevel.Error, "CodeEditor")], TestLog.EntriesOf(language));
    }

    [Fact]
    public async Task An_invalid_configuration_leaves_the_language_without_rules_and_logs_an_error()
    {
        TestLog.Install();
        string file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.language-configuration.json");
        await File.WriteAllTextAsync(file, "{ \"brackets\": [[\"(\"", TestContext.Current.CancellationToken);

        var language = new CodeLanguage("broken", configuration: new Uri(file));

        Assert.Equal(LoadedLanguage.None, await language.Loaded);
        Assert.Equal([(LogEventLevel.Error, "CodeEditor")], TestLog.EntriesOf(language));
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

        IndentationRules? rules = (await new CodeLanguage("pascal", configuration: new Uri(file)).Loaded)
            .Configuration
            .IndentationRules;

        Assert.Equal(
            new IndentationRules(
                new IndentationPattern("^\\s*begin\\b", "i"),
                new IndentationPattern("^\\s*end\\b", null)
            ),
            rules
        );
        File.Delete(file);
    }
}
