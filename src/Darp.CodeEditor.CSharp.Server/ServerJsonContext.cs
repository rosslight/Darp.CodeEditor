using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>An LSP <c>CompletionList</c> the server wrote as JSON, sent as it is.</summary>
[JsonConverter(typeof(CompletionListJsonConverter))]
internal sealed class CompletionListJson
{
    public CompletionListJson(ReadOnlyMemory<byte> utf8) => Utf8 = utf8;

    /// <summary>One JSON value, UTF-8 encoded.</summary>
    public ReadOnlyMemory<byte> Utf8 { get; }
}

/// <summary>Writes a <see cref="CompletionListJson"/> without copying it into a document first.</summary>
internal sealed class CompletionListJsonConverter : JsonConverter<CompletionListJson>
{
    public override CompletionListJson Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) => throw new NotSupportedException("The server only writes completion lists.");

    public override void Write(Utf8JsonWriter writer, CompletionListJson value, JsonSerializerOptions options) =>
        writer.WriteRawValue(value.Utf8.Span, skipInputValidation: true);
}

[JsonSerializable(typeof(CompletionListJson))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
