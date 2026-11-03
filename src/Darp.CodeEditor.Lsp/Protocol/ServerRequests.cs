using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.Lsp.Protocol;

/// <summary>Parameters of <c>workspace/configuration</c>: the settings a server asks its client for.</summary>
public sealed record ConfigurationParams(IReadOnlyList<ConfigurationItem> Items);

/// <param name="Section">The settings section, such as <c>luau-lsp</c>; null for all settings.</param>
public sealed record ConfigurationItem(string? ScopeUri = null, string? Section = null);

/// <summary>Parameters of <c>client/registerCapability</c>.</summary>
public sealed record RegistrationParams(IReadOnlyList<Registration> Registrations);

/// <param name="RegisterOptions">The options of the method, kept as sent.</param>
public sealed record Registration(string Id, string Method, JsonElement? RegisterOptions = null);

/// <summary>Parameters of <c>client/unregisterCapability</c>.</summary>
/// <param name="Unregistrations">Named <c>unregisterations</c> in the protocol.</param>
public sealed record UnregistrationParams(
    [property: JsonPropertyName("unregisterations")] IReadOnlyList<Unregistration> Unregistrations
);

public sealed record Unregistration(string Id, string Method);

/// <summary>Parameters of <c>window/workDoneProgress/create</c>.</summary>
/// <param name="Token">The progress token, a number or a string, kept as sent.</param>
public sealed record WorkDoneProgressCreateParams(JsonElement Token);
