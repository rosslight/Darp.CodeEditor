using System.Text.Json;
using Avalonia.Logging;
using Avalonia.Platform;

namespace Darp.CodeEditor.Loading;

/// <summary>Opens the files of languages and themes, also before Avalonia is set up, and logs when they fail.</summary>
internal static class AssetFiles
{
    /// <summary>The area of the library's entries in Avalonia's log.</summary>
    public const string LogArea = "CodeEditor";

    /// <summary>VS Code's configuration and theme files are JSON with comments and trailing commas.</summary>
    public static readonly JsonDocumentOptions Jsonc = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads <c>avares://</c> files. Avalonia's loader is a service only after setup, and its locator is internal, so the
    /// library keeps its own instance of the loader that Avalonia registers.
    /// </summary>
    private static readonly StandardAssetLoader s_assetLoader = new();

    /// <summary>Opens an absolute <c>avares://</c> or <c>file://</c> URI.</summary>
    public static Stream Open(Uri uri) =>
        uri.Scheme switch
        {
            "avares" => s_assetLoader.Open(uri),
            "file" => File.OpenRead(uri.LocalPath),
            _ => throw new NotSupportedException($"Only avares:// and file:// URIs can be loaded: {uri}"),
        };

    /// <summary>Reports what could not be loaded to Avalonia's log, as binding errors are.</summary>
    public static void LogError(object source, string what, Exception exception) =>
        Logger.TryGet(LogEventLevel.Error, LogArea)?.Log(source, "Could not load {What}: {Exception}", what, exception);
}
