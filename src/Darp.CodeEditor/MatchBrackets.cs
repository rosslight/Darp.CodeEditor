namespace Darp.CodeEditor;

/// <summary>
/// Which brackets the editor highlights at the caret, as VS Code's <c>editor.matchBrackets</c>: for
/// <see cref="CodeEditor.MatchBrackets"/>.
/// </summary>
public enum MatchBrackets
{
    /// <summary>The bracket next to the caret and its match, or else the innermost pair around the caret.</summary>
    Always,

    /// <summary>Only the bracket next to the caret and its match.</summary>
    Near,

    /// <summary>No brackets.</summary>
    Never,
}
