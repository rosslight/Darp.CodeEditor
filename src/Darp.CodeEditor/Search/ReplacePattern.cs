using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Search;

/// <summary>
/// What replaces each match of a regular expression, in VS Code's syntax rather than .NET's, a port of its
/// <c>replacePattern.ts</c>: <c>$0</c> to <c>$99</c> and <c>$&amp;</c> for the match and its groups, <c>$$</c> for a
/// dollar sign, <c>\n</c>, <c>\t</c> and <c>\\</c> for a line break, a tab and a backslash, and <c>\u</c>,
/// <c>\l</c>, <c>\U</c> and <c>\L</c> before a group to upper- or lowercase its first character, or all of it. Anything
/// else stays as it is, .NET's own <c>${name}</c> too. Preserve Case is its <c>buildReplaceStringWithCasePreserved</c>.
/// </summary>
/// <remarks>
/// As in JavaScript, two digits after <c>$</c> name a group only while the expression has that many groups:
/// <c>$10</c> of an expression with one group is that group and a 0.
/// </remarks>
internal sealed class ReplacePattern
{
    /// <summary>
    /// The punctuation inside words that is case-ignorable: Unicode's MidLetter, MidNumLet and Single_Quote.
    /// </summary>
    private const string WordPunctuation =
        "'.:\u00B7\u0387\u05F4\u2018\u2019\u2024\u2027\uFE13\uFE52\uFE55\uFF07\uFF0E\uFF1A";

    private static readonly FrozenDictionary<char, string> s_upperExpansions = UpperExpansions();

    private readonly IReadOnlyList<Piece> _pieces;

    private ReplacePattern(IReadOnlyList<Piece> pieces) => _pieces = pieces;

    /// <summary>A replacement taken as it is, as for a query that is not a regular expression.</summary>
    public static ReplacePattern Static(string text) => new([new Piece(text, 0, "")]);

    /// <summary>Reads <paramref name="replacement"/>.</summary>
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
            else if (char.IsAsciiDigit(next))
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
    /// groups takes the case of the match (<see cref="WithCaseOf"/>); one with groups stays as it is.
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
            result.Append(Cased(value[i], upper: c is 'U' or 'u'));
            if (c is 'u' or 'l')
                op++;
        }
    }

    /// <summary>
    /// <paramref name="replacement"/> in the case of <paramref name="match"/>: in upper case for a match in upper case,
    /// in lower case for one in lower case, otherwise with its first character in the case of the match's first
    /// character. Where both have as many parts between hyphens, or else between underscores, each part takes the case
    /// of the match's part.
    /// </summary>
    private static string WithCaseOf(string match, string replacement)
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
        if (Cased(match, upper: true) == match)
            return Cased(replacement, upper: true);
        if (Cased(match, upper: false) == match)
            return Cased(replacement, upper: false);
        if (replacement.Length == 0)
            return replacement;
        if (Cased(match[0], upper: false) != match[..1])
            return Cased(replacement[0], upper: true) + replacement[1..];
        if (Cased(match[0], upper: true) != match[..1])
            return Cased(replacement[0], upper: false) + replacement[1..];
        return replacement;

        static bool HaveSameParts(string match, string replacement, char separator) =>
            match.Contains(separator)
            && replacement.Contains(separator)
            && match.Split(separator).Length == replacement.Split(separator).Length;
    }

    /// <summary>
    /// <paramref name="text"/> in upper or lower case by Unicode's full case mappings, as JavaScript's
    /// <c>toUpperCase</c> and <c>toLowerCase</c>: .NET maps a character to one, so that <c>ß</c> stays <c>ß</c> where
    /// they make it <c>SS</c>, and <c>Σ</c> is <c>σ</c> also at the end of a word, where they make it <c>ς</c>.
    /// </summary>
    private static string Cased(string text, bool upper)
    {
        var cased = new StringBuilder(text.Length);
        int from = 0;
        for (int i = 0; i < text.Length; i++)
        {
            string? expanded =
                Expanded(text[i], upper) ?? (!upper && text[i] == '\u03A3' && IsFinalSigma(text, i) ? "\u03C2" : null);
            if (expanded is null)
                continue;
            cased.Append(Simple(text[from..i])).Append(expanded);
            from = i + 1;
        }
        return from == 0 ? Simple(text) : cased.Append(Simple(text[from..])).ToString();

        string Simple(string part) => upper ? part.ToUpperInvariant() : part.ToLowerInvariant();
    }

    /// <summary>
    /// Whether the capital sigma at <paramref name="index"/> ends a word, by Unicode's Final_Sigma: a cased letter
    /// before it and none after it, apart from case-ignorable characters such as marks and apostrophes between.
    /// </summary>
    private static bool IsFinalSigma(string text, int index)
    {
        ReadOnlySpan<char> span = text;
        Rune rune;
        int before = index;
        do
        {
            if (Rune.DecodeLastFromUtf16(span[..before], out rune, out int length) != OperationStatus.Done)
                return false;
            before -= length;
        } while (IsCaseIgnorable(rune));
        if (!IsCased(rune))
            return false;
        int after = index + 1;
        while (
            Rune.DecodeFromUtf16(span[after..], out rune, out int length) == OperationStatus.Done
            && IsCaseIgnorable(rune)
        )
            after += length;
        return after == text.Length || !IsCased(rune);

        static bool IsCased(Rune rune) =>
            Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.UppercaseLetter
                    or UnicodeCategory.LowercaseLetter
                    or UnicodeCategory.TitlecaseLetter;

        static bool IsCaseIgnorable(Rune rune) =>
            Rune.GetUnicodeCategory(rune)
                is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.Format
                    or UnicodeCategory.ModifierLetter
                    or UnicodeCategory.ModifierSymbol
            || (rune.IsBmp && WordPunctuation.Contains((char)rune.Value));
    }

    /// <summary><paramref name="c"/> in upper or lower case by Unicode's full case mappings.</summary>
    private static string Cased(char c, bool upper) =>
        Expanded(c, upper) ?? (upper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToString();

    /// <summary>The case of <paramref name="c"/> where it is longer than one character; null elsewhere.</summary>
    private static string? Expanded(char c, bool upper) =>
        upper ? s_upperExpansions.GetValueOrDefault(c)
        : c == '\u0130' ? "i\u0307"
        : null;

    /// <summary>The characters whose upper case is longer than one character, by Unicode's SpecialCasing.txt.</summary>
    private static FrozenDictionary<char, string> UpperExpansions()
    {
        const string Table =
            "\u00DFSS|\u0149\u02BCN|\u01F0J\u030C|\u0390\u0399\u0308\u0301|\u03B0\u03A5\u0308\u0301|\u0587\u0535\u0552|"
            + "\u1E96H\u0331|\u1E97T\u0308|\u1E98W\u030A|\u1E99Y\u030A|\u1E9AA\u02BE|\u1F50\u03A5\u0313|"
            + "\u1F52\u03A5\u0313\u0300|\u1F54\u03A5\u0313\u0301|\u1F56\u03A5\u0313\u0342|\u1FB2\u1FBA\u0399|"
            + "\u1FB3\u0391\u0399|\u1FB4\u0386\u0399|\u1FB6\u0391\u0342|\u1FB7\u0391\u0342\u0399|\u1FBC\u0391\u0399|"
            + "\u1FC2\u1FCA\u0399|\u1FC3\u0397\u0399|\u1FC4\u0389\u0399|\u1FC6\u0397\u0342|\u1FC7\u0397\u0342\u0399|"
            + "\u1FCC\u0397\u0399|\u1FD2\u0399\u0308\u0300|\u1FD3\u0399\u0308\u0301|\u1FD6\u0399\u0342|"
            + "\u1FD7\u0399\u0308\u0342|\u1FE2\u03A5\u0308\u0300|\u1FE3\u03A5\u0308\u0301|\u1FE4\u03A1\u0313|"
            + "\u1FE6\u03A5\u0342|\u1FE7\u03A5\u0308\u0342|\u1FF2\u1FFA\u0399|\u1FF3\u03A9\u0399|\u1FF4\u038F\u0399|"
            + "\u1FF6\u03A9\u0342|\u1FF7\u03A9\u0342\u0399|\u1FFC\u03A9\u0399|\uFB00FF|\uFB01FI|\uFB02FL|\uFB03FFI|"
            + "\uFB04FFL|\uFB05ST|\uFB06ST|\uFB13\u0544\u0546|\uFB14\u0544\u0535|\uFB15\u0544\u053B|"
            + "\uFB16\u054E\u0546|\uFB17\u0544\u053D";
        Dictionary<char, string> upper = Table.Split('|').ToDictionary(entry => entry[0], entry => entry[1..]);
        // Greek with ypogegrammeni or prosgegrammeni: the capital without it, and a capital iota.
        foreach (
            (char first, char capital) in (ReadOnlySpan<(char, char)>)
                [('\u1F80', '\u1F08'), ('\u1F90', '\u1F28'), ('\u1FA0', '\u1F68')]
        )
        {
            for (int i = 0; i < 16; i++)
                upper[(char)(first + i)] = $"{(char)(capital + i % 8)}\u0399";
        }
        return upper.ToFrozenDictionary();
    }

    /// <summary>Text, or a group with the case changes before it.</summary>
    private readonly record struct Piece(string? Text, int Group, string CaseOps);
}
