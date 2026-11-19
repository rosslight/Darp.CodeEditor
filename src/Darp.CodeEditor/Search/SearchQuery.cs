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
    /// inside the text searched where the query can match a line break, and in a text with surrogate pairs its atoms
    /// take a code point whole (<see cref="AtomsMatchCodePoints"/>).
    /// </summary>
    /// <param name="codePoints">Whether the text searched has surrogates, which atoms must not split.</param>
    /// <param name="regex">The regular expression; null where the query is an invalid one.</param>
    /// <param name="error">Why the query is invalid, for the user, in the words of .NET's message.</param>
    public bool TryCreateRegex(
        bool codePoints,
        [NotNullWhen(true)] out Regex? regex,
        [NotNullWhen(false)] out string? error
    )
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
        if (codePoints && UseRegex && AtomsMatchCodePoints(Text) is var rewritten && rewritten != Text)
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
    /// The pattern with each atom that can match half of a surrogate pair taking a code point whole instead: <c>.</c>,
    /// a character class such as <c>[^a]</c>, and <c>\W</c>, <c>\S</c>, <c>\D</c>, <c>\p{…}</c> and <c>\P{…}</c>. At a
    /// pair, such an atom takes the pair where it takes its first half, and nothing otherwise: <c>[^b]</c> matches an
    /// emoji once, and <c>(.)(.)</c> doesn't match it.
    /// </summary>
    internal static string AtomsMatchCodePoints(string pattern)
    {
        var rewritten = new StringBuilder(pattern.Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            int end = AtomEnd(pattern, i);
            if (end > i)
                rewritten.Append(CodePoint(pattern[i..end]));
            else
            {
                // Other escapes and comments as they are: neither holds an atom.
                end =
                    pattern[i] == '\\' ? Math.Min(i + 2, pattern.Length)
                    : pattern.AsSpan(i).StartsWith("(?#", StringComparison.Ordinal)
                        ? (pattern.IndexOf(')', i) is var close and >= 0 ? close + 1 : pattern.Length)
                    : i + 1;
                rewritten.Append(pattern, i, end - i);
            }
            i = end - 1;
        }
        return rewritten.ToString();
    }

    /// <summary><paramref name="atom"/>, taking a surrogate pair whole where it takes its first half.</summary>
    internal static string CodePoint(string atom)
    {
        const string Pair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";
        return $@"(?:(?={Pair}){atom}[\uDC00-\uDFFF]|(?!{Pair}){atom})";
    }

    /// <summary>The end of the atom that can match half of a surrogate pair at <paramref name="i"/>; i for none.</summary>
    private static int AtomEnd(string pattern, int i) =>
        pattern[i] switch
        {
            '.' => i + 1,
            '[' => ClassEnd(pattern, i),
            '\\' when i + 1 < pattern.Length && pattern[i + 1] is 'W' or 'S' or 'D' => i + 2,
            '\\' when i + 1 < pattern.Length && pattern[i + 1] is 'p' or 'P' => pattern.IndexOf('}', i) + 1,
            _ => i,
        };

    /// <summary>The end of the character class that starts at <paramref name="start"/>.</summary>
    private static int ClassEnd(string pattern, int start)
    {
        int i = start + 1;
        // A ] first in the class, also after ^, is one of its characters.
        if (i < pattern.Length && pattern[i] == '^')
            i++;
        if (i < pattern.Length && pattern[i] == ']')
            i++;
        // How deep in classes, with .NET's subtractions, such as [a-z-[aeiou]], inside.
        int depth = 1;
        bool dash = false;
        for (; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
                i++;
            else if (c == '[' && dash)
                depth++;
            else if (c == ']' && --depth == 0)
                return i + 1;
            dash = c == '-';
        }
        return pattern.Length;
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
