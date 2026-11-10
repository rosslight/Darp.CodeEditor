namespace Darp.CodeEditor.LanguageClient;

/// <summary>The URIs of a server's documents, and when two of them name one document.</summary>
internal static class DocumentUris
{
    private static int s_lastUntitled;

    /// <summary>The URI of a document with <paramref name="path"/>: its file's, or a new untitled one.</summary>
    public static string Of(string? path) =>
        path is null ? $"untitled:Untitled-{Interlocked.Increment(ref s_lastUntitled)}" : File(path);

    /// <summary>The <c>file:</c> URI of a path.</summary>
    public static string File(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>
    /// What names a document, whatever form a server writes its URI in. A file URI is its decoded path, with a
    /// Windows drive letter in upper case, and its host in lower case: servers built on VS Code's URI library write
    /// <c>file:///C:/a.txt</c> as <c>file:///c%3A/a.txt</c>. The rest of the path keeps its case, as paths differ by
    /// case on Linux. Other URIs, such as untitled ones, are their own identity.
    /// </summary>
    public static string Identity(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || !parsed.IsFile)
            return uri;
        string path = Uri.UnescapeDataString(parsed.AbsolutePath);
        // A drive, with or without the slash that a URI puts before it.
        int drive = path.StartsWith('/') ? 1 : 0;
        if (path.Length > drive + 1 && char.IsAsciiLetter(path[drive]) && path[drive + 1] == ':')
            path = $"/{char.ToUpperInvariant(path[drive])}{path[(drive + 1)..]}";
        return $"file://{parsed.Host.ToLowerInvariant()}{path}";
    }
}
