namespace Darp.CodeEditor;

/// <summary>
/// A language: its TextMate grammar, its editing rules (comments, brackets, auto-closing pairs, indentation) and,
/// optionally, the language server that provides completion, hover, signature help and diagnostics.
/// </summary>
/// <remarks>
/// Grammars and editing rules use the formats of VS Code extensions (<c>.tmLanguage.json</c> and
/// <c>language-configuration.json</c>), so any language with a VS Code extension can be added from its files. Create a
/// language once and share it between documents; it is safe to use from any thread.
/// </remarks>
public sealed class CodeLanguage
{
    private CodeLanguage() { }

    /// <summary>The language identifier that the language server receives, such as <c>json</c>.</summary>
    public string Id => throw new NotImplementedException();

    /// <summary>The language server of the language's documents, if any.</summary>
    public LanguageServer? Server => throw new NotImplementedException();

    /// <summary>
    /// A language whose grammar and editing rules ship with the editor, taken from VS Code's built-in extensions, such
    /// as <c>csharp</c>, <c>json</c>, <c>lua</c>, <c>xml</c> or <c>yaml</c>.
    /// </summary>
    /// <param name="id">The VS Code language identifier.</param>
    /// <param name="server">The language server of the language's documents, if any.</param>
    /// <exception cref="ArgumentException">The editor does not ship a grammar for <paramref name="id"/>.</exception>
    public static CodeLanguage Bundled(string id, LanguageServer? server = null) => throw new NotImplementedException();

    /// <summary>A language from the files of a VS Code extension.</summary>
    /// <remarks>
    /// Only records the paths: the files are read off the UI thread, by <see cref="PreloadAsync"/> or when an editor
    /// first needs them. The grammar must be self-contained, or include only scopes of the bundled grammars.
    /// </remarks>
    /// <param name="id">The language identifier that the language server receives.</param>
    /// <param name="grammarPath">The TextMate grammar, a <c>.tmLanguage.json</c> file.</param>
    /// <param name="configurationPath">
    /// The editing rules, a <c>language-configuration.json</c> file. Without one, the language has no comment toggling,
    /// bracket matching or auto-closing pairs.
    /// </param>
    /// <param name="server">The language server of the language's documents, if any.</param>
    public static CodeLanguage FromFiles(
        string id,
        string grammarPath,
        string? configurationPath = null,
        LanguageServer? server = null
    ) => throw new NotImplementedException();

    /// <summary>
    /// Loads the grammar and editing rules off the UI thread, so that the first editor of the language shows colors
    /// sooner. Optional: editors load them on their own otherwise.
    /// </summary>
    public Task PreloadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
