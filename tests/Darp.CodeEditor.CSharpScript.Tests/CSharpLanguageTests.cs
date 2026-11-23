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

        IRawGrammar grammar = csharp.Grammar.ShouldNotBeNull();
        grammar.GetScopeName().ShouldBe("source.cs");
        new Registry(new SingleGrammar(grammar))
            .LoadGrammar("source.cs")
            .TokenizeLine("var text = 1; // note")
            .Tokens.SelectMany(token => token.Scopes)
            .ShouldContain("comment.line.double-slash.cs");
        csharp.Configuration.LineComment.ShouldBe("//");
        csharp.Configuration.BlockComment.ShouldBe(new CharacterPair("/*", "*/"));
        csharp.Configuration.Brackets.ShouldContain(new CharacterPair("{", "}"));
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
