using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The bracket next to the caret and its match, or else the pair around the caret, boxed in the color theme's bracket
/// match colors.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class BracketMatchingTests(UiFixture ui)
{
    /// <summary><c>editorBracketMatch.background</c> of the tests' theme.</summary>
    private static readonly Color s_bracketMatch = Color.Parse("#FF00FF");

    private readonly UiFixture _ui = ui;

    /// <summary>
    /// The bracket after the caret first, as in VS Code, then the one before; brackets in strings and comments don't
    /// count. The last offset is a bracket that shows no box.
    /// </summary>
    [Theory]
    [InlineData("|[1, [2]]", 0, 7, 4)]
    [InlineData("[1, [2]|]", 0, 7, 6)]
    [InlineData("[1, [2|]]", 4, 6, 0)]
    [InlineData("[\"]\", 1|]", 0, 7, 2)]
    [InlineData("[1, // ]\n2|]", 0, 10, 7)]
    public Task The_bracket_at_the_caret_and_its_match_show_in_the_themes_colors(
        string marked,
        int open,
        int close,
        int other
    ) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored(marked);

            window.RenderAndCountPixels(s_bracketMatch, new TextRange(open, 1)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(close, 1)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(other, 1)).ShouldBe(0);
        });

    /// <summary>
    /// Away from brackets, the innermost pair around the caret is boxed, as VS Code's <c>editor.matchBrackets</c> does
    /// by default; with <see cref="MatchBrackets.Near"/> only a bracket next to the caret, and with
    /// <see cref="MatchBrackets.Never"/> none.
    /// </summary>
    [Theory]
    [InlineData(MatchBrackets.Always, true, true)]
    [InlineData(MatchBrackets.Near, false, true)]
    [InlineData(MatchBrackets.Never, false, false)]
    public Task Away_from_brackets_the_pair_around_the_caret_is_boxed_as_the_option_says(
        MatchBrackets match,
        bool around,
        bool next
    ) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1, [2], |3]");
            window.Editor.Options.MatchBrackets.ShouldBe(MatchBrackets.Always);

            window.Editor.Options = CodeEditorOptions.Default with { MatchBrackets = match };

            (window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)) > 0).ShouldBe(around);
            (window.RenderAndCountPixels(s_bracketMatch, new TextRange(10, 1)) > 0).ShouldBe(around);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(4, 1)).ShouldBe(0);
            window.Editor.Selection = new TextSelection(4);
            (window.RenderAndCountPixels(s_bracketMatch, new TextRange(6, 1)) > 0).ShouldBe(next);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)).ShouldBe(0);
        });

    /// <summary>
    /// The pair around the caret is found by a bounded scan as well: a closing bracket further away than the search
    /// reads shows no pair.
    /// </summary>
    [Theory]
    [InlineData(BracketMatcher.MaxDistance - 1, true)]
    [InlineData(BracketMatcher.MaxDistance, false)]
    public Task A_pair_around_the_caret_further_away_than_the_search_reads_shows_no_box(int spaces, bool boxed) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1 |" + new string(' ', spaces) + "]");

            (window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)) > 0).ShouldBe(boxed);
        });

    /// <summary>
    /// The pair around the caret follows it into a nested pair and out of it, and stays as the caret passes pairs that
    /// close between its places.
    /// </summary>
    [Fact]
    public Task The_pair_around_the_caret_follows_it_into_nested_pairs_and_out() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1|, [2, 3], [4], 5]");
            int[] brackets = [0, 4, 9, 12, 14, 18];

            (int Caret, int Open, int Close)[] steps = [(2, 0, 18), (7, 4, 9), (16, 0, 18), (11, 0, 18), (3, 0, 18)];
            foreach ((int caret, int open, int close) in steps)
            {
                window.Editor.Selection = new TextSelection(caret);

                foreach (int bracket in brackets)
                {
                    (window.RenderAndCountPixels(s_bracketMatch, new TextRange(bracket, 1)) > 0).ShouldBe(
                        bracket == open || bracket == close,
                        $"the bracket at {bracket} with the caret at {caret}"
                    );
                }
            }
        });

    /// <summary>As in VS Code, a pair shows only for a caret without a selection.</summary>
    [Fact]
    public Task A_selection_shows_no_pair() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("|[1, 2] ");
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(5, 1)).ShouldBeGreaterThan(0);

            // The bracket selected, with the caret after it.
            window.Press(Key.Right, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 1));
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(5, 1)).ShouldBe(0);

            // A selection that leaves the caret where it was, next to a bracket.
            window.Editor.Selection = new TextSelection(6);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)).ShouldBeGreaterThan(0);
            window.Editor.Selection = new TextSelection(7, 6);
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)).ShouldBe(0);
        });

    /// <summary>The search for a match reads a bounded number of characters, so it can't stall the UI thread.</summary>
    [Theory]
    [InlineData(BracketMatcher.MaxDistance - 1, true)]
    [InlineData(BracketMatcher.MaxDistance, false)]
    public Task A_match_further_away_than_the_search_reads_shows_no_pair(int spaces, bool boxed) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("1\n[|" + new string(' ', spaces) + "]");

            (window.RenderAndCountPixels(s_bracketMatch, new TextRange(2, 1)) > 0).ShouldBe(boxed);
        });

    [Fact]
    public void A_closing_bracket_of_an_opening_one_with_several_closes_it_before_a_pair_closes()
    {
        CharacterPair[] brackets = [new("(", ")"), new("(", "]")];

        bool unmatched = BracketMatcher.UnmatchedClosing(new TextDocument("(]"), 1, brackets[1], brackets, _ => null);

        unmatched.ShouldBeFalse();
    }

    [Fact]
    public void Line_breaks_count_against_what_the_search_reads()
    {
        // Empty lines have no characters to read: each line break counts, and the search visits no line past it all.
        var document = new TextDocument("(" + new string('\n', 500_000) + ")");
        int lines = 0;

        BracketMatcher
            .FindPair(
                document,
                0,
                [new CharacterPair("(", ")")],
                _ =>
                {
                    lines++;
                    return null;
                }
            )
            .ShouldBeNull();

        lines.ShouldBeLessThanOrEqualTo(BracketMatcher.MaxDistance + 2);
    }

    [Fact]
    public Task A_pair_whose_match_becomes_a_comment_out_of_view_is_no_longer_boxed() =>
        _ui.Run(async () =>
        {
            // The closing brace below the view.
            using EditorWindow window = await ShowColored(
                "|{ \"a\": 1,\n// b\n" + string.Concat(Enumerable.Repeat("// padding\n", 50)) + "}"
            );
            window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)).ShouldBeGreaterThan(0);

            // A block comment from the second line on, to the end. The lines in view keep their tokens' colors and
            // types: comments.
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            document.Replace(new TextRange(document.CurrentSnapshot.GetOffset(new TextPosition(2, 2)), 1), "*");

            await Wait.UntilAsync(() => window.RenderAndCountPixels(s_bracketMatch, new TextRange(0, 1)) == 0);
        });

    /// <summary>
    /// Brackets pair as VS Code's parser nests them: a closing bracket that closes no open bracket of its kind is no
    /// bracket, and one that closes an outer pair leaves the brackets opened inside it unclosed. The array after them
    /// holds a number, whose color says that the lines are tokenized.
    /// </summary>
    [Theory]
    [InlineData("{ x| ] } [2]", MatchBrackets.Always, new[] { 0, 6 }, new[] { 4 })]
    [InlineData("[ |{ x ] } [2]", MatchBrackets.Near, new int[0], new[] { 0, 2, 6, 8 })]
    [InlineData("[ |{ x ] } [2]", MatchBrackets.Always, new[] { 0, 6 }, new[] { 2, 8 })]
    [InlineData("[ { x ] }| [2]", MatchBrackets.Near, new int[0], new[] { 0, 2, 6, 8 })]
    public Task In_incomplete_code_brackets_pair_as_VS_Codes_parser_nests_them(
        string marked,
        MatchBrackets match,
        int[] boxed,
        int[] unboxed
    ) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored(marked);

            window.Editor.Options = CodeEditorOptions.Default with { MatchBrackets = match };

            foreach (int bracket in boxed)
                window.RenderAndCountPixels(s_bracketMatch, new TextRange(bracket, 1)).ShouldBeGreaterThan(0);
            foreach (int bracket in unboxed)
                window.RenderAndCountPixels(s_bracketMatch, new TextRange(bracket, 1)).ShouldBe(0);
        });

    private static async Task<EditorWindow> ShowColored(string marked)
    {
        (string text, TextSelection selection) = MarkedText.Parse(marked);
        var editor = new CodeEditor
        {
            Document = CodeDocument.Create(CodeLanguage.Json, text),
            ColorTheme = new CodeEditorColorTheme
            {
                Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
            },
        };
        var window = new EditorWindow(editor);
        editor.Selection = selection;
        // The numbers in the theme's color: the grammar and the theme are loaded, and the lines in view tokenized.
        await Wait.UntilAsync(() => window.RenderAndCountPixels(Color.Parse("#098658")) > 0);
        return window;
    }
}
