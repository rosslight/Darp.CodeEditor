using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp.Hosting;

namespace Darp.CodeEditor.Luau;

/// <summary>Luau script editing: the language service of luau-lsp for an environment.</summary>
/// <example>
/// <code>
/// LspLanguageService luau = LuauLanguageServer.CreateService(environment);
/// LuauLanguage.Configure(editor);     // grammar and editing helpers
/// editor.LanguageService = luau;      // every Luau editor of the app shares the service
/// await luau.DisposeAsync();          // when the app ends
/// </code>
/// </example>
public static class LuauLanguageServer
{
    /// <summary>The luau-lsp release that <c>Darp.CodeEditor.Luau.targets</c> puts next to the app.</summary>
    public const string Version = "1.70.1";

    /// <summary>The section of luau-lsp's settings in <c>workspace/configuration</c>.</summary>
    internal const string SettingsSection = "luau-lsp";

    /// <summary>Where <c>Darp.CodeEditor.Luau.targets</c> puts luau-lsp and its base configurations.</summary>
    internal static string HelperDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "helpers", "luau-lsp");

    /// <summary>The file name of the luau-lsp executable on this platform.</summary>
    public static string ServerFileName => OperatingSystem.IsWindows() ? "luau-lsp.exe" : "luau-lsp";

    /// <summary>
    /// Creates a service that starts luau-lsp for <paramref name="environment"/> when the first Luau document opens, or
    /// on <see cref="LspLanguageService.PrewarmAsync"/>. One service, and so one server, serves all Luau editors of an
    /// app.
    /// </summary>
    /// <exception cref="ArgumentException">A definition file has no name, or one with <c>=</c>.</exception>
    public static LspLanguageService CreateService(
        LuauEnvironment environment,
        LuauLanguageServerOptions? options = null
    )
    {
        options ??= new LuauLanguageServerOptions();
        foreach (LuauDefinitions definitions in environment.Definitions)
        {
            string name = definitions.Name.TrimStart('@');
            if (name.Length == 0 || name.Contains('=', StringComparison.Ordinal))
                throw new ArgumentException($"The definitions name \"{definitions.Name}\" is empty or has a '='.");
        }
        string server = Path.GetFullPath(options.ServerPath ?? Path.Combine(HelperDirectory, ServerFileName));
        string? workspace = options.WorkspaceDirectory is { } directory ? Path.GetFullPath(directory) : null;
        // Without a workspace each script stands alone; the root only places the documents in luau-lsp's workspace,
        // which is configured through workspace/configuration. It is not created.
        string root = workspace ?? Path.Combine(AppContext.BaseDirectory, "luau-scripts");
        using JsonDocument settings = JsonDocument.Parse(
            CreateSettings(environment, workspace is not null).ToJsonString()
        );
        JsonElement section = settings.RootElement.Clone();
        return new LspLanguageService(
            new LspLanguageServiceOptions(_ => StartAsync(server, environment, options), "luau")
            {
                Settings = item => item.Section == SettingsSection ? section : null,
                RootUri = ToFileUri(root),
                DocumentUri = id => ToDocumentUri(root, id),
                ClientName = "Darp.CodeEditor.Luau",
                Log = options.Log,
                Restart = options.Restart,
                ResponseTimeout = options.ResponseTimeout,
                MaxResponseTimeout = options.MaxResponseTimeout,
            }
        );
    }

    /// <summary>The luau-lsp settings for <paramref name="environment"/>: this package's, then the host's.</summary>
    /// <param name="indexWorkspace">Whether luau-lsp reads the scripts of the workspace directory.</param>
    internal static JsonObject CreateSettings(LuauEnvironment environment, bool indexWorkspace)
    {
        var settings = new JsonObject
        {
            // luau-lsp defaults to Roblox: its globals, its DataModel types and its sourcemaps.
            ["platform"] = new JsonObject { ["type"] = "standard" },
            // Without snippets luau-lsp would insert "name()" and leave the caret after the parentheses; "(" brings
            // signature help instead.
            ["completion"] = new JsonObject { ["addParentheses"] = false },
            ["index"] = new JsonObject { ["enabled"] = indexWorkspace },
        };
        if (environment.Settings is { } overrides)
            Merge(settings, overrides);
        return settings;
    }

    /// <summary>The arguments of luau-lsp for <paramref name="environment"/>.</summary>
    /// <exception cref="FileNotFoundException">A definition, documentation or base configuration file is missing.</exception>
    internal static List<string> CreateArguments(LuauEnvironment environment, IReadOnlyList<string> extra)
    {
        List<string> arguments = ["lsp", "--stdio"];
        foreach (LuauDefinitions definitions in environment.Definitions)
        {
            arguments.Add("--definitions");
            arguments.Add($"@{definitions.Name.TrimStart('@')}={RequireFile(definitions.Path, "definition")}");
        }
        foreach (string documentation in environment.DocumentationFiles)
        {
            arguments.Add("--docs");
            arguments.Add(RequireFile(documentation, "documentation"));
        }
        string mode = environment.Mode switch
        {
            LuauMode.NoCheck => "nocheck",
            LuauMode.Strict => "strict",
            _ => "nonstrict",
        };
        arguments.Add("--base-luaurc");
        arguments.Add(RequireFile(Path.Combine(HelperDirectory, "luaurc", $"{mode}.luaurc"), "base configuration"));
        arguments.AddRange(extra);
        return arguments;
    }

    /// <summary>
    /// The URI of a document id in luau-lsp's workspace, as luau-lsp writes it: a relative id below
    /// <paramref name="root"/>, a path in it as it is. Other paths go below the root too, so that luau-lsp configures
    /// them like the rest; a script outside the workspace has no neighbors to require anyway.
    /// </summary>
    internal static string ToDocumentUri(string root, string documentId)
    {
        string path = Path.GetFullPath(documentId, root);
        string relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            // C:\scripts\a.luau becomes <root>/C/scripts/a.luau, /home/a.luau <root>/home/a.luau.
            string outside = path.Replace(":", "", StringComparison.Ordinal).TrimStart('/', '\\');
            path = Path.Combine(root, outside);
        }
        return ToFileUri(path);
    }

    /// <summary>
    /// The <c>file</c> URI of an absolute path as luau-lsp writes it, like VS Code: a lower-case drive letter, and every
    /// character but letters, digits, <c>-._~</c> and <c>/</c> percent-encoded. The client finds the document of
    /// published diagnostics by this exact text; <see cref="Uri"/> would keep <c>C:</c> and <c>(</c>, which luau-lsp
    /// writes as <c>c%3A</c> and <c>%28</c>.
    /// </summary>
    internal static string ToFileUri(string path)
    {
        string slashed = path.Replace('\\', '/');
        var uri = new StringBuilder("file://");
        if (slashed.StartsWith("//", StringComparison.Ordinal))
        {
            // A UNC path: the server is the authority.
            int share = slashed.IndexOf('/', 2);
            uri.Append(slashed, 2, (share < 0 ? slashed.Length : share) - 2);
            slashed = share < 0 ? "/" : slashed[share..];
        }
        else if (!slashed.StartsWith('/'))
        {
            slashed = "/" + slashed;
        }
        if (slashed.Length >= 3 && char.IsAsciiLetter(slashed[1]) && slashed[2] == ':')
            slashed = string.Concat("/", char.ToLowerInvariant(slashed[1]).ToString(), slashed.AsSpan(2));
        foreach (byte b in Encoding.UTF8.GetBytes(slashed))
        {
            if (
                char.IsAsciiLetterOrDigit((char)b) || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~' or (byte)'/'
            )
                uri.Append((char)b);
            else
                uri.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return uri.ToString();
    }

    private static async Task<LanguageServerTransport> StartAsync(
        string server,
        LuauEnvironment environment,
        LuauLanguageServerOptions options
    )
    {
        if (!File.Exists(server))
        {
            throw new FileNotFoundException(
                $"luau-lsp is not at {server}. Import Darp.CodeEditor.Luau.targets into the app project, or set "
                    + "LuauLanguageServerOptions.ServerPath.",
                server
            );
        }
        var start = new ProcessStartInfo(server) { WorkingDirectory = Path.GetDirectoryName(server) };
        foreach (string argument in CreateArguments(environment, options.Arguments))
            start.ArgumentList.Add(argument);
        Action<string>? log = options.Log;
        return await LanguageServerTransport
            .StartProcessAsync(start, log is null ? null : line => log($"luau-lsp: {line}"))
            .ConfigureAwait(false);
    }

    private static string RequireFile(string path, string kind)
    {
        string full = Path.GetFullPath(path);
        return File.Exists(full)
            ? full
            : throw new FileNotFoundException(
                kind == "base configuration"
                    ? $"The Luau base configuration is not at {full}. Import Darp.CodeEditor.Luau.targets into the app project."
                    : $"The Luau {kind} file {full} does not exist.",
                full
            );
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="target"/>; objects merge, everything else replaces.</summary>
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach ((string name, JsonNode? value) in source)
        {
            if (value is JsonObject inner && target[name] is JsonObject existing)
                Merge(existing, inner);
            else
                target[name] = value?.DeepClone();
        }
    }
}

/// <summary>Where luau-lsp is, which scripts it sees, and how it runs.</summary>
public sealed record LuauLanguageServerOptions
{
    /// <summary>
    /// The luau-lsp executable. By default <c>helpers/luau-lsp/luau-lsp</c> (<c>.exe</c> on Windows) next to the app,
    /// where <c>Darp.CodeEditor.Luau.targets</c> puts it.
    /// </summary>
    public string? ServerPath { get; init; }

    /// <summary>
    /// The folder of the app's scripts, if they live in one: scripts can then <c>require</c> each other, and the
    /// <c>.luaurc</c> files in it apply. Document ids are paths relative to it, or absolute paths in it. By default
    /// none: each script stands alone, and document ids are names such as <c>script.luau</c>.
    /// </summary>
    public string? WorkspaceDirectory { get; init; }

    /// <summary>
    /// Further arguments of <c>luau-lsp lsp</c>, such as <c>--flag:LuauSolverV2=true</c> to set a Luau flag, or
    /// <c>--no-flags-enabled</c> to keep Luau's flags at their defaults instead of luau-lsp's.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Receives luau-lsp's standard error, its log messages and its restarts.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>How long a crashed server waits before it restarts.</summary>
    public SupervisorOptions? Restart { get; init; }

    /// <summary>
    /// How long luau-lsp may take to answer or to read before it counts as hung and restarts; see
    /// <see cref="LspLanguageServiceOptions.ResponseTimeout"/>.
    /// </summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest that hangs in a row raise <see cref="ResponseTimeout"/> to; see
    /// <see cref="LspLanguageServiceOptions.MaxResponseTimeout"/>.
    /// </summary>
    public TimeSpan MaxResponseTimeout { get; init; } = TimeSpan.FromMinutes(2);
}
