using System.Text;
using System.Text.RegularExpressions;
using Darp.CodeEditor.Highlighting;
using TextMateSharp.Internal.Grammars;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Lines as a language's indentation rules and rules for Enter see them, as VS Code's <c>IndentationLineProcessor</c>
/// makes them: without the brackets in strings, comments and regular expressions, by the grammar's tokens, so that a
/// brace in a string or a comment neither indents nor outdents. A line without brackets, or whose tokens are unknown,
/// is as it is. Made for one indentation, it tokenizes a line at most once, also when that indentation reads its
/// parts.
/// </summary>
internal sealed class RuleLines
{
    private readonly Func<int, string> _text;
    private readonly Func<int, bool, int[]?> _exactTokens;
    private readonly Regex? _brackets;

    /// <summary>The tokens of the line asked for last, and whether it was tokenized now if need be; 0 for none.</summary>
    private (int Number, int[]? Tokens, bool Now) _tokens;

    /// <param name="text">The text of a line by its number.</param>
    /// <param name="exactTokens">
    /// The tokens of a line by its number as its text is now, TextMate's binary tokens, tokenized now if the flag says
    /// so; null when unknown.
    /// </param>
    /// <param name="brackets">Finds the language's brackets; null for none.</param>
    public RuleLines(Func<int, string> text, Func<int, bool, int[]?> exactTokens, Regex? brackets)
    {
        _text = text;
        _exactTokens = exactTokens;
        _brackets = brackets;
    }

    /// <summary>The text of line <paramref name="number"/>, as it is.</summary>
    public string Text(int number) => _text(number);

    /// <summary>
    /// Line <paramref name="number"/> as the rules see it, by its tokens as last tokenized, while they are exact.
    /// </summary>
    public string Masked(int number)
    {
        string text = _text(number);
        return Masked(number, text, 0, text.Length, tokenizeNow: false);
    }

    /// <summary>
    /// The part of line <paramref name="number"/> from <paramref name="from"/> to <paramref name="to"/> as the rules see
    /// it, tokenized now if need be: the line where the user types.
    /// </summary>
    public string Masked(int number, int from, int to) => Masked(number, _text(number), from, to, tokenizeNow: true);

    /// <summary>These lines, with line <paramref name="number"/> as <paramref name="text"/>, which the rules see as it is.</summary>
    public RuleLines With(int number, string text) =>
        new(n => n == number ? text : _text(n), (n, now) => n == number ? null : Tokens(n, now), _brackets);

    private int[]? Tokens(int number, bool tokenizeNow)
    {
        if (_tokens.Number != number || (tokenizeNow && !_tokens.Now))
            _tokens = (number, _exactTokens(number, tokenizeNow), tokenizeNow);
        return _tokens.Tokens;
    }

    private string Masked(int number, string text, int from, int to, bool tokenizeNow)
    {
        // Most lines have no brackets, and need no tokens.
        if (
            _brackets is not { } brackets
            || from >= to
            || !brackets.IsMatch(text)
            || Tokens(number, tokenizeNow) is not { } tokens
        )
            return text[from..to];
        StringBuilder? masked = null;
        int copied = from;
        for (int i = 0; i < tokens.Length; i += 2)
        {
            int start = Math.Clamp(tokens[i], from, to);
            int end = Math.Clamp(i + 2 < tokens.Length ? tokens[i + 2] : to, from, to);
            if (end <= start || (TokenType)EncodedTokenAttributes.GetTokenType(tokens[i + 1]) == TokenType.Other)
                continue;
            string part = text[start..end];
            string without = brackets.Replace(part, "");
            if (without.Length == part.Length)
                continue;
            masked ??= new StringBuilder(to - from);
            masked.Append(text, copied, start - copied).Append(without);
            copied = end;
        }
        return masked is null ? text[from..to] : masked.Append(text, copied, to - copied).ToString();
    }
}
