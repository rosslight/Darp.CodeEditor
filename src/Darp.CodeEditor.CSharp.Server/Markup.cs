using System.Collections.Immutable;
using System.Text;
using EmmyLua.LanguageServer.Framework.Protocol.Model.Markup;
using Microsoft.CodeAnalysis;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Builds hover and documentation content: C# signatures as code, documentation as text.</summary>
internal static class Markup
{
    public static MarkupContent Create(string? code, IEnumerable<string> paragraphs, bool markdown)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(code))
            builder.Append(markdown ? $"```csharp\n{code}\n```" : code);
        foreach (string paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
                continue;
            if (builder.Length > 0)
                builder.Append("\n\n");
            builder.Append(markdown ? Escape(paragraph) : paragraph);
        }
        return new MarkupContent
        {
            Kind = markdown ? MarkupKind.Markdown : MarkupKind.PlainText,
            Value = builder.ToString(),
        };
    }

    /// <summary>Joins tagged text, with line breaks as newlines.</summary>
    public static string ToText(IEnumerable<TaggedText> parts)
    {
        var builder = new StringBuilder();
        foreach (TaggedText part in parts)
            builder.Append(part.Tag == TextTags.LineBreak ? "\n" : part.Text);
        return builder.ToString().Trim();
    }

    /// <summary>Splits a description into its first line, the signature, and the rest, the documentation.</summary>
    public static MarkupContent FromDescription(ImmutableArray<TaggedText> parts, bool markdown)
    {
        int lineBreak = parts.IndexOf(parts.FirstOrDefault(p => p.Tag == TextTags.LineBreak));
        return lineBreak < 0
            ? Create(ToText(parts), [], markdown)
            : Create(ToText(parts.Take(lineBreak)), [ToText(parts.Skip(lineBreak + 1))], markdown);
    }

    /// <summary>Escapes characters that Markdown would interpret, so documentation shows as written.</summary>
    private static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            if (c is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '#' or '|' or '~')
                builder.Append('\\');
            builder.Append(c);
        }
        // Markdown needs two trailing spaces or a blank line for a line break.
        return builder.Replace("\n", "  \n").ToString();
    }
}
