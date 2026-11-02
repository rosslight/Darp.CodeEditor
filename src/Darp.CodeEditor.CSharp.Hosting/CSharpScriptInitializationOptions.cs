using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Darp.CodeEditor.CSharp.Hosting;

/// <summary>
/// Reads and writes the versioned section of the server's <c>initializationOptions</c>:
/// <c>{ "csharpScript": { "version": 1, ...environment } }</c>.
/// </summary>
public static class CSharpScriptInitializationOptions
{
    public const string SectionName = "csharpScript";

    /// <summary>The version of the section this library writes and reads.</summary>
    public const int Version = 1;

    /// <summary>Creates the <c>initializationOptions</c> object for the <c>initialize</c> request.</summary>
    public static JsonObject Create(CSharpScriptEnvironment environment)
    {
        var section = new EnvironmentSection
        {
            Version = Version,
            Globals = environment.Globals,
            References = environment.References,
            FrameworkReferences = environment.FrameworkReferences,
            Imports = environment.Imports,
            LanguageVersion = environment.LanguageVersion,
            Nullable = environment.Nullable,
            AllowUnsafe = environment.AllowUnsafe,
            CheckOverflow = environment.CheckOverflow,
            WarningLevel = environment.WarningLevel,
        };
        return new JsonObject
        {
            [SectionName] = JsonSerializer.SerializeToNode(section, HostingJsonContext.Default.EnvironmentSection),
        };
    }

    /// <summary>Reads the environment from <c>initializationOptions</c>. Omitted values keep their defaults.</summary>
    /// <returns>Null when the options have no <c>csharpScript</c> section.</returns>
    /// <exception cref="FormatException">The section has another version or is not a valid environment.</exception>
    public static CSharpScriptEnvironment? Read(JsonElement initializationOptions)
    {
        if (
            initializationOptions.ValueKind != JsonValueKind.Object
            || !initializationOptions.TryGetProperty(SectionName, out JsonElement element)
        )
        {
            return null;
        }
        EnvironmentSection? section;
        try
        {
            section = element.Deserialize(HostingJsonContext.Default.EnvironmentSection);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The \"{SectionName}\" section is invalid: {exception.Message}", exception);
        }
        if (section?.Version != Version)
        {
            throw new FormatException(
                $"The \"{SectionName}\" section must have \"version\": {Version}; this server reads version {Version} only."
            );
        }
        var defaults = new CSharpScriptEnvironment();
        return new CSharpScriptEnvironment
        {
            Globals = section.Globals,
            References = section.References ?? defaults.References,
            FrameworkReferences = section.FrameworkReferences ?? defaults.FrameworkReferences,
            Imports = section.Imports ?? defaults.Imports,
            LanguageVersion = section.LanguageVersion ?? defaults.LanguageVersion,
            Nullable = section.Nullable ?? defaults.Nullable,
            AllowUnsafe = section.AllowUnsafe ?? defaults.AllowUnsafe,
            CheckOverflow = section.CheckOverflow ?? defaults.CheckOverflow,
            WarningLevel = section.WarningLevel ?? defaults.WarningLevel,
        };
    }
}

/// <summary>The section as it is written in JSON; every value but the version is optional.</summary>
internal sealed class EnvironmentSection
{
    public int? Version { get; set; }

    public CSharpScriptGlobals? Globals { get; set; }

    public IReadOnlyList<string>? References { get; set; }

    public IReadOnlyList<string>? FrameworkReferences { get; set; }

    public IReadOnlyList<string>? Imports { get; set; }

    public string? LanguageVersion { get; set; }

    public CSharpScriptNullable? Nullable { get; set; }

    public bool? AllowUnsafe { get; set; }

    public bool? CheckOverflow { get; set; }

    public int? WarningLevel { get; set; }
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(EnvironmentSection))]
internal sealed partial class HostingJsonContext : JsonSerializerContext;
