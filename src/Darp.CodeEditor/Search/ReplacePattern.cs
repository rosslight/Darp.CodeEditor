using System.Text;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Search;

/// <summary>
/// What replaces each match of a regular expression, in VS Code's syntax rather than .NET's, a port of its
/// <c>replacePattern.ts</c>: <c>$0</c> to <c>$99</c> and <c>$&amp;</c> for the match and its groups, <c>$$</c> for a
/// dollar sign, <c>\n</c>, <c>\t</c> and <c>\\</c> for a line break, a tab and a backslash, and <c>\u</c>,
/// <c>\l</c>, <c>\U</c> and <c>\L</c> before a group to upper- or lowercase its first character, or all of it. Anything
/// else stays as it is, .NET's own <c>${name}</c> too.
/// </summary>
/// <remarks>
/// As in JavaScript, two digits after <c>$</c> name a group only while the expression has that many groups:
/// <c>$10</c> of an expression with one group is that group and a 0.
/// </remarks>
internal sealed class ReplacePattern
{
    /// <summary>The replacement's parts, in order.</summary>
    private readonly IReadOnlyList<Piece> _pieces;

    private ReplacePattern(IReadOnlyList<Piece> pieces) => _pieces = pieces;

    /// <summary>A replacement taken as it is, as for a query that is not a regular expression.</summary>
    public static ReplacePattern Static(string text) => new([new Piece(text, 0, "")]);

    /// <summary>Reads <paramref name="replacement"/> in VS Code's syntax.</summary>
    public static ReplacePattern Parse(string replacement)
    {
        var pieces = new List<Piece>();
        var text = new StringBuilder();
        var caseOps = new StringBuilder();
        // Where the replacement's characters that are still to go into the text start.
        int unchanged = 0;
        for (int i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] == '\\')
            {
                if (++i == replacement.Length)
                    break;
                switch (replacement[i])
                {
                    case '\\':
                        AddText("\\", i);
                        break;
                    case 'n':
                        AddText("\n", i);
                        break;
                    case 't':
                        AddText("\t", i);
                        break;
                    case 'u' or 'U' or 'l' or 'L':
                        AddText("", i);
                        caseOps.Append(replacement[i]);
                        break;
                }
                continue;
            }
            if (replacement[i] != '$')
                continue;
            if (++i == replacement.Length)
                break;
            char next = replacement[i];
            if (next == '$')
                AddText("$", i);
            else if (next is '0' or '&')
                AddGroup(0, i - 1, i);
            else if (char.IsAsciiDigit(next) && next != '0')
            {
                if (i + 1 < replacement.Length && char.IsAsciiDigit(replacement[i + 1]))
                    AddGroup((next - '0') * 10 + (replacement[i + 1] - '0'), i - 1, ++i);
                else
                    AddGroup(next - '0', i - 1, i);
            }
        }
        text.Append(replacement, unchanged, replacement.Length - unchanged);
        if (text.Length > 0)
            pieces.Add(new Piece(text.ToString(), 0, ""));
        return new ReplacePattern(pieces);

        // Adds value in place of the escape whose second character is at end.
        void AddText(string value, int end)
        {
            text.Append(replacement, unchanged, end - 1 - unchanged).Append(value);
            unchanged = end + 1;
        }

        // Adds the group in place of the characters from start to end.
        void AddGroup(int group, int start, int end)
        {
            text.Append(replacement, unchanged, start - unchanged);
            if (text.Length > 0)
                pieces.Add(new Piece(text.ToString(), 0, ""));
            text.Clear();
            pieces.Add(new Piece(null, group, caseOps.ToString()));
            caseOps.Clear();
            unchanged = end + 1;
        }
    }

    /// <summary>
    /// The text that replaces <paramref name="match"/>. With <paramref name="preserveCase"/>, a replacement without
    /// groups takes the case of the match, as VS Code's Preserve Case does (<see cref="WithCaseOf"/>); one with groups
    /// stays as it is, as in VS Code.
    /// </summary>
    public string Build(Match match, bool preserveCase = false)
    {
        if (_pieces is [] or [{ Text: not null }])
        {
            string text = _pieces is [{ Text: { } value }] ? value : "";
            return preserveCase ? WithCaseOf(match.Value, text) : text;
        }
        var result = new StringBuilder();
        foreach (Piece piece in _pieces)
        {
            if (piece.Text is { } value)
                result.Append(value);
            else
                AppendWithCase(result, Substitute(piece.Group, match.Groups), piece.CaseOps);
        }
        return result.ToString();
    }

    /// <summary>
    /// The group's text; for a group of two digits that the expression doesn't have, the group of the first digit and
    /// the second digit, as in JavaScript; <c>$</c> and the digits for a group of one digit that it doesn't have.
    /// </summary>
    private static string Substitute(int group, GroupCollection groups)
    {
        if (group == 0)
            return groups[0].Value;
        string rest = "";
        for (; group > 0; group /= 10)
        {
            if (group < groups.Count)
                return groups[group].Value + rest;
            rest = (char)('0' + group % 10) + rest;
        }
        return "$" + rest;
    }

    /// <summary>
    /// Appends <paramref name="value"/>, its characters in turn changed by <paramref name="caseOps"/>: <c>u</c> and
    /// <c>l</c> change one character and go on to the next change, <c>U</c> and <c>L</c> every character after it.
    /// </summary>
    private static void AppendWithCase(StringBuilder result, string value, string caseOps)
    {
        int op = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (op == caseOps.Length)
            {
                result.Append(value, i, value.Length - i);
                return;
            }
            char c = caseOps[op];
            result.Append(c is 'U' or 'u' ? char.ToUpperInvariant(value[i]) : char.ToLowerInvariant(value[i]));
            if (c is 'u' or 'l')
                op++;
        }
    }

    /// <summary>
    /// <paramref name="replacement"/> in the case of <paramref name="match"/>, a port of VS Code's
    /// <c>buildReplaceStringWithCasePreserved</c>: in upper case for a match in upper case, in lower case for one in
    /// lower case, otherwise with its first character in the case of the match's first character. Where both have as
    /// many parts between hyphens, or else between underscores, each part takes the case of the match's part.
    /// </summary>
    internal static string WithCaseOf(string match, string replacement)
    {
        if (match.Length == 0)
            return replacement;
        bool hyphens = HaveSameParts(match, replacement, '-');
        bool underscores = HaveSameParts(match, replacement, '_');
        if (hyphens != underscores)
        {
            char separator = hyphens ? '-' : '_';
            string[] matchParts = match.Split(separator);
            return string.Join(
                separator,
                replacement.Split(separator).Select((part, i) => WithCaseOf(matchParts[i], part))
            );
        }
        if (match.ToUpperInvariant() == match)
            return replacement.ToUpperInvariant();
        if (match.ToLowerInvariant() == match)
            return replacement.ToLowerInvariant();
        if (replacement.Length == 0)
            return replacement;
        if (char.ToLowerInvariant(match[0]) != match[0])
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        if (char.ToUpperInvariant(match[0]) != match[0])
            return char.ToLowerInvariant(replacement[0]) + replacement[1..];
        return replacement;

        static bool HaveSameParts(string match, string replacement, char separator) =>
            match.Contains(separator)
            && replacement.Contains(separator)
            && match.Split(separator).Length == replacement.Split(separator).Length;
    }

    /// <summary>Text, or a group with the case changes before it.</summary>
    private readonly record struct Piece(string? Text, int Group, string CaseOps);
}
