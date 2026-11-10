using System.Text.Json;
using System.Text.Json.Nodes;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>A server's answer to <c>textDocument/signatureHelp</c>: the signatures of the call around the caret.</summary>
/// <param name="Signatures">The signatures, at least one.</param>
/// <param name="ActiveSignature">The signature that shows: the server's choice, or the user's.</param>
/// <param name="Source">The answer as the server sent it, for asking again.</param>
internal sealed record SignatureHelpInfo(
    IReadOnlyList<SignatureInfo> Signatures,
    int ActiveSignature,
    JsonElement Source
)
{
    /// <summary>
    /// Reads a signature help result; null for a null result or one without signatures. An active signature outside
    /// the signatures is the first one.
    /// </summary>
    /// <exception cref="JsonException">The result is not a signature help.</exception>
    public static SignatureHelpInfo? Read(JsonElement result)
    {
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (
            result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("signatures", out JsonElement signatures)
            || signatures.ValueKind != JsonValueKind.Array
        )
        {
            throw new JsonException("A signature help needs signatures.");
        }
        int? activeParameter = ServerJson.Number(result, "activeParameter");
        var read = new List<SignatureInfo>();
        foreach (JsonElement signature in signatures.EnumerateArray())
            read.Add(SignatureInfo.Read(signature, activeParameter));
        if (read.Count == 0)
            return null;
        int active = ServerJson.Number(result, "activeSignature") is int a and >= 0 && a < read.Count ? a : 0;
        return new SignatureHelpInfo(read, active, result);
    }

    /// <summary>The answer as the server sent it, with the signature that shows as its active one.</summary>
    public JsonNode ToContext()
    {
        JsonObject help = JsonObject.Create(Source) ?? throw new InvalidOperationException("The answer is an object.");
        help["activeSignature"] = ActiveSignature;
        return help;
    }
}

/// <summary>A signature of a call.</summary>
/// <param name="Label">The signature, such as <c>Log(string message, int level)</c>.</param>
/// <param name="Documentation">Its documentation; null for none.</param>
/// <param name="Parameters">Its parameters.</param>
/// <param name="ActiveParameter">The parameter at the caret; -1 for none.</param>
internal sealed record SignatureInfo(
    string Label,
    HoverText? Documentation,
    IReadOnlyList<ParameterInfo> Parameters,
    int ActiveParameter
)
{
    /// <summary>Whether its active parameter falls outside its parameters: it cannot match the call.</summary>
    public bool CannotMatch { get; init; }

    /// <summary>
    /// Reads a <c>SignatureInformation</c>. Its own active parameter wins over the answer's; without either it is the
    /// first, and one outside the parameters is none, as in VS Code.
    /// </summary>
    /// <exception cref="JsonException">It is not a signature.</exception>
    public static SignatureInfo Read(JsonElement signature, int? activeParameter)
    {
        if (signature.ValueKind != JsonValueKind.Object || ServerJson.String(signature, "label") is not { } label)
            throw new JsonException("A signature needs a label.");
        var parameters = new List<ParameterInfo>();
        if (signature.TryGetProperty("parameters", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement parameter in array.EnumerateArray())
                parameters.Add(ParameterInfo.Read(parameter, label));
        }
        int active = ServerJson.Number(signature, "activeParameter") ?? activeParameter ?? 0;
        return new SignatureInfo(
            label,
            ServerJson.Documentation(signature),
            parameters,
            active >= 0 && active < parameters.Count ? active : -1
        )
        {
            // The call has an argument where the signature has no parameter; one without parameters may still be
            // called without arguments.
            CannotMatch = active >= parameters.Count && !(active == 0 && parameters.Count == 0),
        };
    }
}

/// <summary>A parameter of a signature.</summary>
/// <param name="Start">Where its label starts in the signature's label.</param>
/// <param name="Length">The length of its label there; 0 when the signature's label does not show it.</param>
/// <param name="Documentation">Its documentation; null for none.</param>
internal sealed record ParameterInfo(int Start, int Length, HoverText? Documentation)
{
    /// <summary>
    /// Reads a <c>ParameterInformation</c>: its label is offsets into the signature's label, or a string, which is
    /// found there as VS Code finds it: the first time it stands as a whole word.
    /// </summary>
    /// <exception cref="JsonException">It is not a parameter.</exception>
    public static ParameterInfo Read(JsonElement parameter, string signature)
    {
        if (parameter.ValueKind != JsonValueKind.Object || !parameter.TryGetProperty("label", out JsonElement label))
            throw new JsonException("A parameter needs a label.");
        HoverText? documentation = ServerJson.Documentation(parameter);
        if (
            label.ValueKind == JsonValueKind.Array
            && label.GetArrayLength() == 2
            && label[0].TryGetInt32(out int start)
            && label[1].TryGetInt32(out int end)
        )
        {
            start = Math.Clamp(start, 0, signature.Length);
            return new ParameterInfo(start, Math.Clamp(end, start, signature.Length) - start, documentation);
        }
        if (label.ValueKind != JsonValueKind.String)
            throw new JsonException("A parameter's label must be a string or two offsets.");
        string text = label.GetString() ?? "";
        int index = text.Length == 0 ? -1 : WholeWord(signature, text);
        return index < 0
            ? new ParameterInfo(0, 0, documentation)
            : new ParameterInfo(index, text.Length, documentation);
    }

    /// <summary>
    /// The first index of <paramref name="text"/> in <paramref name="signature"/> with no word character right before
    /// or after it; -1 for none.
    /// </summary>
    private static int WholeWord(string signature, string text)
    {
        for (int index = signature.IndexOf(text, StringComparison.Ordinal); index >= 0; )
        {
            int end = index + text.Length;
            if (
                (index == 0 || !IsWordCharacter(signature[index - 1]))
                && (end == signature.Length || !IsWordCharacter(signature[end]))
            )
                return index;
            index = signature.IndexOf(text, index + 1, StringComparison.Ordinal);
        }
        return -1;

        static bool IsWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}

/// <summary>How signature help was asked for.</summary>
/// <param name="Kind">One of <c>SignatureHelpTriggerKind</c>.</param>
/// <param name="Character">The character typed, for a trigger character.</param>
/// <param name="Showing">The signature help that showed, with the signature the user chose; null when none did.</param>
internal readonly record struct SignatureHelpTrigger(int Kind, char? Character, SignatureHelpInfo? Showing);
