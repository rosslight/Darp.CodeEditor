using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

// Going to brackets, as VS Code's bracketMatching does it.
internal sealed partial class EditingHelpers
{
    /// <summary>
    /// Moves the caret to the bracket that matches the one at the start of the selection, as VS Code's
    /// <c>jumpToBracket</c> does: from an opening bracket to before its closing one, from a closing bracket to before
    /// its opening one; elsewhere to before the closing bracket of the pair around it, or else to the next bracket.
    /// Without any, the caret goes to the selection's start.
    /// </summary>
    public void JumpToBracket()
    {
        (int anchor, int caret) = SelectionEnds();
        int position = Math.Min(anchor, caret);
        IReadOnlyList<CharacterPair> brackets = Configuration.Brackets;
        int? target;
        if (BracketMatcher.FindPair(Buffer, position, brackets, _highlighter.TokensOf) is var (open, close))
            target = Touches(close, position) ? open : close;
        else if (BracketMatcher.FindEnclosing(Buffer, position, brackets, _highlighter.TokensOf) is var (_, enclosing))
            target = enclosing;
        else
            target = BracketMatcher.FindNext(Buffer, position, brackets, _highlighter.TokensOf);
        Select(target ?? position, target ?? position);
    }

    /// <summary>
    /// Selects the brackets that VS Code's <c>selectToBracket</c> finds at the start of the selection, and the text
    /// between them: the pair of the bracket there, or else the pair around it, or else the pair of the next bracket.
    /// From a closing bracket, the selection goes backward. Without a pair, nothing changes.
    /// </summary>
    public void SelectToBracket()
    {
        (int anchor, int caret) = SelectionEnds();
        int position = Math.Min(anchor, caret);
        IReadOnlyList<CharacterPair> brackets = Configuration.Brackets;
        Func<int, int[]?> tokensOf = _highlighter.TokensOf;
        (int Open, int Close)? pair =
            BracketMatcher.FindPair(Buffer, position, brackets, tokensOf)
            ?? BracketMatcher.FindEnclosing(Buffer, position, brackets, tokensOf)
            ?? (
                BracketMatcher.FindNext(Buffer, position, brackets, tokensOf) is { } next
                    ? BracketMatcher.FindPair(Buffer, next, brackets, tokensOf)
                    : null
            );
        if (pair is not var (open, close))
            return;
        if (Touches(close, position))
            Select(close + 1, open);
        else
            Select(open, close + 1);
    }

    /// <summary>Whether a bracket at <paramref name="bracket"/> touches <paramref name="position"/>: before or after it.</summary>
    private static bool Touches(int bracket, int position) => position == bracket || position == bracket + 1;
}
