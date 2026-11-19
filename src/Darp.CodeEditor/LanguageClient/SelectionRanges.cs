using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>A server's answer to <c>textDocument/selectionRange</c>, for one position.</summary>
internal static class SelectionRanges
{
    /// <summary>
    /// The range of the first selection range and those of its parents, from the innermost out, in offsets of
    /// <paramref name="snapshot"/>; none for a null answer.
    /// </summary>
    /// <exception cref="JsonException">The answer is not a list of selection ranges.</exception>
    public static IReadOnlyList<TextRange> Read(JsonElement result, TextSnapshot snapshot)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];
        if (result.ValueKind != JsonValueKind.Array)
            throw new JsonException("Selection ranges are an array.");
        var ranges = new List<TextRange>();
        // JSON nests the parents, so the chain ends; a server's ranges that don't nest are left out where they're used.
        for (
            JsonElement? current = result.GetArrayLength() > 0 ? result[0] : null;
            current is { ValueKind: JsonValueKind.Object } range;
            current = range.TryGetProperty("parent", out JsonElement parent) ? parent : null
        )
        {
            LspRange lspRange =
                (
                    range.TryGetProperty("range", out JsonElement value)
                        ? value.Deserialize(LspJsonContext.Default.LspRange)
                        : null
                ) ?? throw new JsonException("A selection range needs a range.");
            int start = Positions.ToOffset(snapshot, lspRange.Start);
            ranges.Add(TextRange.FromBounds(start, Math.Max(start, Positions.ToOffset(snapshot, lspRange.End))));
        }
        return ranges;
    }
}
