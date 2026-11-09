using System.Globalization;
using System.Text;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// Reads the Markdown that language servers write in hovers: paragraphs with emphasis, inline code, links and line
/// breaks; lists, also nested; fenced code blocks; headings, shown in bold; and lines between parts. Anything else stays
/// as its text, as do links to anything but web pages.
/// </summary>
/// <remarks>
/// It follows CommonMark where hovers need it, emphasis included, and keeps it simple elsewhere: a line of dashes is
/// always a line between parts, never a heading's underline, and lists number their items as they are written.
/// </remarks>
internal static class Markdown
{
    private const string Bullets = "-*+";

    /// <summary>
    /// Reads <paramref name="markdown"/> into blocks, with <paramref name="code"/> making its code blocks from their
    /// lines. Reading stops between lines and paragraphs once <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static IReadOnlyList<HoverBlock> Parse(
        string markdown,
        Func<IReadOnlyList<string>, HoverCode> code,
        CancellationToken cancellationToken
    )
    {
        var reader = new BlockReader(code, cancellationToken);
        // Lines end at \r\n, \n or \r, as everywhere in the library and in the hover's budget.
        string[] lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = lines[i].Replace('\t', ' ');
            string trimmed = line.TrimStart(' ');
            int indent = line.Length - trimmed.Length;
            if (Fence(trimmed) is { } fence)
            {
                var codeLines = new List<string>();
                for (i++; i < lines.Length && !IsClosingFence(lines[i], fence.Character, fence.Length); i++)
                    codeLines.Add(Unindent(lines[i], indent));
                reader.Code(codeLines);
            }
            else if (trimmed.Length == 0)
                reader.Blank();
            else if (IsRule(trimmed))
                reader.Add(new HoverRule());
            else if (Heading(trimmed) is { } heading)
                reader.Heading(heading);
            else if (ListItem(trimmed) is { } item)
                reader.Item(indent, item.Marker, item.Width, line);
            else
                reader.Line(indent, line);
        }
        return reader.End();
    }

    /// <summary>The inline text of a paragraph: emphasis, code spans, links, escapes and line breaks.</summary>
    public static IReadOnlyList<HoverSpan> Inline(string text)
    {
        var spans = new List<HoverSpan>();
        AppendInline(text, null, spans);
        return spans;
    }

    /// <summary>An opening fence of three or more backticks or tildes, with its language; null for none.</summary>
    private static (char Character, int Length)? Fence(string line)
    {
        if (line.Length < 3 || line[0] is not ('`' or '~'))
            return null;
        int length = Run(line, 0, line[0]);
        // A backtick fence's language has no backticks: it would be inline code.
        return length >= 3 && (line[0] == '~' || !line.AsSpan(length).Contains('`')) ? (line[0], length) : null;
    }

    private static bool IsClosingFence(string line, char character, int length)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= length && Run(trimmed, 0, character) == trimmed.Length;
    }

    /// <summary>A code block's line, without the indentation of its fence.</summary>
    private static string Unindent(string line, int indent)
    {
        int remove = 0;
        while (remove < indent && remove < line.Length && line[remove] == ' ')
            remove++;
        return line[remove..];
    }

    /// <summary>Three or more dashes, stars or underscores, maybe with spaces between.</summary>
    private static bool IsRule(string line)
    {
        char c = line[0];
        if (c is not ('-' or '*' or '_'))
            return false;
        int count = 0;
        foreach (char x in line)
        {
            if (x == c)
                count++;
            else if (x != ' ')
                return false;
        }
        return count >= 3;
    }

    /// <summary>A heading's text, after one to six hashes and a space; null for none.</summary>
    private static string? Heading(string line)
    {
        int level = Run(line, 0, '#');
        if (level is < 1 or > 6 || (level < line.Length && line[level] != ' '))
            return null;
        return line[level..].Trim().TrimEnd('#').TrimEnd();
    }

    /// <summary>A list item's marker, such as <c>-</c> or <c>3.</c>, and its width with the space after it.</summary>
    private static (string Marker, int Width)? ListItem(string line)
    {
        if (Bullets.Contains(line[0]) && line.Length > 1 && line[1] == ' ')
            return ("•", 2);
        int digits = 0;
        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits]))
            digits++;
        if (digits > 0 && digits + 1 < line.Length && line[digits] is '.' or ')' && line[digits + 1] == ' ')
            return ($"{int.Parse(line.AsSpan(0, digits), CultureInfo.InvariantCulture)}.", digits + 2);
        return null;
    }

    private static int Run(string text, int start, char c)
    {
        int end = start;
        while (end < text.Length && text[end] == c)
            end++;
        return end - start;
    }

    /// <summary>
    /// Appends the spans of <paramref name="text"/>, all with <paramref name="link"/>. Code spans, escapes, links and
    /// autolinks are read first, and runs of <c>*</c> and <c>_</c> kept as delimiters; then emphasis pairs them, as
    /// CommonMark does, in time that grows with the square of the delimiters at most. Only a link's text is read again, by
    /// itself, and it holds no links.
    /// </summary>
    private static void AppendInline(string text, Uri? link, List<HoverSpan> spans)
    {
        var parts = new List<Part>();
        var plain = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
            }
            else if (c == '`')
            {
                int run = Run(text, i, '`');
                int close = ClosingBackticks(text, i + run, run);
                if (close < 0)
                {
                    plain.Append(text, i, run);
                    i += run;
                    continue;
                }
                Flush();
                parts.Add(new Part(CodeSpan(text[(i + run)..close]), SpanStyle.Code, link));
                i = close + run;
            }
            else if (c is '*' or '_')
            {
                Flush();
                int run = Run(text, i, c);
                parts.Add(Delimiter(text, i, run));
                i += run;
            }
            else if (c == '[' && link is null && Link(text, i) is { } found)
            {
                Flush();
                var label = new List<HoverSpan>();
                AppendInline(found.Label, WebPage(found.Target), label);
                parts.AddRange(label.Select(span => new Part(span.Text, span.Style, span.Link)));
                i = found.End;
            }
            else if (c == '<' && link is null && Autolink(text, i) is { } autolink)
            {
                Flush();
                parts.Add(new Part(autolink.Uri.OriginalString, SpanStyle.None, autolink.Uri));
                i = autolink.End;
            }
            else
            {
                plain.Append(c);
                i++;
            }
        }
        Flush();
        Emphasize(parts);
        foreach (Part part in parts)
        {
            string partText = part.Delimiter is { } delimiter ? new string(delimiter, part.Count) : part.Text;
            if (partText.Length == 0)
                continue;
            // Text of one style and link is one span.
            if (spans.Count > 0 && spans[^1] is var last && last.Style == part.Style && last.Link == part.Link)
                spans[^1] = last with { Text = last.Text + partText };
            else
                spans.Add(new HoverSpan(partText, part.Style, part.Link));
        }

        void Flush()
        {
            if (plain.Length > 0)
                parts.Add(new Part(plain.ToString(), SpanStyle.None, link));
            plain.Clear();
        }

        Part Delimiter(string text, int start, int run)
        {
            char before = start > 0 ? text[start - 1] : ' ';
            char after = start + run < text.Length ? text[start + run] : ' ';
            // As CommonMark: a run that text follows can open, one that text precedes can close; punctuation counts as
            // text only next to space or punctuation.
            bool leftFlanking =
                !char.IsWhiteSpace(after)
                && (!IsPunctuation(after) || char.IsWhiteSpace(before) || IsPunctuation(before));
            bool rightFlanking =
                !char.IsWhiteSpace(before)
                && (!IsPunctuation(before) || char.IsWhiteSpace(after) || IsPunctuation(after));
            // An underscore inside a word, as in snake_case, is text.
            bool underscore = text[start] == '_';
            return new Part("", SpanStyle.None, link)
            {
                Delimiter = text[start],
                Count = run,
                Length = run,
                CanOpen = leftFlanking && (!underscore || !rightFlanking || IsPunctuation(before)),
                CanClose = rightFlanking && (!underscore || !leftFlanking || IsPunctuation(after)),
            };
        }
    }

    /// <summary>
    /// Pairs delimiter runs into emphasis, as CommonMark does: each run that can close takes the nearest run of the same
    /// character before it that can open, two delimiters of each for bold when both have them, one for italic, and
    /// styles what lies between. What a run does not use stays as its text.
    /// </summary>
    private static void Emphasize(List<Part> parts)
    {
        for (int close = 0; close < parts.Count; close++)
        {
            Part closer = parts[close];
            if (closer.Delimiter is not { } c || !closer.CanClose)
                continue;
            while (closer.Count > 0 && Opener(parts, close, closer) is { } open)
            {
                Part opener = parts[open];
                int used = opener.Count >= 2 && closer.Count >= 2 ? 2 : 1;
                SpanStyle style = used == 2 ? SpanStyle.Bold : SpanStyle.Italic;
                for (int i = open + 1; i < close; i++)
                {
                    parts[i].Style |= style;
                    // Runs between the two can no longer pair.
                    parts[i].CanOpen = parts[i].CanClose = false;
                }
                opener.Count -= used;
                closer.Count -= used;
            }
        }
    }

    /// <summary>The nearest run before <paramref name="close"/> that can open emphasis that it closes; null for none.</summary>
    private static int? Opener(List<Part> parts, int close, Part closer)
    {
        for (int open = close - 1; open >= 0; open--)
        {
            Part opener = parts[open];
            if (opener.Delimiter != closer.Delimiter || !opener.CanOpen || opener.Count == 0)
                continue;
            // CommonMark's rule of three, for runs that can both open and close.
            bool both = (opener.CanOpen && opener.CanClose) || (closer.CanOpen && closer.CanClose);
            if (both && (opener.Length + closer.Length) % 3 == 0 && (opener.Length % 3 != 0 || closer.Length % 3 != 0))
            {
                continue;
            }
            return open;
        }
        return null;
    }

    private static bool IsPunctuation(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    private static bool IsAsciiPunctuation(char c) =>
        c is (>= '!' and <= '/') or (>= ':' and <= '@') or (>= '[' and <= '`') or (>= '{' and <= '~');

    /// <summary>Where a run of exactly <paramref name="length"/> backticks starts, from <paramref name="from"/>; -1 for none.</summary>
    private static int ClosingBackticks(string text, int from, int length)
    {
        for (int i = from; i < text.Length; )
        {
            int run = Run(text, i, '`');
            if (run == length)
                return i;
            i += Math.Max(run, 1);
        }
        return -1;
    }

    /// <summary>A code span's text: line breaks as spaces, and one space trimmed from both ends when both have one.</summary>
    private static string CodeSpan(string text)
    {
        text = text.Replace('\n', ' ');
        return text.Length >= 2 && text[0] == ' ' && text[^1] == ' ' && text.Trim().Length > 0 ? text[1..^1] : text;
    }

    /// <summary>A link, <c>[label](target "title")</c>, that starts at <paramref name="start"/>; null for none.</summary>
    private static (string Label, string Target, int End)? Link(string text, int start)
    {
        int depth = 0;
        int i = start;
        for (; i < text.Length; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == '[')
                depth++;
            else if (text[i] == ']' && --depth == 0)
                break;
        }
        if (i + 1 >= text.Length || text[i + 1] != '(')
            return null;
        int labelEnd = i;
        depth = 0;
        for (i += 1; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')' && --depth == 0)
                break;
        }
        if (i >= text.Length)
            return null;
        string destination = text[(labelEnd + 2)..i].Trim();
        string target =
            destination.StartsWith('<') && destination.IndexOf('>') is var close and > 0
                ? destination[1..close]
                : destination.Split(' ', 2)[0];
        return (text[(start + 1)..labelEnd], target, i + 1);
    }

    /// <summary>An autolink, <c>&lt;https://…&gt;</c>, to a web page; null for none.</summary>
    private static (Uri Uri, int End)? Autolink(string text, int start)
    {
        int close = text.IndexOf('>', start + 1);
        if (close < 0 || text.AsSpan(start + 1, close - start - 1).ContainsAny(' ', '<'))
            return null;
        return WebPage(text[(start + 1)..close]) is { } uri ? (uri, close + 1) : null;
    }

    /// <summary>The URI of a web page, http or https; null for anything else, which opens no link.</summary>
    private static Uri? WebPage(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>A part of a paragraph's text: text with its style and link, or a run of emphasis delimiters.</summary>
    private sealed class Part(string text, SpanStyle style, Uri? link)
    {
        public string Text { get; } = text;

        public SpanStyle Style { get; set; } = style;

        public Uri? Link { get; } = link;

        /// <summary>The run's character, <c>*</c> or <c>_</c>; null for text.</summary>
        public char? Delimiter { get; init; }

        /// <summary>How many of the run's delimiters are not used by emphasis yet.</summary>
        public int Count { get; set; }

        /// <summary>How many delimiters the run had.</summary>
        public int Length { get; init; }

        public bool CanOpen { get; set; }

        public bool CanClose { get; set; }
    }

    /// <summary>
    /// Collects blocks: the paragraph being read, and the list items it is in. A line that follows a paragraph's line
    /// continues it; a blank line ends it, and a line after it that is indented as an item's text is a paragraph of
    /// that item.
    /// </summary>
    private sealed class BlockReader(Func<IReadOnlyList<string>, HoverCode> code, CancellationToken cancellationToken)
    {
        private readonly Func<IReadOnlyList<string>, HoverCode> _code = code;
        private readonly CancellationToken _cancellationToken = cancellationToken;
        private readonly List<HoverBlock> _blocks = [];

        /// <summary>The column where the text of each open list item starts, the outermost first.</summary>
        private readonly List<int> _items = [];

        private readonly StringBuilder _paragraph = new();
        private string? _marker;
        private int _level;

        /// <summary>Whether the last line asked for a line break before the next: two spaces or a backslash at its end.</summary>
        private bool _hardBreak;

        public void Add(HoverBlock block)
        {
            EndParagraph();
            _items.Clear();
            _blocks.Add(block);
        }

        public void Code(IReadOnlyList<string> lines)
        {
            EndParagraph();
            _blocks.Add(_code(lines));
        }

        public void Blank() => EndParagraph();

        public void Heading(string text)
        {
            Add(
                new HoverParagraph([.. Inline(text).Select(span => span with { Style = span.Style | SpanStyle.Bold })])
            );
        }

        /// <summary>A list item's first line, whose text starts after <paramref name="width"/> columns of its marker.</summary>
        public void Item(int indent, string marker, int width, string line)
        {
            EndParagraph();
            while (_items.Count > 0 && indent < _items[^1])
                _items.RemoveAt(_items.Count - 1);
            _level = _items.Count + 1;
            _items.Add(indent + width);
            _marker = marker;
            Append(line[(indent + width)..]);
        }

        /// <summary>A line of a paragraph: the first, or one that continues it.</summary>
        public void Line(int indent, string line)
        {
            if (_paragraph.Length == 0)
            {
                // A paragraph of the innermost item it is indented for, or out of the lists.
                while (_items.Count > 0 && indent < _items[^1])
                    _items.RemoveAt(_items.Count - 1);
                _level = _items.Count;
            }
            Append(line);
        }

        public IReadOnlyList<HoverBlock> End()
        {
            EndParagraph();
            return _blocks;
        }

        /// <summary>
        /// Appends a line, after a space or the line break that the last line asked for. Two spaces or a backslash at its
        /// end ask for one before the next.
        /// </summary>
        private void Append(string line)
        {
            if (_paragraph.Length > 0)
                _paragraph.Append(_hardBreak ? '\n' : ' ');
            string text = line.Trim();
            _hardBreak = line.EndsWith("  ", StringComparison.Ordinal) || text.EndsWith('\\');
            _paragraph.Append(text.EndsWith('\\') ? text[..^1] : text);
        }

        private void EndParagraph()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_paragraph.Length > 0)
                _blocks.Add(new HoverParagraph(Inline(_paragraph.ToString()), _level, _marker));
            _paragraph.Clear();
            _marker = null;
            _hardBreak = false;
        }
    }
}
