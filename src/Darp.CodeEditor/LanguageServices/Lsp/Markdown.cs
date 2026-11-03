using System.Text;
using Darp.CodeEditor.Lsp.Protocol;

namespace Darp.CodeEditor.LanguageServices;

/// <summary>
/// Reads the Markdown that language servers send for hover and documentation into code blocks and paragraphs. Inline
/// formatting is dropped: backslash escapes, emphasis, code spans and link targets.
/// </summary>
internal static class Markdown
{
    public static IReadOnlyList<MarkupBlock> ToBlocks(MarkupContent? content) =>
        content switch
        {
            null => [],
            { Kind: MarkupKind.Markdown } => Parse(content.Value),
            _ when content.Value.Trim() is { Length: > 0 } text => [new MarkupBlock(MarkupBlockKind.Paragraph, text)],
            _ => [],
        };

    /// <summary>The content as plain text: Markdown without its formatting.</summary>
    public static string? ToPlainText(MarkupContent? content) =>
        ToBlocks(content) is { Count: > 0 } blocks ? MarkupBlock.ToPlainText(blocks) : null;

    public static IReadOnlyList<MarkupBlock> Parse(string markdown)
    {
        var blocks = new List<MarkupBlock>();
        var paragraph = new StringBuilder();
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (
                trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal)
            )
            {
                EndParagraph();
                string fence = trimmed[..3];
                string language = trimmed[3..].Trim();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++)
                    code.Append(code.Length > 0 ? "\n" : "").Append(lines[i]);
                if (code.Length > 0)
                    blocks.Add(
                        new MarkupBlock(MarkupBlockKind.Code, code.ToString(), language.Length > 0 ? language : null)
                    );
                continue;
            }
            if (trimmed.Length == 0 || IsRule(trimmed))
            {
                EndParagraph();
                continue;
            }
            if (paragraph.Length > 0)
            {
                // Markdown keeps a line break after two spaces or a backslash, and before a list item.
                if (paragraph[^1] == '\\')
                    paragraph[^1] = '\n';
                else if (paragraph[^1] != '\n')
                    paragraph.Append(IsListItem(trimmed) ? '\n' : ' ');
            }
            AppendInline(paragraph, StripHeading(trimmed));
            if (line.EndsWith("  ", StringComparison.Ordinal))
                paragraph.Append('\n');
        }
        EndParagraph();
        return blocks;

        void EndParagraph()
        {
            string text = paragraph.ToString().Trim();
            if (text.Length > 0)
                blocks.Add(new MarkupBlock(MarkupBlockKind.Paragraph, text));
            paragraph.Clear();
        }
    }

    private static bool IsAsciiPunctuation(char c) => c < 128 && (char.IsPunctuation(c) || char.IsSymbol(c));

    private static string StripHeading(string line)
    {
        int level = 0;
        while (level < line.Length && line[level] == '#')
            level++;
        return level is > 0 and <= 6 && level < line.Length && line[level] == ' ' ? line[(level + 1)..] : line;
    }

    private static bool IsRule(string line) =>
        line.Length >= 3 && (line.All(c => c == '-') || line.All(c => c == '*') || line.All(c => c == '_'));

    private static bool IsListItem(string line) =>
        line.StartsWith("- ", StringComparison.Ordinal)
        || line.StartsWith("* ", StringComparison.Ordinal)
        || (line.Length > 2 && char.IsDigit(line[0]) && line.IndexOf(". ", StringComparison.Ordinal) is > 0 and < 4);

    /// <summary>Appends a line without escapes, emphasis markers, code span backticks and link targets.</summary>
    private static void AppendInline(StringBuilder builder, string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && IsAsciiPunctuation(line[i + 1]))
            {
                builder.Append(line[++i]);
                continue;
            }
            if (c == '`')
                continue;
            if ((c == '*' || c == '_') && i + 1 < line.Length && line[i + 1] == c)
            {
                i++;
                continue;
            }
            // [text](target) keeps the text.
            if (c == ']' && i + 1 < line.Length && line[i + 1] == '(' && line.IndexOf(')', i + 2) is int close and > 0)
            {
                i = close;
                continue;
            }
            if (c == '[' && line.IndexOf("](", i + 1, StringComparison.Ordinal) > i)
                continue;
            builder.Append(c);
        }
    }
}
