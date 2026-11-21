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

/// <summary>An occurrence of the symbol at a position, in the text it was asked for.</summary>
internal sealed record Occurrence(TextRange Range, OccurrenceKind Kind);

/// <summary>A server's answer to <c>textDocument/documentHighlight</c>.</summary>
internal static class DocumentHighlights
{
    /// <summary>
    /// The occurrences of a result, in offsets of <paramref name="snapshot"/>; one without a kind is
    /// <see cref="OccurrenceKind.Text"/>, as the protocol says. Null for a null result, which leaves the occurrences to
    /// the word's, as in VS Code; none for an empty one.
    /// </summary>
    /// <exception cref="JsonException">The result is not a list of document highlights.</exception>
    public static IReadOnlyList<Occurrence>? Read(JsonElement result, TextSnapshot snapshot)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (result.ValueKind != JsonValueKind.Array)
            throw new JsonException("Document highlights are an array.");
        var occurrences = new List<Occurrence>();
        foreach (JsonElement item in result.EnumerateArray())
        {
            LspRange range =
                (
                    item.ValueKind == JsonValueKind.Object && item.TryGetProperty("range", out JsonElement value)
                        ? value.Deserialize(LspJsonContext.Default.LspRange)
                        : null
                ) ?? throw new JsonException("A document highlight needs a range.");
            int start = Positions.ToOffset(snapshot, range.Start);
            int end = Math.Max(start, Positions.ToOffset(snapshot, range.End));
            OccurrenceKind kind =
                item.TryGetProperty("kind", out JsonElement number)
                && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt32(out int read)
                && read is (int)OccurrenceKind.Read or (int)OccurrenceKind.Write
                    ? (OccurrenceKind)read
                    : OccurrenceKind.Text;
            occurrences.Add(new Occurrence(TextRange.FromBounds(start, end), kind));
        }
        return occurrences;
    }
}
