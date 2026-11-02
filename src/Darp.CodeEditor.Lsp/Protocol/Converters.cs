using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <summary>Reads <c>string | MarkupContent</c>, a string as plain text. Writes markup content.</summary>
internal sealed class MarkupContentConverter : JsonConverter<MarkupContent>
{
    public override MarkupContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? new MarkupContent(MarkupKind.PlainText, reader.GetString() ?? "")
            : Json.Read(ref reader, LspJsonContext.Default.MarkupContent);

    public override void Write(Utf8JsonWriter writer, MarkupContent value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, LspJsonContext.Default.MarkupContent);
}

/// <summary>Reads <c>boolean | options</c> as whether the feature is supported. Writes a boolean.</summary>
internal sealed class BooleanOrOptionsConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.StartObject => Json.Skip(ref reader),
            _ => throw new JsonException("Expected a boolean or options."),
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } enabled)
            writer.WriteBooleanValue(enabled);
        else
            writer.WriteNullValue();
    }
}

/// <summary>
/// Reads <c>TextDocumentSyncOptions | TextDocumentSyncKind</c>; a kind means open and close notifications and changes
/// of that kind. Writes options.
/// </summary>
internal sealed class TextDocumentSyncConverter : JsonConverter<TextDocumentSyncOptions>
{
    public override TextDocumentSyncOptions Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType != JsonTokenType.Number)
            return Json.Read(ref reader, LspJsonContext.Default.TextDocumentSyncOptions);
        var kind = (TextDocumentSyncKind)reader.GetInt32();
        return new TextDocumentSyncOptions(OpenClose: kind != TextDocumentSyncKind.None, Change: kind);
    }

    public override void Write(Utf8JsonWriter writer, TextDocumentSyncOptions value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, LspJsonContext.Default.TextDocumentSyncOptions);
}

/// <summary>Reads <c>integer | string</c> as a string. Writes a string.</summary>
internal sealed class StringOrNumberConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number
            ? reader.GetInt64().ToString(CultureInfo.InvariantCulture)
            : reader.GetString() ?? "";

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Reads and writes <c>string | [uinteger, uinteger]</c>.</summary>
internal sealed class ParameterLabelConverter : JsonConverter<ParameterLabel>
{
    public override ParameterLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String ? new ParameterLabel(reader.GetString() ?? "")
        : Json.Read(ref reader, InternalJsonContext.Default.Int32Array) is [int start, int end]
            ? new ParameterLabel(start, end)
        : throw new JsonException("A parameter label range has two offsets.");

    public override void Write(Utf8JsonWriter writer, ParameterLabel value, JsonSerializerOptions options)
    {
        if (value.Text is { } text)
            writer.WriteStringValue(text);
        else
            JsonSerializer.Serialize(writer, [value.Start, value.End], InternalJsonContext.Default.Int32Array);
    }
}

/// <summary>Reads and writes <c>TextEdit | InsertReplaceEdit</c>.</summary>
internal sealed class CompletionTextEditConverter : JsonConverter<CompletionTextEdit>
{
    public override CompletionTextEdit Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Json.Read(ref reader, InternalJsonContext.Default.EditShape) switch
        {
            { NewText: { } text, Range: { } range } => new CompletionTextEdit(text, range),
            { NewText: { } text, Insert: { } insert, Replace: { } replace } => new CompletionTextEdit(
                text,
                insert,
                replace
            ),
            _ => throw new JsonException("A completion edit needs new text, and a range or insert and replace ranges."),
        };

    public override void Write(Utf8JsonWriter writer, CompletionTextEdit value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(
            writer,
            value.Insert == value.Replace
                ? new EditShape(value.NewText, Range: value.Insert)
                : new EditShape(value.NewText, Insert: value.Insert, Replace: value.Replace),
            InternalJsonContext.Default.EditShape
        );
}

/// <summary>Reads and writes <c>Range | { insert: Range; replace: Range }</c>.</summary>
internal sealed class CompletionEditRangeConverter : JsonConverter<CompletionEditRange>
{
    public override CompletionEditRange Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    ) =>
        Json.Read(ref reader, InternalJsonContext.Default.EditShape) switch
        {
            { Start: { } start, End: { } end } => new CompletionEditRange(new DocumentRange(start, end)),
            { Insert: { } insert, Replace: { } replace } => new CompletionEditRange(insert, replace),
            _ => throw new JsonException("An edit range needs a start and an end, or insert and replace ranges."),
        };

    public override void Write(Utf8JsonWriter writer, CompletionEditRange value, JsonSerializerOptions options)
    {
        if (value.Insert == value.Replace)
            JsonSerializer.Serialize(writer, value.Insert, LspJsonContext.Default.DocumentRange);
        else
        {
            JsonSerializer.Serialize(
                writer,
                new EditShape(Insert: value.Insert, Replace: value.Replace),
                InternalJsonContext.Default.EditShape
            );
        }
    }
}

/// <summary>Reads <c>CompletionList | CompletionItem[]</c>. Writes a completion list.</summary>
internal sealed class CompletionListConverter : JsonConverter<CompletionList>
{
    public override CompletionList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return new CompletionList(Json.Read(ref reader, LspJsonContext.Default.IReadOnlyListCompletionItem));
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a completion list.");
        CompletionList? list = null;
        bool isIncomplete = false;
        CompletionItemDefaults? defaults = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string name = reader.GetString() ?? "";
            reader.Read();
            if (name == "isIncomplete")
                isIncomplete = reader.GetBoolean();
            else if (name == "itemDefaults")
                defaults = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.CompletionItemDefaults);
            else if (name == "items")
                list = new CompletionList(Json.Read(ref reader, LspJsonContext.Default.IReadOnlyListCompletionItem));
            else
                reader.Skip();
        }
        return list is null
            ? throw new JsonException("A completion list needs items.")
            : list with
            {
                IsIncomplete = isIncomplete,
                ItemDefaults = defaults,
            };
    }

    public override void Write(Utf8JsonWriter writer, CompletionList value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isIncomplete"u8, value.IsIncomplete);
        if (value.ItemDefaults is { } defaults)
        {
            writer.WritePropertyName("itemDefaults"u8);
            JsonSerializer.Serialize(writer, defaults, LspJsonContext.Default.CompletionItemDefaults);
        }
        writer.WritePropertyName("items"u8);
        JsonSerializer.Serialize(writer, value.Items, LspJsonContext.Default.IReadOnlyListCompletionItem);
        writer.WriteEndObject();
    }
}

/// <summary>The members of LSP's edit and range unions.</summary>
internal sealed record EditShape(
    string? NewText = null,
    DocumentRange? Range = null,
    DocumentRange? Insert = null,
    DocumentRange? Replace = null,
    Position? Start = null,
    Position? End = null
);

internal static class Json
{
    /// <summary>Reads a value that must not be null.</summary>
    public static T Read<T>(ref Utf8JsonReader reader, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(ref reader, typeInfo) ?? throw new JsonException("Expected a value, not null.");

    /// <returns>True, for options objects that mean "supported".</returns>
    public static bool Skip(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return true;
    }
}
