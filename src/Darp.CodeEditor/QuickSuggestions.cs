namespace Darp.CodeEditor;

/// <summary>
/// Where typing a word opens completion in a language's documents, by the kind of text typed in, as VS Code's
/// <c>editor.quickSuggestions</c>. Ctrl+Space and the server's trigger characters open it everywhere.
/// </summary>
/// <param name="Other">In code: anything but comments and strings.</param>
/// <param name="Comments">In comments.</param>
/// <param name="Strings">In strings.</param>
public readonly record struct QuickSuggestions(bool Other, bool Comments, bool Strings)
{
    /// <summary>In code only.</summary>
    public static QuickSuggestions Default { get; } = new(Other: true, Comments: false, Strings: false);
}
