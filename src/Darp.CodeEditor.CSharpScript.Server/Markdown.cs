using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>Documentation as the Markdown that hover, completion and signature help send.</summary>
internal static class Markdown
{
    // Roslyn's tags around a list item and a code block, which it keeps internal.
    private const string ContainerStart = nameof(ContainerStart);
    private const string ContainerEnd = nameof(ContainerEnd);
    private const string CodeBlockStart = nameof(CodeBlockStart);
    private const string CodeBlockEnd = nameof(CodeBlockEnd);

    /// <summary>
    /// The style of a part, which marks the text of <c>&lt;c&gt;</c> as code, and which Roslyn keeps internal: without
    /// it, as in a version that renames it, that text stays text.
    /// </summary>
    private static readonly PropertyInfo? s_style = typeof(TaggedText).GetProperty(
        "Style",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    /// <summary>The flag of Roslyn's <c>TaggedTextStyle.Code</c>.</summary>
    private const int CodeStyle = 1 << 3;

    /// <summary><paramref name="text"/> as Markdown that reads as the same text.</summary>
    public static string Escape(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text)
            AppendEscaped(escaped, c);
        return escaped.ToString();
    }

    /// <summary>Appends <paramref name="c"/> as Markdown that reads as it.</summary>
    public static void AppendEscaped(StringBuilder markdown, char c)
    {
        if (c is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '#')
            markdown.Append('\\');
        markdown.Append(c);
    }

    /// <summary><paramref name="text"/> as a code span, in the code font.</summary>
    public static string Code(string text)
    {
        // Fenced by more backticks than any run of them inside, and spaced from a backtick at either end.
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', longest + 1);
        string space = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return fence + space + text + space + fence;
    }

    /// <summary>
    /// Roslyn's parts of documentation as Markdown: what names code, as a reference, a keyword or a parameter's name, as
    /// a code span; a line that only labels what follows, as <c>Returns:</c>, in bold before it; and each line on a line
    /// of its own, without its indentation.
    /// </summary>
    public static string Of(IReadOnlyList<TaggedText> parts)
    {
        var markdown = new StringBuilder();
        var code = new StringBuilder();
        // Spaces after the code so far: they join it only when more code follows.
        string spaces = "";
        bool lineStart = true;
        bool labelled = false;
        for (int i = 0; i < parts.Count; i++)
        {
            TaggedText part = parts[i];
            switch (part.Tag)
            {
                case TextTags.LineBreak:
                    EndCode();
                    // A label goes before what it labels; a line break between paragraphs needs no spaces.
                    markdown.Append(
                        labelled ? " "
                        : IsBreak(parts, i - 1) || IsBreak(parts, i + 1) ? "\n"
                        : "  \n"
                    );
                    lineStart = true;
                    labelled = false;
                    continue;
                case TextTags.Space:
                    if (code.Length > 0)
                        spaces += part.Text;
                    else if (!lineStart)
                        markdown.Append(part.Text);
                    continue;
                case TextTags.Text when !IsMarkedAsCode(part):
                case ContainerStart or ContainerEnd or CodeBlockStart or CodeBlockEnd:
                    EndCode();
                    string text = lineStart ? part.Text.TrimStart() : part.Text;
                    if (text.Length == 0)
                        continue;
                    labelled = lineStart && IsLabel(parts, i);
                    markdown.Append(labelled ? $"**{Escape(text.TrimEnd())}**" : Escape(text));
                    break;
                default:
                    code.Append(spaces).Append(part.Text);
                    spaces = "";
                    break;
            }
            lineStart = false;
        }
        EndCode();
        return markdown.ToString().TrimEnd();

        void EndCode()
        {
            if (code.Length > 0)
                markdown.Append(Code(code.ToString())).Append(spaces);
            code.Clear();
            spaces = "";
        }
    }

    private static bool IsMarkedAsCode(TaggedText part) =>
        s_style?.GetValue(part) is Enum style && (Convert.ToInt32(style, null) & CodeStyle) != 0;

    /// <summary>Whether part <paramref name="i"/> is a line break, as the parts' ends are.</summary>
    private static bool IsBreak(IReadOnlyList<TaggedText> parts, int i) =>
        i < 0 || i >= parts.Count || parts[i].Tag == TextTags.LineBreak;

    /// <summary>
    /// Whether part <paramref name="i"/> is a line of its own of at most two words that ends with a colon, as Roslyn
    /// labels a section: <c>Returns:</c>, <c>Exceptions:</c> or <c>Variables captured:</c>.
    /// </summary>
    private static bool IsLabel(IReadOnlyList<TaggedText> parts, int i) =>
        IsBreak(parts, i + 1)
        && parts[i].Text.Trim() is { Length: > 1 } text
        && text.EndsWith(':')
        && text.Count(c => c == ' ') <= 1;
}
