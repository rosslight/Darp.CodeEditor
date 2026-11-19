using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>A server's answer to <c>textDocument/selectionRange</c>, for one position.</summary>
internal static class SelectionRanges
{
    /// <summary>
    /// The ranges of the first selection range and its parents, from the innermost out, in offsets of
    /// <paramref name="snapshot"/>; none for a null answer.
    /// </summary>
    /// <exception cref="JsonException">The answer is not a list of selection ranges.</exception>
    public static IReadOnlyList<TextRange> Read(JsonElement result, TextSnapshot snapshot)
    {
        if (result.ValueKind == JsonValueKind.Null)
            return [];
        if (result.ValueKind != JsonValueKind.Array)
            throw new JsonException("Selection ranges are a list.");
        var ranges = new List<TextRange>();
        if (result.GetArrayLength() == 0)
            return ranges;
        // Parents nest, so a server's chain is bounded by the text; a cycle can't come from JSON.
        for (JsonElement? current = result[0]; current is { ValueKind: JsonValueKind.Object } range; )
        {
            LspRange lspRange =
                (
                    range.TryGetProperty("range", out JsonElement value)
                        ? value.Deserialize(LspJsonContext.Default.LspRange)
                        : null
                ) ?? throw new JsonException("A selection range has no range.");
            int start = Positions.ToOffset(snapshot, lspRange.Start);
            ranges.Add(TextRange.FromBounds(start, Math.Max(start, Positions.ToOffset(snapshot, lspRange.End))));
            current = range.TryGetProperty("parent", out JsonElement parent) ? parent : null;
        }
        return ranges;
    }
}
