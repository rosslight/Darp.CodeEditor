using TextMateSharp.Internal.Grammars;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// What TextMate's scopes say a token is, as VS Code's standard token types: a scope with <c>comment</c>,
/// <c>string</c> or <c>regex</c> in its name makes the token one, and the editing helpers treat it so.
/// </summary>
internal enum TokenType
{
    Other = 0,
    Comment = 1,
    String = 2,
    RegEx = 3,
}

internal static class TokenTypes
{
    /// <summary>
    /// The type of the token at <paramref name="column"/>, from TextMate's binary tokens: pairs of a start index and
    /// encoded attributes. The last token reaches to the end of the line, also when the line grew since.
    /// </summary>
    public static TokenType At(int[] tokens, int column)
    {
        // The last token that starts at or before the column.
        int low = 0;
        int high = tokens.Length / 2 - 1;
        if (high < 0)
            return TokenType.Other;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (tokens[2 * middle] <= column)
                low = middle;
            else
                high = middle - 1;
        }
        return (TokenType)EncodedTokenAttributes.GetTokenType(tokens[2 * low + 1]);
    }
}
