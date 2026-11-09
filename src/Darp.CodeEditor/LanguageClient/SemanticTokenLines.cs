using System.Text.Json;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// A server's semantic tokens for a run of lines, decoded from the protocol's relative format, each line by itself.
/// </summary>
/// <param name="Legend">What the tokens' types and modifiers name.</param>
/// <param name="FirstLine">The first line covered, from 0.</param>
/// <param name="Lines">
/// The tokens of each line covered, from <paramref name="FirstLine"/> on, in groups of <see cref="Stride"/>: the column
/// where it starts, from 0, its length, its type and its modifiers, as the legend's index and bits. Lines without tokens
/// have an empty array.
/// </param>
internal sealed record SemanticTokenLines(SemanticTokensLegend Legend, int FirstLine, int[][] Lines)
{
    /// <summary>The numbers per token.</summary>
    public const int Stride = 4;

    /// <summary>
    /// Reads a <c>SemanticTokens</c> result for the lines from <paramref name="firstLine"/> to
    /// <paramref name="lastLine"/>, both from 0. Tokens outside them, of a type the legend does not name, or empty, are
    /// left out. A null result counts as no tokens.
    /// </summary>
    /// <exception cref="JsonException">The result is not semantic tokens.</exception>
    public static SemanticTokenLines Read(JsonElement result, SemanticTokensLegend legend, int firstLine, int lastLine)
    {
        var lines = new List<int>[lastLine - firstLine + 1];
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (!result.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                throw new JsonException("Semantic tokens need their data.");
            if (data.GetArrayLength() % 5 != 0)
                throw new JsonException("Semantic tokens' data come in groups of five.");
            long line = 0;
            long column = 0;
            JsonElement.ArrayEnumerator numbers = data.EnumerateArray();
            while (numbers.MoveNext())
            {
                uint deltaLine = Number(numbers.Current);
                uint deltaColumn = Next(ref numbers);
                uint length = Next(ref numbers);
                uint type = Next(ref numbers);
                uint modifiers = Next(ref numbers);
                // In long: deltas that a server sends may add up past an int.
                line += deltaLine;
                column = deltaLine == 0 ? column + deltaColumn : deltaColumn;
                // Tokens come in the order of the text.
                if (line > lastLine)
                    break;
                if (line < firstLine || type >= legend.TokenTypes.Length || length == 0 || column > int.MaxValue)
                    continue;
                List<int> tokens = lines[line - firstLine] ??= [];
                tokens.AddRange([(int)column, (int)Math.Min(length, int.MaxValue), (int)type, (int)modifiers]);
            }
        }
        else if (result.ValueKind != JsonValueKind.Null)
        {
            throw new JsonException("Semantic tokens must be an object.");
        }
        return new SemanticTokenLines(legend, firstLine, [.. lines.Select(tokens => tokens?.ToArray() ?? [])]);
    }

    private static uint Next(ref JsonElement.ArrayEnumerator numbers) =>
        numbers.MoveNext() ? Number(numbers.Current) : throw new JsonException("Semantic tokens end early.");

    private static uint Number(JsonElement number) =>
        number.ValueKind == JsonValueKind.Number && number.TryGetUInt32(out uint value)
            ? value
            : throw new JsonException("Semantic tokens' data are unsigned integers.");
}
