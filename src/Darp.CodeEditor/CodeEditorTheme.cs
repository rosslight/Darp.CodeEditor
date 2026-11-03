using TextMateSharp.Grammars;

namespace Darp.CodeEditor;

/// <summary>A VS Code color theme: one that comes with TextMateSharp, or a theme file (.json).</summary>
/// <remarks>
/// The theme colors the text, the line numbers, selection and current line, and the editor's own UI: completion, hover
/// and signature help, diagnostics, semantic colors and search. Colors a theme leaves out fall back to VS Code's
/// defaults for dark or light themes.
/// </remarks>
public sealed record CodeEditorTheme
{
    private CodeEditorTheme(ThemeName? bundled, string? file)
    {
        Bundled = bundled;
        File = file;
    }

    /// <summary>VS Code's default dark theme.</summary>
    public static CodeEditorTheme DarkPlus { get; } = new(ThemeName.DarkPlus, null);

    /// <summary>VS Code's default light theme.</summary>
    public static CodeEditorTheme LightPlus { get; } = new(ThemeName.LightPlus, null);

    internal ThemeName? Bundled { get; }

    internal string? File { get; }

    /// <summary>A theme that comes with TextMateSharp.</summary>
    public static CodeEditorTheme FromBundled(ThemeName name) => new(name, null);

    /// <summary>A VS Code theme file. Files it includes are looked up next to it.</summary>
    public static CodeEditorTheme FromFile(string path) => new(null, Path.GetFullPath(path));

    public override string ToString() => File ?? Bundled?.ToString() ?? "";
}
