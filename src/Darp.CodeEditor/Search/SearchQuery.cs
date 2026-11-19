using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Search;

/// <summary>
/// What the user searches for, and how it matches, as in VS Code: whether it can match a line break as its
/// <c>isMultilineRegexSource</c> decides, its regular expression with the <c>m</c> and <c>u</c> flags, and text taken
/// into it escaped as by its <c>escapeRegExpCharacters</c>.
/// </summary>
/// <param name="Text">The text, or a .NET regular expression; its line breaks are <c>\n</c>.</param>
/// <param name="MatchCase">Whether case counts.</param>
/// <param name="WholeWord">Whether a match must not continue a word on either side.</param>
/// <param name="UseRegex">Whether <paramref name="Text"/> is a regular expression.</param>
internal sealed record SearchQuery(string Text, bool MatchCase, bool WholeWord, bool UseRegex)
{
    /// <summary>
    /// Whether the query can match a line break: text with a line break, or a regular expression with one, or with
    /// <c>\n</c>, <c>\r</c> or <c>\W</c>. Its matches may span lines.
    /// </summary>
    public bool IsMultiline => UseRegex ? IsMultilineRegex(Text) : Text.Contains('\n');

    /// <summary>
    /// A regular expression that matches <paramref name="text"/>, for text taken into the query: with a backslash
    /// before each of <c>\ { } * + ? | ^ $ . [ ] ( )</c>.
    /// </summary>
    public static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is '\\' or '{' or '}' or '*' or '+' or '?' or '|' or '^' or '$' or '.' or '[' or ']' or '(' or ')')
                escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>
    /// The query as a regular expression with the search's time limit: <c>^</c> and <c>$</c> match at the line breaks
    /// inside the text searched where the query can match a line break, and <c>.</c> matches a code point.
    /// </summary>
    /// <param name="regex">The regular expression; null where the query is an invalid one.</param>
    /// <param name="error">Why the query is invalid, for the user, in the words of .NET's message.</param>
    public bool TryCreateRegex([NotNullWhen(true)] out Regex? regex, [NotNullWhen(false)] out string? error)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (!MatchCase)
            options |= RegexOptions.IgnoreCase;
        if (IsMultiline)
            options |= RegexOptions.Multiline;
        try
        {
            regex = new Regex(UseRegex ? Text : Regex.Escape(Text), options, TextSearch.TimeLimit);
            error = null;
        }
        catch (ArgumentException e)
        {
            regex = null;
            error = e.Message;
            return false;
        }
        if (UseRegex && DotsMatchCodePoints(Text) is var rewritten && rewritten != Text)
        {
            // The query as the user wrote it is valid, and its errors read as written: the rewritten one should be.
            try
            {
                regex = new Regex(rewritten, options, TextSearch.TimeLimit);
            }
            catch (ArgumentException) { }
        }
        return true;
    }

    /// <summary>
    /// The pattern with each <c>.</c> outside a character class matching a code point, not half of a surrogate pair:
    /// an emoji is one match of <c>.</c>, not two. Character classes, such as <c>[^a]</c> or <c>\W</c>, still match
    /// half of a pair, which the search then rejects.
    /// </summary>
    internal static string DotsMatchCodePoints(string pattern)
    {
        const string CodePoint = @"(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|.)";
        if (!pattern.Contains('.', StringComparison.Ordinal))
            return pattern;
        var rewritten = new StringBuilder(pattern.Length + CodePoint.Length);
        // How deep in character classes, with .NET's subtractions, such as [a-z-[aeiou]], inside.
        int depth = 0;
        bool dash = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                rewritten.Append(pattern.AsSpan(i, Math.Min(2, pattern.Length - i)));
                i++;
                dash = false;
                continue;
            }
            if (depth > 0)
            {
                if (c == '[' && dash)
                    depth++;
                else if (c == ']')
                    depth--;
                dash = c == '-';
                rewritten.Append(c);
            }
            else if (c == '[')
            {
                depth = 1;
                rewritten.Append(c);
                // A ] first in the class, also after ^, is one of its characters.
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                    rewritten.Append(pattern[++i]);
                if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                    rewritten.Append(pattern[++i]);
            }
            else if (pattern.AsSpan(i).StartsWith("(?#", StringComparison.Ordinal))
            {
                // A comment, to its first ).
                int end = pattern.IndexOf(')', i) is var close and >= 0 ? close + 1 : pattern.Length;
                rewritten.Append(pattern, i, end - i);
                i = end - 1;
            }
            else if (c == '.')
                rewritten.Append(CodePoint);
            else
                rewritten.Append(c);
        }
        return rewritten.ToString();
    }

    private static bool IsMultilineRegex(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\n')
                return true;
            if (pattern[i] != '\\')
                continue;
            if (++i == pattern.Length)
                break;
            if (pattern[i] is 'n' or 'r' or 'W')
                return true;
        }
        return false;
    }
}

/// <summary>What replaces the matches of a search.</summary>
/// <param name="Text">
/// The replacement: for a regular expression in the syntax of <see cref="ReplacePattern"/>, otherwise as it is.
/// </param>
/// <param name="PreserveCase">
/// Whether a replacement without groups takes the case of each match.
/// </param>
internal sealed record SearchReplacement(string Text, bool PreserveCase);
