using System.Text;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Completion;

/// <summary>
/// A completion item's text in the LSP snippet syntax, as far as the editor uses it: the stops <c>$1</c>, <c>${1}</c>
/// and <c>${1:text}</c>, the final stop <c>$0</c>, and the escapes <c>\$</c>, <c>\}</c> and <c>\\</c>.
/// </summary>
/// <remarks>
/// Anything else goes in as the text it stands for and creates no stop: a choice as its first option, a variable as its
/// default or nothing, a transformation as its stop's text, a nested placeholder as its text, and a repeated stop as its
/// text, without syncing. What does not parse is text. A snippet with stops gets a final stop at its end, unless it has
/// one.
/// </remarks>
internal sealed class Snippet
{
    /// <summary>The most stops of a snippet, the final one included: half again as many as a real snippet has.</summary>
    public const int MaxStops = 30;

    /// <summary>
    /// The longest snippet, as written and as it goes in: many times a real snippet's few lines. It also bounds how deep
    /// a server's text can nest, and so the parser's recursion.
    /// </summary>
    public const int MaxText = 5_000;

    /// <summary>The number of a part that belongs to no stop.</summary>
    private const int NoStop = -1;

    private readonly List<Part> _parts;

    private Snippet(List<Part> parts) => _parts = parts;

    /// <summary>Parses <paramref name="text"/>, in the snippet syntax.</summary>
    /// <returns>The snippet; null for one over the limits, which goes in as plain text.</returns>
    public static Snippet? Parse(string text)
    {
        if (text.Length > MaxText)
            return null;
        var parts = new List<Part>();
        Read(text, 0, text.Length, parts);
        Dictionary<int, string> texts = parts
            .Where(part => part.Number > 0 && part.Text.Length > 0)
            .DistinctBy(part => part.Number)
            .ToDictionary(part => part.Number, part => part.Text);
        var stops = new HashSet<int>();
        for (int i = 0; i < parts.Count; i++)
        {
            (string partText, int number, bool isStop) = parts[i];
            if (number != NoStop)
                parts[i] = new Part(texts.GetValueOrDefault(number, partText), number, isStop && stops.Add(number));
        }
        if (stops.Count > 0 && stops.Add(0))
            parts.Add(new Part("", 0, IsStop: true));
        return stops.Count > MaxStops ? null : new Snippet(parts);
    }

    /// <summary>
    /// The text that goes in, and its stops: lines after the first are indented by the line it goes in, the indentation
    /// is that of the document, and line breaks are the document's.
    /// </summary>
    /// <returns>The text and its stops; null for a text over <see cref="MaxText"/>, which goes in as plain text.</returns>
    public RenderedSnippet? Render(SnippetContext context)
    {
        var indenting = new Indenting(context.Indentation);
        int lineColumns = indenting.Width(context.LineIndentation);
        var text = new StringBuilder();
        var stops = new List<SnippetStop>();
        foreach (Part part in _parts)
        {
            int start = text.Length;
            for (int at = 0; ; )
            {
                int length = part.Text.AsSpan(at).IndexOfAny('\r', '\n');
                ReadOnlySpan<char> line = length < 0 ? part.Text.AsSpan(at) : part.Text.AsSpan(at, length);
                if (text.Length == 0 || text[^1] is '\r' or '\n')
                {
                    int whitespace = line.Length - line.TrimStart(" \t").Length;
                    int column = text.Length == 0 ? 0 : lineColumns;
                    text.Append(indenting.Whitespace(indenting.Width(line[..whitespace], column)));
                    line = line[whitespace..];
                }
                text.Append(line);
                if (text.Length > MaxText)
                    return null;
                if (length < 0)
                    break;
                at += length + (part.Text.AsSpan(at + length).StartsWith("\r\n") ? 2 : 1);
                text.Append(context.LineBreak);
            }
            if (part.IsStop)
                stops.Add(new SnippetStop(part.Number, start, text.Length));
        }
        return new RenderedSnippet(text.ToString(), stops);
    }

    /// <summary>Reads the syntax from <paramref name="start"/> to <paramref name="end"/> into parts.</summary>
    private static void Read(string text, int start, int end, List<Part> parts)
    {
        var plain = new StringBuilder();
        for (int at = start; at < end; )
        {
            if (text[at] == '\\' && at + 1 < end && text[at + 1] is '$' or '}' or '\\')
            {
                plain.Append(text[at + 1]);
                at += 2;
            }
            else if (text[at] == '$' && Element(text, at, end, out int next) is { } part)
            {
                Flush();
                parts.Add(part);
                at = next;
            }
            else
            {
                plain.Append(text[at++]);
            }
        }
        Flush();

        void Flush()
        {
            if (plain.Length == 0)
                return;
            parts.Add(new Part(plain.ToString()));
            plain.Clear();
        }
    }

    /// <summary>The element at the <c>$</c> at <paramref name="at"/>, and where it ends; null if it starts none.</summary>
    private static Part? Element(string text, int at, int end, out int next)
    {
        bool braced = at + 1 < end && text[at + 1] == '{';
        int head = at + (braced ? 2 : 1);
        next = ReadHead(text, head, end, out int number);
        if (next == head)
            return null;
        bool isStop = number != NoStop;
        if (!braced)
            return new Part("", number, isStop);
        int afterHead = next;
        int close = Close(text, afterHead, end);
        if (close < 0)
            return null;
        next = close + 1;
        return text[afterHead] switch
        {
            '}' => new Part("", number, isStop),
            ':' => new Part(TextOf(text, afterHead + 1, close), number, isStop),
            '|' when isStop => FirstOption(text, afterHead + 1, close) is { } option ? new Part(option, number) : null,
            '/' => new Part("", number),
            _ => null,
        };
    }

    /// <summary>
    /// Reads the number or the name at <paramref name="at"/>; <paramref name="number"/> is <see cref="NoStop"/> for a
    /// name.
    /// </summary>
    /// <returns>Where it ends; <paramref name="at"/> for neither.</returns>
    private static int ReadHead(string text, int at, int end, out int number)
    {
        number = NoStop;
        int i = at;
        if (i < end && char.IsAsciiDigit(text[i]))
        {
            while (i < end && char.IsAsciiDigit(text[i]))
                i++;
            return int.TryParse(text.AsSpan(at, i - at), out number) ? i : at;
        }
        while (i < end && (text[i] == '_' || char.IsAsciiLetter(text[i]) || (i > at && char.IsAsciiDigit(text[i]))))
            i++;
        return i;
    }

    /// <summary>
    /// The <c>}</c> that closes an element, past balanced braces, as in a transformation's <c>\d{3}</c> or
    /// <c>${1:/upcase}</c>; -1 for none.
    /// </summary>
    private static int Close(string text, int at, int end)
    {
        int depth = 0;
        for (int i = at; i < end; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == '{')
                depth++;
            else if (text[i] == '}' && depth-- == 0)
                return i;
        }
        return -1;
    }

    /// <summary>The text that the syntax from <paramref name="start"/> to <paramref name="end"/> stands for.</summary>
    private static string TextOf(string text, int start, int end)
    {
        var parts = new List<Part>();
        Read(text, start, end, parts);
        return string.Concat(parts.Select(part => part.Text));
    }

    /// <summary>The first option of a choice that ends in <c>|</c>; null for an empty one or another ending.</summary>
    private static string? FirstOption(string text, int start, int end)
    {
        if (text[end - 1] != '|')
            return null;
        var option = new StringBuilder();
        for (int at = start; at < end && text[at] is not (',' or '|'); at++)
        {
            if (text[at] == '\\' && at + 1 < end && text[at + 1] is ',' or '|' or '\\')
                at++;
            option.Append(text[at]);
        }
        return option.Length > 0 ? option.ToString() : null;
    }

    /// <summary>A run of the snippet's text.</summary>
    /// <param name="Text">The text.</param>
    /// <param name="Number">The number of the stop it belongs to, or <see cref="NoStop"/>.</param>
    /// <param name="IsStop">Whether it is that stop, rather than text that repeats it.</param>
    private readonly record struct Part(string Text, int Number = NoStop, bool IsStop = false);
}

/// <summary>Where a snippet goes in, for <see cref="Snippet.Render"/>.</summary>
/// <param name="LineIndentation">The whitespace at the start of the line it goes in, up to where it goes in.</param>
/// <param name="Indentation">The document's indentation.</param>
/// <param name="LineBreak">The document's line break.</param>
internal sealed record SnippetContext(string LineIndentation, Indentation Indentation, string LineBreak);

/// <summary>A snippet's text and its stops, ready to go in.</summary>
/// <param name="Text">The text.</param>
/// <param name="Stops">The stops, in the order of the text.</param>
internal sealed record RenderedSnippet(string Text, IReadOnlyList<SnippetStop> Stops);

/// <summary>A stop of a rendered snippet.</summary>
/// <param name="Index">Its number: 1 and up, and 0 for the final stop.</param>
/// <param name="Start">Where its text starts in the snippet's text.</param>
/// <param name="End">Where its text ends.</param>
internal readonly record struct SnippetStop(int Index, int Start, int End);
