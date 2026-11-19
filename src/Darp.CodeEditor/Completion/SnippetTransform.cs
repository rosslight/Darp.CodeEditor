using System.Text;
using System.Text.RegularExpressions;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// The transformation of a snippet's placeholder or variable, <c>/regex/format/options</c>, applied as VS Code applies
/// it: the format replaces the first match of the regular expression in the text, or each match with the option
/// <c>g</c>, and the rest of the text stays. When nothing matches and the format has an else branch, the format alone
/// is the result, its groups empty.
/// </summary>
/// <remarks>
/// <para>
/// The regular expression is .NET's, with JavaScript's options as .NET's: <c>i</c> ignores case, invariantly, <c>m</c>
/// makes <c>^</c> and <c>$</c> match at lines, <c>s</c> makes <c>.</c> match line breaks, and <c>y</c> matches only
/// where the text starts, or where the match before ended. <c>g</c> replaces each match; <c>d</c>, <c>u</c> and
/// <c>v</c> change nothing. Other options, an option twice, or <c>u</c> with <c>v</c> make the transformation text, as
/// JavaScript rejects them; so does a regular expression that .NET rejects.
/// </para>
/// <para>
/// The result never splits a surrogate pair: a match, or a group of it that the format uses, that starts or ends between
/// a pair's halves does not count, and the case of a group changes by whole characters. A match that takes longer than
/// <see cref="MatchTimeout"/> counts as none: the transformation runs on the UI thread, and a server's regular
/// expression may backtrack without end on what the user typed.
/// </para>
/// </remarks>
internal sealed class SnippetTransform
{
    /// <summary>How long one match may take: far longer than a real snippet's transformation needs.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>A word of the case shorthands: letters and digits, and characters beyond a surrogate pair's halves.</summary>
    private static readonly Regex s_words = new(
        @"(?:[\p{L}0-9]|[\uD800-\uDBFF][\uDC00-\uDFFF])+",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex s_kebabWords = new(
        @"\p{Lu}{2,}(?=\p{Lu}\p{Ll}+[0-9]*|[\s_-]|$)|\p{Lu}?\p{Ll}+[0-9]*|\p{Lu}(?=\p{Lu}\p{Ll})|\p{Lu}(?=[\s_-]|$)|[0-9]+",
        RegexOptions.CultureInvariant
    );

    private static readonly Regex s_kebabSeparators = new(@"[\s_-]+", RegexOptions.CultureInvariant);

    private static readonly Regex s_edgeUnderscores = new("^_+|_+$", RegexOptions.CultureInvariant);

    private static readonly Regex s_snakeBoundary = new(@"(\p{Ll})(\p{Lu})", RegexOptions.CultureInvariant);

    private static readonly Regex s_snakeSeparators = new(@"[\s\-]+", RegexOptions.CultureInvariant);

    private readonly Regex _regex;
    private readonly bool _global;
    private readonly IReadOnlyList<SnippetFormatPart> _format;

    private SnippetTransform(Regex regex, bool global, IReadOnlyList<SnippetFormatPart> format)
    {
        _regex = regex;
        _global = global;
        _format = format;
    }

    /// <summary>
    /// The transformation of <paramref name="pattern"/> with JavaScript's <paramref name="options"/>, whose matches
    /// <paramref name="format"/> replaces; null when the options or the pattern are invalid.
    /// </summary>
    public static SnippetTransform? Create(string pattern, string options, IReadOnlyList<SnippetFormatPart> format)
    {
        if (
            options.Any(option => !"dgimsuvy".Contains(option))
            || options.Distinct().Count() != options.Length
            || (options.Contains('u') && options.Contains('v'))
        )
        {
            return null;
        }
        RegexOptions regexOptions = RegexOptions.CultureInvariant;
        if (options.Contains('i'))
            regexOptions |= RegexOptions.IgnoreCase;
        if (options.Contains('m'))
            regexOptions |= RegexOptions.Multiline;
        if (options.Contains('s'))
            regexOptions |= RegexOptions.Singleline;
        try
        {
            // Sticky: each match starts where the text starts, or where the match before ended.
            var regex = new Regex(
                options.Contains('y') ? @"\G(?:" + pattern + ")" : pattern,
                regexOptions,
                MatchTimeout
            );
            return new SnippetTransform(regex, options.Contains('g'), format);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary><paramref name="value"/> transformed.</summary>
    public string Resolve(string value)
    {
        var result = new StringBuilder();
        int copied = 0;
        bool matched = false;
        try
        {
            for (Match match = _regex.Match(value); match.Success; match = match.NextMatch())
            {
                if (SplitsPair(value, match))
                    continue;
                result.Append(value, copied, match.Index - copied).Append(Format(match.Groups));
                copied = match.Index + match.Length;
                matched = true;
                if (!_global)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            matched = false;
        }
        if (matched)
            return result.Append(value, copied, value.Length - copied).ToString();
        return _format.Any(part => part is SnippetFormatGroup { Else.Length: > 0 }) ? Format(null) : value;
    }

    /// <summary>Whether the match, or a group of it that the format uses, starts or ends inside a surrogate pair.</summary>
    private bool SplitsPair(string text, Match match)
    {
        if (Inside(match.Index) || Inside(match.Index + match.Length))
            return true;
        foreach (SnippetFormatPart part in _format)
        {
            if (
                part is SnippetFormatGroup { Index: var index }
                && index < match.Groups.Count
                && match.Groups[index] is { Success: true } group
                && (Inside(group.Index) || Inside(group.Index + group.Length))
            )
            {
                return true;
            }
        }
        return false;

        bool Inside(int at) => at > 0 && at < text.Length && char.IsSurrogatePair(text[at - 1], text[at]);
    }

    /// <summary>The format with the groups of a match; with none, each group is empty.</summary>
    private string Format(GroupCollection? groups)
    {
        var text = new StringBuilder();
        foreach (SnippetFormatPart part in _format)
        {
            if (part is SnippetFormatText { Text: var value })
            {
                text.Append(value);
                continue;
            }
            var group = (SnippetFormatGroup)part;
            Group? captured = groups is not null && group.Index < groups.Count ? groups[group.Index] : null;
            text.Append(Resolve(group, captured is { Success: true } ? captured.Value : ""));
        }
        return text.ToString();
    }

    /// <summary>A group's value as the format asks for it: in another case, or as its if or else branch.</summary>
    private static string Resolve(SnippetFormatGroup group, string value) =>
        group.Shorthand switch
        {
            "upcase" => value.ToUpperInvariant(),
            "downcase" => value.ToLowerInvariant(),
            "capitalize" => FirstUpper(value),
            "pascalcase" => Words(value, (word, _) => FirstUpper(word)),
            "camelcase" => Words(value, (word, index) => index == 0 ? FirstLower(word) : FirstUpper(word)),
            "kebabcase" => KebabCase(value),
            "snakecase" => s_snakeSeparators.Replace(s_snakeBoundary.Replace(value, "$1_$2"), "_").ToLowerInvariant(),
            _ when value.Length > 0 && group.If is { } ifValue => ifValue,
            _ when value.Length == 0 && group.Else is { } elseValue => elseValue,
            _ => value,
        };

    /// <summary>The words of letters and digits, each as <paramref name="cased"/> gives it, joined; without any, the value.</summary>
    private static string Words(string value, Func<string, int, string> cased)
    {
        MatchCollection words = s_words.Matches(value);
        return words.Count == 0 ? value : string.Concat(words.Select((word, index) => cased(word.Value, index)));
    }

    /// <summary>The words, split also where the case changes, in lower case, joined by dashes.</summary>
    private static string KebabCase(string value)
    {
        if (!s_words.IsMatch(value))
            return value;
        string cleaned = s_edgeUnderscores.Replace(value.Trim(), "");
        MatchCollection words = s_kebabWords.Matches(cleaned);
        IEnumerable<string> parts =
            words.Count == 0
                ? s_kebabSeparators.Split(cleaned).Where(word => word.Length > 0)
                : words.Select(word => word.Value);
        return string.Join('-', parts.Select(word => word.ToLowerInvariant()));
    }

    private static string FirstUpper(string word) => word[..First(word)].ToUpperInvariant() + word[First(word)..];

    private static string FirstLower(string word) => word[..First(word)].ToLowerInvariant() + word[First(word)..];

    /// <summary>The length of the first character of <paramref name="text"/>: both halves of a surrogate pair.</summary>
    private static int First(string text) =>
        text.Length == 0 ? 0
        : char.IsSurrogatePair(text, 0) ? 2
        : 1;
}

/// <summary>A part of a transformation's format.</summary>
internal abstract record SnippetFormatPart;

/// <summary>Text of a format, which goes in as it is.</summary>
internal sealed record SnippetFormatText(string Text) : SnippetFormatPart;

/// <summary>
/// A reference to a group of the match: <c>$1</c>, <c>${1}</c>, <c>${1:/upcase}</c>, <c>${1:+if}</c>,
/// <c>${1:-else}</c>, <c>${1:else}</c> or <c>${1:?if:else}</c>.
/// </summary>
/// <param name="Index">The group's number: 0 for the whole match.</param>
/// <param name="Shorthand">
/// The case it goes in, by name: <c>upcase</c>, <c>downcase</c>, <c>capitalize</c>, <c>pascalcase</c>,
/// <c>camelcase</c>, <c>kebabcase</c> or <c>snakecase</c>; another name leaves the group as it is. Null for none.
/// </param>
/// <param name="If">What goes in instead of the group when it is not empty; null for the group itself.</param>
/// <param name="Else">What goes in instead of the group when it is empty; null for nothing.</param>
internal sealed record SnippetFormatGroup(int Index, string? Shorthand, string? If, string? Else) : SnippetFormatPart;
