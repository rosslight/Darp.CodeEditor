namespace Darp.CodeEditor;

/// <summary>
/// The colors of a <see cref="CodeEditor"/>: a VS Code color theme, with its TextMate token colors and its semantic
/// token colors, so that syntax and language-server colors never clash.
/// </summary>
/// <remarks>Create a theme once and share it between editors; it is safe to use from any thread.</remarks>
public sealed class CodeEditorTheme
{
    private CodeEditorTheme() { }

    /// <summary>VS Code's default dark theme.</summary>
    public static CodeEditorTheme DarkPlus { get; } = new();

    /// <summary>VS Code's default light theme.</summary>
    public static CodeEditorTheme LightPlus { get; } = new();

    /// <summary>A VS Code color theme file, such as one from a theme extension.</summary>
    public static CodeEditorTheme FromFile(string path) => throw new NotImplementedException();

    /// <summary>Loads the theme off the UI thread. Optional: editors load it on their own otherwise.</summary>
    public Task PreloadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
