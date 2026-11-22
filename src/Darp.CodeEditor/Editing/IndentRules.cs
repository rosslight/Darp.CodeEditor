using System.Text.RegularExpressions;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Editing;

/// <summary>
/// The indentation of lines by a language's editing rules, as far as its <see cref="AutoIndent"/> goes, ported from VS
/// Code's <c>enterAction.ts</c>, <c>autoIndent.ts</c> and the electric characters of <c>typeOperations.ts</c>: what
/// Enter inserts, by what <see cref="EnterActions"/> gives, and a line that outdents as the user types. It sees the
/// lines as <see cref="RuleLines"/> gives them, so its callers may ask about a line as it is up to the caret.
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
    private readonly EnterActions _enter;
    private readonly Indenting _indenting;
    private readonly string _lineBreak;
    private readonly TimeProvider _time;

    /// <summary>The rules of a document's language, its indentation and line break, and the clock of the time limit.</summary>
    public IndentRules(
        LanguageConfiguration configuration,
        AutoIndent autoIndent,
        Indentation indentation,
        string lineBreak,
        TimeProvider? time = null
    )
    {
        _configuration = configuration;
        _autoIndent = autoIndent;
        _enter = new EnterActions(configuration, autoIndent, lineBreak);
        _indenting = new Indenting(indentation);
        _lineBreak = lineBreak;
        _time = time ?? IndentationRules.Clock;
    }

    /// <summary>
    /// What Enter inserts at the caret, <paramref name="column"/> characters into line <paramref name="lineNumber"/>: the
    /// rules for Enter, then the brackets, then the indentation rules, each as far as <see cref="AutoIndent"/> goes.
    /// Without any of them, the new line keeps the line's indentation up to the caret.
    /// </summary>
    public NewLine Enter(RuleLines lines, int lineNumber, int column)
    {
        if (_autoIndent == AutoIndent.None)
            return new NewLine(_lineBreak, _lineBreak.Length, 0);
        // The line is read out of time too, for its indentation to keep, but its time counts.
        IndentationMatching matching = StartMatching();
        string text = lines.Text(lineNumber);
        string indentation = text[..Math.Min(column, Leading(text))];
        if (_autoIndent >= AutoIndent.Brackets)
        {
            try
            {
                (_, string before, string after) = Around(matching, lines, lineNumber, column, column);
                string previous = lineNumber > 1 ? matching.Read(lines.Masked, lineNumber - 1) : "";
                if (_enter.For(matching, previous, before, after, indentation) is { } enter)
                    return NewLineFor(enter);
                if (IndentForEnter(matching, lines, lineNumber, before, after) is { } byRules)
                {
                    // The new line replaces the indentation of the text it takes along; on a line of whitespace only,
                    // all of the rest. A caret before the line's text keeps its column, within the new indentation.
                    int firstText = Leading(text);
                    string indented = _indenting.Normalize(byRules);
                    string inserted = _lineBreak + indented;
                    if (firstText == text.Length)
                        return new NewLine(inserted, inserted.Length, text.Length - column);
                    int caret = _indenting.Length(_indenting.Width(text.AsSpan(0, Math.Min(column, firstText))));
                    return new NewLine(
                        inserted,
                        column <= firstText ? _lineBreak.Length + Math.Min(caret, indented.Length) : inserted.Length,
                        Math.Max(0, firstText - column)
                    );
                }
            }
            catch (RegexMatchTimeoutException) { }
        }
        string kept = _lineBreak + _indenting.Normalize(indentation);
        return new NewLine(kept, kept.Length, 0);
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
        IndentationMatching matching = StartMatching();
        try
        {
            (string text, string before, string after) = Around(matching, lines, lineNumber, from, to);
            // A line that matched it already keeps the indentation that the user gave it.
            if (
                !matching.Decreases(before + matching.Read(() => lines.Typed(lineNumber, from, typed)) + after)
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
        IndentationMatching matching = StartMatching();
        try
        {
            (string text, string before, string after) = Around(matching, lines, lineNumber, from, to);
            if (
                !string.IsNullOrWhiteSpace(before + after)
                || !matching.IndentsNextLine(matching.Read(lines.Masked, lineNumber - 1))
                || !matching.Increases(before + matching.Read(() => lines.Typed(lineNumber, from, typed)) + after)
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
    /// The indentation that the lines above give line <paramref name="lineNumber"/>, and whether it is one level more.
    /// Lines of whitespace only, and those that match the unindented pattern, give none. A line that matches the
    /// increase pattern, or the pattern that indents only the next line, gives one level more, and a line below one that
    /// indents only it gives the indentation from before. With <paramref name="honorIntentional"/>, a line that neither
    /// increases nor decreases gives its own, as the user may have indented it on purpose; otherwise the lines above it
    /// are searched for one that does. Null when no line above gives one.
    /// </summary>
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
    /// The new line for <paramref name="enter"/>: between brackets, the text after the caret goes on a line of its own,
    /// at the indentation that Enter keeps.
    /// </summary>
    private NewLine NewLineFor(CompleteEnterAction enter)
    {
        string indentation = enter.Indentation;
        if (enter.Action.Indent == IndentAction.IndentOutdent)
        {
            string inner = _lineBreak + _indenting.Normalize(indentation + enter.Append);
            return new NewLine(inner + _lineBreak + _indenting.Normalize(indentation), inner.Length, 0);
        }
        if (enter.Action.Indent == IndentAction.Outdent)
            indentation = _indenting.Unshift(indentation);
        string inserted = _lineBreak + _indenting.Normalize(indentation + enter.Append);
        return new NewLine(inserted, inserted.Length, 0);
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
        return matching.Decreases(after) ? _indenting.Unshift(Indentation(inherited)) : Indentation(inherited);
    }

    private string Indentation(InheritedIndentation inherited) =>
        inherited.Increases ? _indenting.Shift(inherited.Indentation) : inherited.Indentation;

    private IndentationMatching StartMatching() => new(_configuration.IndentationRules, _time);

    /// <summary>
    /// Line <paramref name="lineNumber"/>, and its parts before <paramref name="from"/> and after <paramref name="to"/>
    /// as the rules see them, read within the time of the indentation.
    /// </summary>
    private static (string Text, string Before, string After) Around(
        IndentationMatching matching,
        RuleLines lines,
        int lineNumber,
        int from,
        int to
    )
    {
        string text = matching.Read(lines.Text, lineNumber);
        return (
            text,
            matching.Read(() => lines.Masked(lineNumber, 0, from)),
            matching.Read(() => lines.Masked(lineNumber, to, text.Length))
        );
    }

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
