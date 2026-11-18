using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>How an occurrence of a symbol uses it, as the protocol's <c>DocumentHighlightKind</c>.</summary>
internal enum OccurrenceKind
{
    /// <summary>A textual occurrence, as of a word without a language server.</summary>
    Text = 1,

    /// <summary>A read of the symbol, such as of a variable.</summary>
    Read = 2,

    /// <summary>A write of the symbol, such as to a variable.</summary>
    Write = 3,
}

/// <summary>An occurrence of the symbol at a position, in the snapshot it was asked for.</summary>
internal sealed record Occurrence(TextRange Range, OccurrenceKind Kind);

/// <summary>A server's answer to <c>textDocument/documentHighlight</c>.</summary>
internal static class DocumentHighlights
{
    /// <summary>
    /// Reads a result: the occurrences, each with a range and maybe a kind, <see cref="OccurrenceKind.Text"/> without
    /// one, as VS Code takes them. Null for a null result, which leaves the occurrences to the words of the text, as in
    /// VS Code; an empty list for none.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="snapshot">The text that the request's position was in.</param>
    /// <exception cref="JsonException">The result is not a list of highlights.</exception>
    public static IReadOnlyList<Occurrence>? Read(JsonElement result, TextSnapshot snapshot)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (result.ValueKind != JsonValueKind.Array)
            throw new JsonException("Document highlights are an array.");
        var occurrences = new List<Occurrence>();
        foreach (JsonElement item in result.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("range", out JsonElement value))
                throw new JsonException("A document highlight needs a range.");
            LspRange range =
                value.Deserialize(LspJsonContext.Default.LspRange) ?? throw new JsonException("The range is null.");
            int start = Positions.ToOffset(snapshot, range.Start);
            int end = Math.Max(start, Positions.ToOffset(snapshot, range.End));
            OccurrenceKind kind =
                item.TryGetProperty("kind", out JsonElement number)
                && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt32(out int k)
                && k is 2 or 3
                    ? (OccurrenceKind)k
                    : OccurrenceKind.Text;
            occurrences.Add(new Occurrence(TextRange.FromBounds(start, end), kind));
        }
        return occurrences;
    }
}
