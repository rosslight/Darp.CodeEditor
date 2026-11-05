using Darp.CodeEditor.Loading;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.Tests;

/// <summary>Tokenizes with TextMateSharp, from the built-in grammars, as the editor will.</summary>
internal static class TextMate
{
    /// <summary>The scopes of each token of <paramref name="line"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Tokenize(IRawGrammar grammar, string line)
    {
        var registry = new Registry(new Grammars(grammar));
        IGrammar loaded = registry.LoadGrammar(grammar.GetScopeName());
        return [.. loaded.TokenizeLine(line).Tokens.Select(t => (IReadOnlyList<string>)[.. t.Scopes])];
    }

    /// <summary>A language's grammar, and the built-in ones it may include.</summary>
    private sealed class Grammars(IRawGrammar grammar) : IRegistryOptions
    {
        private readonly IRawGrammar _grammar = grammar;

        public IRawGrammar? GetGrammar(string scopeName) =>
            scopeName == _grammar.GetScopeName() ? _grammar : TextMateGrammars.GetBuiltIn(scopeName);

        public IRawTheme GetDefaultTheme() => new ThemeRaw();

        public IRawTheme? GetTheme(string scopeName) => null;

        public ICollection<string>? GetInjections(string scopeName) => null;
    }
}
