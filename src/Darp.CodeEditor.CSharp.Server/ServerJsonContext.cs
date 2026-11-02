using System.Text.Json.Serialization;
using LspDiagnostic = EmmyLua.LanguageServer.Framework.Protocol.Model.Diagnostic.Diagnostic;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Parameters of <c>textDocument/publishDiagnostics</c> with the document URI as the client sent it.</summary>
internal sealed record PublishDiagnosticsNotification(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version")] int? Version,
    [property: JsonPropertyName("diagnostics")] List<LspDiagnostic> Diagnostics
);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PublishDiagnosticsNotification))]
internal sealed partial class ServerJsonContext : JsonSerializerContext;
