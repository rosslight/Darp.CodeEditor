using System.Text.RegularExpressions;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The indentation of lines moved to another place, as VS Code's <c>moveLinesCommand.ts</c> asks for it with
/// <c>matchEnterRule</c> and <c>getGoodIndentForLine</c>: by the rules for Enter and the indentation rules of
/// <see cref="IndentRules"/>, at <see cref="AutoIndent.Full"/>, with the lines as <see cref="RuleLines"/> gives them in
/// their new places. Each answer is null where it has none, when matching runs out of time, or when a line it read is
/// <see cref="RuleLines.Unclassified"/>: the lines then keep their indentation.
/// </summary>
internal static class MovedIndentation
{
    /// <summary>
    /// The indentation that line <paramref name="lineNumber"/> should have where it stands: what the lines above give it
    /// by the indentation rules, or, right below the line that gives it, what Enter at that line's end gives, without
    /// the text that Enter appends; one level less when the line itself matches the decrease pattern.
    /// </summary>
    public static string? InPlace(IndentRules rules, RuleLines lines, int lineNumber)
    {
        if (!rules.IndentsByRules)
            return null;
        IndentationMatching matching = rules.StartMatching();
        Indenting indenting = rules.Indenting;
        try
        {
            if (IndentRules.Inherited(matching, lines, lineNumber, honorIntentional: true) is not { } inherited)
                return null;
            bool decreases = matching.Decreases(matching.Read(lines.Masked, lineNumber));
            if (
                inherited.Line == lineNumber - 1
                && rules.ActionFor(matching, "", matching.Read(lines.Masked, lineNumber - 1), "") is { } action
            )
            {
                string text = lines.Text(lineNumber - 1);
                string indentation = text[..Math.Max(0, IndentRules.Leading(text) - action.RemoveText)];
                indentation = action.Indent switch
                {
                    IndentAction.Indent or IndentAction.IndentOutdent => indenting.Shift(indentation),
                    IndentAction.Outdent => indenting.Unshift(indentation),
                    _ => indentation,
                };
                string entered = (decreases ? indenting.Unshift(indentation) : indentation) + action.AppendText;
                return lines.Unclassified ? null : entered[..IndentRules.Leading(entered)];
            }
            if (lines.Unclassified)
                return null;
            if (decreases)
                return inherited.Increases ? inherited.Indentation : indenting.Unshift(inherited.Indentation);
            return rules.Indentation(inherited);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indentation that Enter at the end of line <paramref name="above"/>, or of the closest line with text above
    /// it, gives line <paramref name="lineNumber"/>: that of the new line, one level less when the line matches the
    /// decrease pattern. Null also when no rule for Enter or bracket applies there, and when the line lacks the text
    /// that Enter puts on the new line, such as a doc comment's <c>*</c>.
    /// </summary>
    public static string? Entered(IndentRules rules, RuleLines lines, int lineNumber, int above)
    {
        if (!rules.IndentsByRules)
            return null;
        IndentationMatching matching = rules.StartMatching();
        try
        {
            if (rules.EnterAtEnd(matching, lines, above) is not var (action, indentation, append))
                return null;
            // What the new line starts with: between brackets, without the line that takes the text after the caret.
            string prefix = action.Indent switch
            {
                IndentAction.IndentOutdent => indentation,
                IndentAction.Outdent => rules.Indenting.Unshift(indentation) + append,
                _ => indentation + append,
            };
            string text = matching.Read(lines.Text, lineNumber);
            if (!text.TrimStart().Contains(prefix.TrimStart(), StringComparison.Ordinal))
                return null;
            string entered = prefix[..IndentRules.Leading(prefix)];
            bool decreases = matching.Decreases(matching.Read(lines.Masked, lineNumber));
            return lines.Unclassified ? null
                : decreases ? rules.Indenting.Unshift(entered)
                : entered;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
