using System.Text;
using System.Text.RegularExpressions;
using Darp.CodeEditor.Highlighting;
using TextMateSharp.Internal.Grammars;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// Lines as the indentation rules and the rules for Enter see them, as VS Code's <c>IndentationLineProcessor</c> makes
/// them: without the brackets in strings, comments and regular expressions, so that a brace in a string or a comment
/// neither indents nor outdents.
/// </summary>
internal static class RuleText
{
    /// <summary>
    /// The part of <paramref name="line"/> from <paramref name="from"/> to <paramref name="to"/>, without what
    /// <paramref name="brackets"/> finds in each of its strings, comments and regular expressions, by the line's
    /// <paramref name="tokens"/>, TextMate's binary tokens. As it is without tokens or brackets.
    /// </summary>
    public static string Of(string line, int[]? tokens, Regex? brackets, int from, int to)
    {
        if (tokens is null || brackets is null || from >= to)
            return line[from..to];
        StringBuilder? stripped = null;
        int copied = from;
        for (int i = 0; i < tokens.Length; i += 2)
        {
            // Tokens may be from before an edit inside the line: clamped to it.
            int start = Math.Clamp(tokens[i], from, to);
            int end = Math.Clamp(i + 2 < tokens.Length ? tokens[i + 2] : to, from, to);
            if (end <= start || (TokenType)EncodedTokenAttributes.GetTokenType(tokens[i + 1]) == TokenType.Other)
                continue;
            string part = line[start..end];
            string without = brackets.Replace(part, "");
            if (without.Length == part.Length)
                continue;
            stripped ??= new StringBuilder(to - from);
            stripped.Append(line, copied, start - copied).Append(without);
            copied = end;
        }
        return stripped is null ? line[from..to] : stripped.Append(line, copied, to - copied).ToString();
    }
}
