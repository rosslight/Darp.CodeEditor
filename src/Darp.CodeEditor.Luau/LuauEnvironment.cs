using System.Text.Json.Nodes;

namespace Darp.CodeEditor.Luau;

/// <summary>
/// What the app's Luau scripts see: the app's API, described in definition and documentation files, and how strictly
/// scripts are type-checked.
/// </summary>
/// <example>
/// <code>
/// var environment = new LuauEnvironment
/// {
///     Definitions = [new LuauDefinitions("app", Path.Combine(AppContext.BaseDirectory, "app.d.luau"))],
///     DocumentationFiles = [Path.Combine(AppContext.BaseDirectory, "app.docs.json")],
///     Mode = LuauMode.Strict,
/// };
/// </code>
/// </example>
public sealed record LuauEnvironment
{
    /// <summary>
    /// Definition files that declare the app's globals, functions and types, such as
    /// <c>declare function log(message: string): ()</c>.
    /// </summary>
    public IReadOnlyList<LuauDefinitions> Definitions { get; init; } = [];

    /// <summary>
    /// Documentation files for the symbols of <see cref="Definitions"/>: JSON objects keyed by symbols such as
    /// <c>@app/global/log</c> or <c>@app/globaltype/Device.readTemperature</c>. Completion, hover and signature help
    /// show them. <c>---</c> comments in a definition file document its symbols too.
    /// </summary>
    public IReadOnlyList<string> DocumentationFiles { get; init; } = [];

    /// <summary>
    /// How strictly scripts are type-checked unless they start with <c>--!strict</c>, <c>--!nonstrict</c> or
    /// <c>--!nocheck</c>.
    /// </summary>
    public LuauMode Mode { get; init; } = LuauMode.Nonstrict;

    /// <summary>
    /// Further luau-lsp settings: the contents of its <c>luau-lsp</c> section, such as
    /// <c>{ "types": { "disabledGlobals": ["os.exit"] } }</c>. They are merged over the settings this package sets:
    /// the standard platform instead of Roblox, and completions that insert names without parentheses.
    /// </summary>
    public JsonObject? Settings { get; init; }
}

/// <summary>A definition file, loaded into the global scope of every script.</summary>
/// <param name="Name">
/// The name the definitions are loaded under, such as <c>app</c>. Documentation symbols of its declarations start with
/// <c>@</c> and the name, such as <c>@app/global/log</c>.
/// </param>
/// <param name="Path">The <c>.d.luau</c> file.</param>
public sealed record LuauDefinitions(string Name, string Path);

/// <summary>How strictly Luau type-checks a script.</summary>
public enum LuauMode
{
    /// <summary>No type errors, only syntax errors and lints.</summary>
    NoCheck,

    /// <summary>Luau's default: types that are unknown are <c>any</c>, so only clear mistakes are errors.</summary>
    Nonstrict,

    /// <summary>Every type is inferred and checked.</summary>
    Strict,
}
