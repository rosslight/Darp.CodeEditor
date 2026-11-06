using Avalonia.Media;
using Darp.CodeEditor.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>The bracket next to the caret and its match, boxed in the color theme's bracket match colors.</summary>
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

    [Fact]
    public Task Away_from_brackets_nothing_is_boxed() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1, 2|, 3]");

            window.RenderAndCountPixels(s_bracketMatch).ShouldBe(0);
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
