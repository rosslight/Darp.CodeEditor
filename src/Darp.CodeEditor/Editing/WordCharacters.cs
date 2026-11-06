namespace Darp.CodeEditor.Editing;

/// <summary>What VS Code counts as part of a word, by its default word separators.</summary>
internal static class WordCharacters
{
    private const string Separators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

    /// <summary>Whether <paramref name="c"/> is neither whitespace nor a word separator.</summary>
    public static bool IsWordCharacter(char c) => !char.IsWhiteSpace(c) && !Separators.Contains(c);
}
