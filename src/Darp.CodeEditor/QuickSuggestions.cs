namespace Darp.CodeEditor;

/// <summary>
/// Where typing a word opens completion by itself, as VS Code's <c>editor.quickSuggestions</c>: by the kind of text the
/// typed character is in, as the language's grammar tells. Ctrl+Space and the server's trigger characters open it
/// everywhere. Set it per language, as <see cref="CodeLanguage.QuickSuggestions"/>.
/// </summary>
/// <param name="Other">Whether typing opens it in code: outside comments and strings.</param>
/// <param name="Comments">Whether typing opens it in comments.</param>
/// <param name="Strings">Whether typing opens it in strings.</param>
public readonly record struct QuickSuggestions(bool Other, bool Comments, bool Strings)
{
    /// <summary>VS Code's default: in code, not in comments or strings.</summary>
    public static QuickSuggestions Default => new(Other: true, Comments: false, Strings: false);
}
