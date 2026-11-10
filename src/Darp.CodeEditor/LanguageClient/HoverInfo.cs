using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>A server's answer to <c>textDocument/hover</c>.</summary>
/// <param name="Contents">Its parts, in order, each Markdown or plain text.</param>
/// <param name="Range">The text it is about, in the snapshot it was asked for; null when the server gave none.</param>
internal sealed record HoverInfo(IReadOnlyList<HoverText> Contents, TextRange? Range)
{
    /// <summary>
    /// Reads a hover result: <c>MarkupContent</c>, a <c>MarkedString</c> or an array of them; a code block of a
    /// <c>MarkedString</c> becomes Markdown. Null for a null result, or one without contents.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="snapshot">The text that the request's position was in.</param>
    /// <exception cref="JsonException">The result is not a hover.</exception>
    public static HoverInfo? Read(JsonElement result, TextSnapshot snapshot)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out JsonElement contents))
            throw new JsonException("A hover needs contents.");
        var texts = new List<HoverText>();
        if (contents.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in contents.EnumerateArray())
                texts.Add(MarkedString(item));
        }
        else if (contents.ValueKind == JsonValueKind.Object && contents.TryGetProperty("kind", out JsonElement kind))
        {
            texts.Add(
                new HoverText(
                    String(contents, "value"),
                    kind.ValueKind == JsonValueKind.String && kind.ValueEquals("markdown")
                )
            );
        }
        else
        {
            texts.Add(MarkedString(contents));
        }
        texts.RemoveAll(text => string.IsNullOrWhiteSpace(text.Text));
        if (texts.Count == 0)
            return null;
        TextRange? range = null;
        if (result.TryGetProperty("range", out JsonElement value) && value.ValueKind == JsonValueKind.Object)
        {
            LspRange lspRange =
                value.Deserialize(LspJsonContext.Default.LspRange) ?? throw new JsonException("The range is null.");
            int start = Positions.ToOffset(snapshot, lspRange.Start);
            range = TextRange.FromBounds(start, Math.Max(start, Positions.ToOffset(snapshot, lspRange.End)));
        }
        return new HoverInfo(texts, range);
    }

    /// <summary>A <c>MarkedString</c>: Markdown, or a code block in a language.</summary>
    private static HoverText MarkedString(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
            return new HoverText(item.GetString() ?? "", IsMarkdown: true);
        string language = String(item, "language");
        string code = String(item, "value");
        // A fence longer than any run of backticks in the code.
        string fence = new('`', Math.Max(3, LongestRun(code, '`') + 1));
        return new HoverText($"{fence}{language}\n{code}\n{fence}", IsMarkdown: true);
    }

    private static string String(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new JsonException($"A hover's '{name}' must be a string.");

    private static int LongestRun(string text, char c)
    {
        int longest = 0;
        int run = 0;
        foreach (char x in text)
        {
            run = x == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }
}

/// <summary>A part of a hover's contents.</summary>
/// <param name="Text">Its text.</param>
/// <param name="IsMarkdown">Whether it is Markdown, rather than plain text.</param>
internal readonly record struct HoverText(string Text, bool IsMarkdown);
