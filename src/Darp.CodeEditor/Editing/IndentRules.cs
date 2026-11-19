using System.Text.RegularExpressions;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The indentation of lines by a language's editing rules, as far as its <see cref="AutoIndent"/> goes, ported from VS
/// Code's <c>enterAction.ts</c>, <c>autoIndent.ts</c> and the electric characters of <c>typeOperations.ts</c>: what
/// Enter inserts, the indentation that a line should have, a line that outdents as the user types, and a closing
/// bracket typed first on its line that lines up with its opening one. It sees the lines as <see cref="RuleLines"/>
/// gives them, so its callers may ask about lines that are not, or not yet, in the document.
/// </summary>
/// <remarks>
/// One call reads and matches lines, with the indentation rules and the rules for Enter, for at most
/// <see cref="IndentationRules.TimeLimit"/>, and a match for at most <see cref="IndentationRules.MatchTimeout"/>, as
/// with a pattern that backtracks on long lines: the line then keeps its indentation.
/// </remarks>
internal sealed class IndentRules
{
    private readonly LanguageConfiguration _configuration;
    private readonly AutoIndent _autoIndent;
    private readonly Indenting _indenting;
    private readonly TimeProvider _time;

    /// <param name="configuration">The language's editing rules.</param>
    /// <param name="autoIndent">How far the rules apply.</param>
    /// <param name="indentation">The document's indentation.</param>
    /// <param name="time">The clock of the time limit; by default the system's.</param>
    public IndentRules(
        LanguageConfiguration configuration,
        AutoIndent autoIndent,
        Indentation indentation,
        TimeProvider? time = null
    )
    {
        _configuration = configuration;
        _autoIndent = autoIndent;
        _indenting = new Indenting(indentation);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// What Enter inserts at <paramref name="column"/> of line <paramref name="lineNumber"/>: the rules for Enter, then
    /// the brackets, then the indentation rules, each as far as <see cref="AutoIndent"/> goes. Without any of them, the
    /// new line keeps the line's indentation up to the caret.
    /// </summary>
    /// <param name="lines">The document's lines.</param>
    /// <param name="lineNumber">The caret's line.</param>
    /// <param name="column">The caret's offset in its line.</param>
    /// <param name="lineBreak">The document's line break.</param>
    public NewLine Enter(RuleLines lines, int lineNumber, int column, string lineBreak)
    {
        if (_autoIndent == AutoIndent.None)
            return new NewLine(lineBreak, lineBreak.Length, 0);
        string text = lines.Text(lineNumber);
        string indentation = text[..Math.Min(column, Leading(text))];
        if (_autoIndent >= AutoIndent.Brackets)
        {
            try
            {
                IndentationMatching matching = StartMatching();
                string before = lines.Masked(lineNumber, 0, column);
                string after = lines.Masked(lineNumber, column, text.Length);
                string previous = lineNumber > 1 ? matching.Read(lines.Masked, lineNumber - 1) : "";
                if (ActionFor(matching, previous, before, after) is { } action)
                    return NewLineFor(action, indentation, lineBreak);
                if (IndentForEnter(matching, lines, lineNumber, before, after) is { } byRules)
                {
                    // The new line replaces the indentation of the text it takes along; on a line of whitespace only,
                    // all of the rest.
                    int firstText = Leading(text);
                    int replaced = firstText == text.Length ? text.Length - column : Math.Max(0, firstText - column);
                    string inserted = lineBreak + _indenting.Normalize(byRules);
                    return new NewLine(inserted, inserted.Length, replaced);
                }
            }
            catch (RegexMatchTimeoutException) { }
        }
        string kept = lineBreak + _indenting.Normalize(indentation);
        return new NewLine(kept, kept.Length, 0);
    }

    /// <summary>
    /// The indentation that line <paramref name="lineNumber"/> should have: by the indentation rules from the lines
    /// above at <see cref="AutoIndent.Full"/>, or else by what Enter would do at the end of the closest line above with
    /// text. Null for none, and for no indentation.
    /// </summary>
    public string? GoodIndentation(RuleLines lines, int lineNumber)
    {
        IndentationMatching matching = StartMatching();
        try
        {
            if (
                _autoIndent == AutoIndent.Full
                && _configuration.IndentationRules is not null
                && Inherited(matching, lines, lineNumber, honorIntentional: false) is { } inherited
            )
            {
                string indentation = inherited.Increases
                    ? _indenting.Shift(inherited.Indentation)
                    : inherited.Indentation;
                return indentation.Length > 0 ? _indenting.Normalize(indentation) : null;
            }
            int above = lineNumber - 1;
            while (above >= 1 && string.IsNullOrWhiteSpace(matching.Read(lines.Text, above)))
                above--;
            if (
                above < 1
                || ActionFor(
                    matching,
                    above > 1 ? matching.Read(lines.Masked, above - 1) : "",
                    matching.Read(lines.Masked, above),
                    ""
                )
                    is not { } action
            )
                return null;
            string text = lines.Text(above);
            (string kept, string append) = Resolved(action, text[..Leading(text)]);
            return kept.Length + append.Length > 0 ? kept + append : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indentation of line <paramref name="lineNumber"/> once <paramref name="typed"/> replaces its text from
    /// <paramref name="from"/> to <paramref name="to"/>, if that makes the line match the decrease pattern at
    /// <see cref="AutoIndent.Full"/>: what the lines above give it, one level less. Null when the line matched it
    /// already, keeps its indentation, when no line above has text, or when matching runs out of time.
    /// </summary>
    public string? Outdented(RuleLines lines, int lineNumber, int from, int to, char typed)
    {
        if (_autoIndent < AutoIndent.Full || _configuration.IndentationRules is null)
            return null;
        string text = lines.Text(lineNumber);
        string before = lines.Masked(lineNumber, 0, from);
        string after = lines.Masked(lineNumber, to, text.Length);
        IndentationMatching matching = StartMatching();
        try
        {
            // A line that matched it already keeps the indentation that the user gave it.
            if (
                !matching.Decreases(before + lines.Typed(lineNumber, from, typed) + after)
                || matching.Decreases(before + after)
            )
                return null;
            if (Inherited(matching, lines, lineNumber, honorIntentional: false) is not { } inherited)
                return null;
            string indentation = _indenting.Normalize(
                inherited.Increases ? inherited.Indentation : _indenting.Unshift(inherited.Indentation)
            );
            return indentation == _indenting.Normalize(text[..Math.Min(from, Leading(text))]) ? null : indentation;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indentation of line <paramref name="lineNumber"/> once <paramref name="typed"/>, the last character of an
    /// auto-closing pair's opening, replaces its text from <paramref name="from"/> to <paramref name="to"/>, first on the
    /// line, if that opens a block by the increase pattern under a line that indents only the next one, as
    /// <c>{</c> under <c>if (x)</c> does: the statement's indentation, at <see cref="AutoIndent.Full"/>. Null otherwise,
    /// also when the user indented the line otherwise.
    /// </summary>
    public string? Opened(RuleLines lines, int lineNumber, int from, int to, char typed)
    {
        if (
            _autoIndent < AutoIndent.Full
            || _configuration.IndentationRules is null
            || lineNumber <= 1
            || !_configuration.AutoClosingPairs.Any(pair => pair.Open[^1] == typed)
        )
            return null;
        string text = lines.Text(lineNumber);
        string before = lines.Masked(lineNumber, 0, from);
        string after = lines.Masked(lineNumber, to, text.Length);
        if (!string.IsNullOrWhiteSpace(before + after))
            return null;
        IndentationMatching matching = StartMatching();
        try
        {
            if (
                !matching.IndentsNextLine(matching.Read(lines.Masked, lineNumber - 1))
                || !matching.Increases(before + lines.Typed(lineNumber, from, typed) + after)
                || Inherited(matching, lines, lineNumber, honorIntentional: false) is not { } inherited
            )
                return null;
            string statement = _indenting.Normalize(inherited.Indentation);
            bool indentedByStatement =
                _indenting.Normalize(_indenting.Shift(inherited.Indentation))
                == _indenting.Normalize(text[..Leading(text)]);
            return indentedByStatement && statement != _indenting.Normalize(text[..Leading(text)]) ? statement : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The indentation of line <paramref name="lineNumber"/> once <paramref name="typed"/> goes in at
    /// <paramref name="column"/> and completes a closing bracket first on the line: that of the line of the opening
    /// bracket it matches, by <paramref name="openingLine"/>, when that is a line above with other indentation. Null
    /// otherwise, and for a bracket typed into a string, comment or regular expression. Only brackets of one character
    /// each are matched.
    /// </summary>
    public string? LinedUp(
        RuleLines lines,
        int lineNumber,
        int column,
        char typed,
        Func<CharacterPair, int?> openingLine
    )
    {
        if (
            _configuration.BracketPattern is not { } brackets
            || !_configuration.Brackets.Any(b => b.Close.EndsWith(typed))
        )
            return null;
        if (lines.Typed(lineNumber, column, typed).Length == 0)
            return null;
        string text = lines.Text(lineNumber);
        string before = text[..column] + typed;
        Match last = Match.Empty;
        for (Match next = brackets.Match(before); next.Success; next = next.NextMatch())
            last = next;
        if (!last.Success || Leading(before) < last.Index)
            return null;
        foreach (CharacterPair pair in _configuration.Brackets)
        {
            if (
                pair is { Open.Length: 1, Close.Length: 1 }
                && string.Equals(pair.Close, last.Value, StringComparison.OrdinalIgnoreCase)
                && openingLine(pair) is { } opening
            )
            {
                if (opening == lineNumber)
                    return null;
                string openingText = lines.Text(opening);
                string indentation = _indenting.Normalize(openingText[..Leading(openingText)]);
                return indentation == text[..Leading(text)] ? null : indentation;
            }
        }
        return null;
    }

    /// <summary>
    /// The indentation that the lines above give line <paramref name="lineNumber"/>, and whether it is one level more.
    /// Lines of whitespace only, and those that match the unindented pattern, give none. A line that matches the
    /// increase pattern, or the pattern that indents only the next line, gives one level more, and a line below one that
    /// indents only it gives the indentation from before. Null when no line above gives one.
    /// </summary>
    /// <param name="matching">The language's indentation rules, within the time of one indentation.</param>
    /// <param name="lines">The lines, of which every one read counts against the time.</param>
    /// <param name="lineNumber">The line to indent.</param>
    /// <param name="honorIntentional">
    /// Whether a line above that neither increases nor decreases gives its own indentation, as the user may have
    /// indented it on purpose. Otherwise the lines above it are searched for one that does.
    /// </param>
    internal static InheritedIndentation? Inherited(
        IndentationMatching matching,
        RuleLines lines,
        int lineNumber,
        bool honorIntentional
    )
    {
        if (lineNumber <= 1)
            return new InheritedIndentation("", false);
        string Read(int number) => matching.Read(lines.Text, number);
        string Rule(int number) => matching.Read(lines.Masked, number);
        bool Increases(int number) => matching.Increases(Rule(number));
        bool Decreases(int number) => matching.Decreases(Rule(number));
        bool IndentsNext(int number) => matching.IndentsNextLine(Rule(number));
        InheritedIndentation From(int number, bool increases)
        {
            string text = Read(number);
            return new InheritedIndentation(text[..Leading(text)], increases);
        }
        // The first line with text, above empty lines, takes no indentation.
        for (int prior = lineNumber - 1; prior > 0; prior--)
        {
            if (Read(prior).Length > 0)
                break;
            if (prior == 1)
                return new InheritedIndentation("", false);
        }
        int preceding = -1;
        for (int number = lineNumber - 1; number >= 1 && preceding < 0; number--)
        {
            if (!string.IsNullOrWhiteSpace(Read(number)) && !matching.IsUnIndented(Rule(number)))
                preceding = number;
        }
        if (preceding < 1)
            return null;
        if (Increases(preceding) || IndentsNext(preceding))
            return From(preceding, true);
        if (Decreases(preceding) || preceding == 1)
            return From(preceding, false);
        // Below a line that indents only it, which is read as it is.
        string above = Read(preceding - 1);
        if (!matching.Increases(above) && !matching.Decreases(above) && matching.IndentsNextLine(above))
            return From(StatementStart(preceding - 1), false);
        if (honorIntentional)
            return From(preceding, false);
        for (int number = preceding; number > 0; number--)
        {
            if (Increases(number))
                return From(number, true);
            if (IndentsNext(number))
                return From(StatementStart(number), false);
            if (Decreases(number))
                return From(number, false);
        }
        return From(1, false);

        // The first of the lines up to the given one that each indent only the next.
        int StatementStart(int number)
        {
            while (number > 1 && IndentsNext(number - 1))
                number--;
            return number;
        }
    }

    /// <summary>
    /// What Enter does by the rules for Enter, at <see cref="AutoIndent.Advanced"/>: the first rule whose expressions
    /// match the texts; and by the brackets: between an opening and a closing bracket, indent and outdent, and after an
    /// opening bracket, indent. Null when none applies.
    /// </summary>
    private EnterAction? ActionFor(IndentationMatching matching, string previous, string before, string after)
    {
        if (_autoIndent >= AutoIndent.Advanced)
        {
            foreach (OnEnterRule rule in _configuration.OnEnterRules)
            {
                if (rule.Matches(matching, previous, before, after))
                    return rule.Action;
            }
        }
        if (_autoIndent < AutoIndent.Brackets || before.Length == 0)
            return null;
        IReadOnlyList<CharacterPair> brackets = _configuration.Brackets;
        if (after.Length > 0 && brackets.Any(b => EndsWithOpening(before, b.Open) && StartsWithClosing(after, b.Close)))
            return new EnterAction(IndentAction.IndentOutdent, "", 0);
        if (brackets.Any(b => EndsWithOpening(before, b.Open)))
            return new EnterAction(IndentAction.Indent, "", 0);
        return null;
    }

    /// <summary>
    /// The new line for <paramref name="action"/> after a line indented by <paramref name="indentation"/> up to the
    /// caret: between brackets, the text after the caret goes on a line of its own, at that indentation.
    /// </summary>
    private NewLine NewLineFor(EnterAction action, string indentation, string lineBreak)
    {
        (indentation, string append) = Resolved(action, indentation);
        if (action.Indent == IndentAction.IndentOutdent)
        {
            string inner = lineBreak + _indenting.Normalize(indentation + append);
            return new NewLine(inner + lineBreak + _indenting.Normalize(indentation), inner.Length, 0);
        }
        if (action.Indent == IndentAction.Outdent)
            indentation = _indenting.Unshift(indentation);
        string inserted = lineBreak + _indenting.Normalize(indentation + append);
        return new NewLine(inserted, inserted.Length, 0);
    }

    /// <summary>
    /// The indentation for <paramref name="action"/> after a line indented by <paramref name="indentation"/>, without
    /// the characters it removes, and the text to append: a tab first to indent, which the document's indentation turns
    /// into its own.
    /// </summary>
    private static (string Indentation, string Append) Resolved(EnterAction action, string indentation)
    {
        if (action.RemoveText > 0)
            indentation = indentation[..Math.Max(0, indentation.Length - action.RemoveText)];
        string append = action.Indent switch
        {
            IndentAction.Indent => "\t" + action.AppendText,
            IndentAction.IndentOutdent when action.AppendText.Length == 0 => "\t",
            _ => action.AppendText,
        };
        return (indentation, append);
    }

    /// <summary>
    /// The indentation of a new line by the indentation rules, at <see cref="AutoIndent.Full"/>, with the caret's line
    /// as the text before the caret, <paramref name="before"/>. Null without the rules.
    /// </summary>
    private string? IndentForEnter(
        IndentationMatching matching,
        RuleLines lines,
        int lineNumber,
        string before,
        string after
    )
    {
        if (_autoIndent < AutoIndent.Full || _configuration.IndentationRules is null)
            return null;
        if (
            Inherited(matching, lines.With(lineNumber, before), lineNumber + 1, honorIntentional: true)
            is not { } inherited
        )
            return before[..Leading(before)];
        string indentation = inherited.Increases ? _indenting.Shift(inherited.Indentation) : inherited.Indentation;
        return matching.Decreases(after) ? _indenting.Unshift(indentation) : indentation;
    }

    private IndentationMatching StartMatching() => new(_configuration.IndentationRules, _time);

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

    /// <summary>The length of the whitespace that <paramref name="text"/> starts with.</summary>
    internal static int Leading(string text)
    {
        int count = 0;
        while (count < text.Length && text[count] is ' ' or '\t')
            count++;
        return count;
    }
}

/// <summary>What Enter inserts at the caret: the text, where the caret goes in it, and how much after the caret it replaces.</summary>
internal readonly record struct NewLine(string Text, int Caret, int Replaced);

/// <summary>An indentation that the lines above give, and whether it is one level more.</summary>
internal readonly record struct InheritedIndentation(string Indentation, bool Increases);
