using AvaloniaEdit.Document;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// What Enter does by a language's rules for Enter and its brackets, as far as its <see cref="AutoIndent"/> goes, ported
/// from VS Code's <c>onEnter.ts</c> and <c>enterAction.ts</c>: at <see cref="AutoIndent.Advanced"/>, the first rule
/// whose expressions match the texts; at <see cref="AutoIndent.Brackets"/>, between an opening and a closing bracket,
/// indent and outdent, and after an opening bracket, indent. <see cref="IndentRules"/> asks it, for Enter and for the
/// lines whose indentation follows Enter's.
/// </summary>
internal sealed class EnterActions(LanguageConfiguration configuration, AutoIndent autoIndent, string lineBreak)
{
    /// <summary>
    /// What Enter does between <paramref name="before"/> and <paramref name="after"/>, on a line below
    /// <paramref name="previous"/>, as the rules see them, and indented by <paramref name="indentation"/> up to the
    /// caret. Null when no rule or bracket applies.
    /// </summary>
    public CompleteEnterAction? For(
        IndentationMatching matching,
        string previous,
        string before,
        string after,
        string indentation
    )
    {
        if (ActionFor(matching, previous, before, after) is not { } action)
            return null;
        if (action.RemoveText > 0)
            indentation = indentation[..Math.Max(0, indentation.Length - action.RemoveText)];
        string append = action.Indent switch
        {
            IndentAction.Indent => "\t" + action.AppendText,
            IndentAction.IndentOutdent when action.AppendText.Length == 0 => "\t",
            _ => action.AppendText,
        };
        return new CompleteEnterAction(action, indentation, TextUtilities.NormalizeNewLines(append, lineBreak));
    }

    private EnterAction? ActionFor(IndentationMatching matching, string previous, string before, string after)
    {
        if (autoIndent >= AutoIndent.Advanced)
        {
            foreach (OnEnterRule rule in configuration.OnEnterRules)
            {
                if (rule.Matches(matching, previous, before, after))
                    return rule.Action;
            }
        }
        if (autoIndent < AutoIndent.Brackets || before.Length == 0)
            return null;
        IReadOnlyList<CharacterPair> brackets = configuration.Brackets;
        if (after.Length > 0 && brackets.Any(b => EndsWithOpening(before, b.Open) && StartsWithClosing(after, b.Close)))
            return new EnterAction(IndentAction.IndentOutdent, "", 0);
        if (brackets.Any(b => EndsWithOpening(before, b.Open)))
            return new EnterAction(IndentAction.Indent, "", 0);
        return null;
    }

    /// <summary>
    /// Whether <paramref name="text"/> ends with <paramref name="open"/> and whitespace: a bracket that starts with a
    /// word character, such as <c>begin</c>, starts a word.
    /// </summary>
    private static bool EndsWithOpening(string text, string open)
    {
        string trimmed = text.TrimEnd();
        int start = trimmed.Length - open.Length;
        return trimmed.EndsWith(open, StringComparison.Ordinal)
            && (!IsAsciiWordCharacter(open[0]) || start == 0 || !IsAsciiWordCharacter(trimmed[start - 1]));
    }

    /// <summary>Whether <paramref name="text"/> starts with whitespace and <paramref name="close"/>, likewise.</summary>
    private static bool StartsWithClosing(string text, string close)
    {
        string trimmed = text.TrimStart();
        return trimmed.StartsWith(close, StringComparison.Ordinal)
            && (
                !IsAsciiWordCharacter(close[^1])
                || close.Length == trimmed.Length
                || !IsAsciiWordCharacter(trimmed[close.Length])
            );
    }

    /// <summary>A word character of JavaScript's regular expressions, as the rules' <c>\b</c> means it.</summary>
    private static bool IsAsciiWordCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

/// <summary>
/// What Enter does: the rule's or bracket's <see cref="EnterAction"/>, the indentation it keeps, without the characters
/// it removes, and the text it appends: a tab first to indent, which the document's indentation turns into its own, and
/// the document's line breaks.
/// </summary>
internal readonly record struct CompleteEnterAction(EnterAction Action, string Indentation, string Append);
