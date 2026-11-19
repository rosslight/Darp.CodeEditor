using System.Text.RegularExpressions;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// What one indentation may cost on the UI thread: the lines it reads, the rules it matches and the lines it tokenizes,
/// on a clock that moves only when a line is read.
/// </summary>
public sealed class IndentRulesTests
{
    [Fact]
    public void Indentation_stops_between_quick_matches_once_its_time_is_up()
    {
        // Each match is quick, far within its own time limit: only the time of the whole indentation ends the scan.
        var rules = new IndentationRules(new Regex("x"), new Regex("y"));
        var time = new ManualTime();
        int reads = 0;
        var lines = new RuleLines(
            _ =>
            {
                reads++;
                time.Advance(TimeSpan.FromMilliseconds(1));
                return "a";
            },
            (_, _) => null,
            brackets: null
        );

        Should.Throw<RegexMatchTimeoutException>(() =>
            IndentRules.Inherited(new IndentationMatching(rules, time), lines, 1_000, honorIntentional: false)
        );

        reads.ShouldBe((int)IndentationRules.TimeLimit.TotalMilliseconds);
    }

    [Fact]
    public void A_blank_line_stops_looking_up_for_text_once_its_indentation_is_out_of_time()
    {
        var time = new ManualTime();
        int reads = 0;
        var lines = new RuleLines(
            _ =>
            {
                reads++;
                time.Advance(TimeSpan.FromMilliseconds(1));
                return "";
            },
            (_, _) => null,
            brackets: null
        );
        var rules = new IndentRules(LanguageConfiguration.None, AutoIndent.Advanced, Indentation.Spaces(4), "\n", time);

        rules.GoodIndentation(lines, 100_000).ShouldBeNull();

        reads.ShouldBe((int)IndentationRules.TimeLimit.TotalMilliseconds);
    }

    [Fact]
    public void The_rules_for_Enter_match_within_the_time_of_the_whole_indentation()
    {
        // Reading the line above takes all of the time: no rule matches after it, and the new line keeps the indentation.
        var time = new ManualTime();
        LanguageConfiguration configuration = LanguageConfiguration.None with
        {
            OnEnterRules = [new OnEnterRule(new Regex("x"), null, null, new EnterAction(IndentAction.Indent, "", 0))],
        };
        var lines = new RuleLines(
            number =>
            {
                if (number == 1)
                    time.Advance(IndentationRules.TimeLimit);
                return number == 1 ? "a" : "  x";
            },
            (_, _) => null,
            brackets: null
        );
        var rules = new IndentRules(configuration, AutoIndent.Advanced, Indentation.Spaces(2), "\n", time);

        rules.Enter(lines, 2, 3).ShouldBe(new NewLine("\n  ", 3, 0));
    }

    [Fact]
    public void Enter_counts_reading_the_carets_line_against_the_time_of_its_indentation()
    {
        // Reading the caret's line takes all of the time: the rule for Enter that its text matches doesn't apply.
        var time = new ManualTime();
        LanguageConfiguration configuration = LanguageConfiguration.None with
        {
            OnEnterRules = [new OnEnterRule(new Regex("x"), null, null, new EnterAction(IndentAction.Indent, "", 0))],
        };
        var lines = new RuleLines(
            _ =>
            {
                time.Advance(IndentationRules.TimeLimit);
                return "  x";
            },
            (_, _) => null,
            brackets: null
        );
        var rules = new IndentRules(configuration, AutoIndent.Advanced, Indentation.Spaces(2), "\n", time);

        rules.Enter(lines, 1, 3).ShouldBe(new NewLine("\n  ", 3, 0));
    }

    [Fact]
    public void Enter_tokenizes_the_carets_line_once()
    {
        var configuration = new LanguageConfiguration(null, null, [new CharacterPair("(", ")")], [], [], null);
        int tokenized = 0;
        var lines = new RuleLines(
            _ => "f(a, b)",
            (_, _) =>
            {
                tokenized++;
                return [0, 0];
            },
            configuration.BracketPattern
        );

        new IndentRules(configuration, AutoIndent.Full, Indentation.Spaces(4), "\n").Enter(lines, 1, 4);

        tokenized.ShouldBe(1);
    }
}
