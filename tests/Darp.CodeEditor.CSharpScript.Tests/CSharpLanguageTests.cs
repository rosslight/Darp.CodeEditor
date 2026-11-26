using Darp.CodeEditor.Loading;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.CSharpScript.Tests;

public sealed class CSharpLanguageTests
{
    [Fact]
    public async Task The_language_loads_a_grammar_that_tokenizes_and_its_editing_rules()
    {
        LoadedLanguage csharp = await CodeLanguage.CSharp.Loaded;

        csharp.Grammar.ShouldNotBeNull().GetScopeName().ShouldBe("source.cs");
        (await ScopesAsync("var text = 1; // note")).ShouldContain("comment.line.double-slash.cs");
        csharp.Configuration.LineComment.ShouldBe("//");
        csharp.Configuration.BlockComment.ShouldBe(new CharacterPair("/*", "*/"));
        csharp.Configuration.Brackets.ShouldContain(new CharacterPair("{", "}"));
    }

    [Theory]
    [InlineData("var c = 'a';")]
    [InlineData("var c = '\\'';")]
    [InlineData("var c = '\\x41';")]
    [InlineData("var c = '\\u0041';")]
    public async Task A_char_literal_is_a_string(string line) =>
        (await ScopesAsync(line)).ShouldContain("string.quoted.single.cs");

    [Fact]
    public async Task The_name_of_an_anonymous_type_in_a_hover_is_no_string() =>
        (await ScopesAsync("IOrderedEnumerable<'a> IEnumerable<'a>.OrderByDescending<'a, int>()")).ShouldNotContain(
            "string.quoted.single.cs"
        );

    private static async Task<IEnumerable<string>> ScopesAsync(string line)
    {
        LoadedLanguage csharp = await CodeLanguage.CSharp.Loaded;
        return new Registry(new SingleGrammar(csharp.Grammar.ShouldNotBeNull()))
            .LoadGrammar("source.cs")
            .TokenizeLine(line)
            .Tokens.SelectMany(token => token.Scopes);
    }

    private sealed class SingleGrammar(IRawGrammar grammar) : IRegistryOptions
    {
        private readonly IRawGrammar _grammar = grammar;

        public IRawGrammar? GetGrammar(string scopeName) => scopeName == _grammar.GetScopeName() ? _grammar : null;

        public IRawTheme GetDefaultTheme() => new ThemeRaw();

        public IRawTheme? GetTheme(string scopeName) => null;

        public ICollection<string>? GetInjections(string scopeName) => null;
    }
}
