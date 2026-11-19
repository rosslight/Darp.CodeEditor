using Avalonia.Input;
using Keys = Avalonia.Input.Key;

namespace Darp.CodeEditor.Tests;

/// <summary>Ctrl+Shift+\ goes to the matching bracket, and Select to Bracket selects a pair, as VS Code's do.</summary>
[Collection(UiFixture.Collection)]
public sealed class BracketJumpTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Theory]
    [InlineData("[1, |[2]]", "[1, [2|]]")]
    [InlineData("[1, [2|]]", "[1, |[2]]")]
    [InlineData("[1, [2]|]", "|[1, [2]]")]
    [InlineData("|[1, [2]]", "[1, [2]|]")]
    [InlineData("[1|, [2]]", "[1, [2]|]")]
    [InlineData("[1, [|]]", "[1, |[]]")]
    [InlineData("{\"a\": 1|} [2]", "|{\"a\": 1} [2]")]
    [InlineData("1, |2 [3]", "1, 2 |[3]")]
    [InlineData("1, «2» 3", "1, |2 3")]
    [InlineData("[\"]\", |1]", "[\"]\", 1|]")]
    [InlineData("{ x| ] }", "{ x ] |}")]
    [InlineData("[ |{ x ] }", "[ { x |] }")]
    public Task Ctrl_Shift_backslash_goes_to_the_matching_bracket_or_else_to_the_end_of_the_pair_around_or_the_next_bracket(
        string marked,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            // Brackets in strings don't count, nor a closing one that closes none; without any, the caret goes to the
            // selection's start. An opening bracket that an outer pair's closing one closes has no match.
            using EditorWindow window = await ShowColored(marked);

            window.Press(Keys.OemPipe, EditorWindow.Command | KeyModifiers.Shift);

            MarkedText.Of(window).ShouldBe(expected);
        });

    [Fact]
    public Task Ctrl_Shift_backslash_goes_back_and_forth_between_a_pair() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1, [2|, 3]]");

            window.Press(Keys.OemPipe, EditorWindow.Command | KeyModifiers.Shift);
            MarkedText.Of(window).ShouldBe("[1, [2, 3|]]");
            window.Press(Keys.OemPipe, EditorWindow.Command | KeyModifiers.Shift);
            MarkedText.Of(window).ShouldBe("[1, |[2, 3]]");
            window.Press(Keys.OemPipe, EditorWindow.Command | KeyModifiers.Shift);
            MarkedText.Of(window).ShouldBe("[1, [2, 3|]]");
        });

    [Theory]
    [InlineData(3, 3, 3, 9)]
    [InlineData(5, 5, 3, 9)]
    [InlineData(9, 9, 9, 3)]
    [InlineData(1, 1, 3, 9)]
    public Task Select_to_bracket_selects_the_pair_next_to_the_caret_or_around_it_or_the_next_one(
        int anchor,
        int caret,
        int selectedAnchor,
        int selectedCaret
    ) =>
        _ui.Run(async () =>
        {
            // From a closing bracket, backward.
            using EditorWindow window = await ShowColored("1, |[2, 3]");
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+alt+b", CodeEditorCommands.SelectToBracket)
            );
            window.Editor.Selection = new TextSelection(anchor, caret);

            window.Press(Keys.B, KeyModifiers.Control | KeyModifiers.Alt);

            window.Editor.Selection.ShouldBe(new TextSelection(selectedAnchor, selectedCaret));
        });

    /// <summary>Without a pair there is nothing to select: the key goes on.</summary>
    [Fact]
    public Task Select_to_bracket_without_a_pair_leaves_its_key_to_the_binding_before() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("1, |2 3");
            window.Editor.Keymap = CodeEditorKeymap.Empty.With(
                new CodeEditorKeyBinding("ctrl+alt+b", CodeEditorCommands.CursorWordEndRight),
                new CodeEditorKeyBinding("ctrl+alt+b", CodeEditorCommands.SelectToBracket)
            );

            window.Press(Keys.B, KeyModifiers.Control | KeyModifiers.Alt);

            MarkedText.Of(window).ShouldBe("1, 2| 3");
        });

    [Theory]
    [InlineData(false, KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(true, KeyModifiers.Meta | KeyModifiers.Shift)]
    public void By_default_Ctrl_Shift_backslash_goes_to_the_matching_bracket_and_Select_to_Bracket_has_no_key(
        bool mac,
        KeyModifiers modifiers
    )
    {
        var keymap = new CodeEditorKeymap(CodeEditorKeymap.DefaultBindings(mac));

        LineCommandTests.Commands(keymap, Keys.OemPipe, modifiers).ShouldBe([CodeEditorCommands.JumpToBracket]);
        keymap.Bindings.ShouldNotContain(binding => binding.Command == CodeEditorCommands.SelectToBracket);
    }

    private static async Task<EditorWindow> ShowColored(string marked)
    {
        await CodeLanguage.Json.Loaded;
        EditorWindow window = MarkedText.Show(marked, CodeLanguage.Json);
        await Wait.UntilAsync(() =>
        {
            window.Render();
            return window.RenderedColors().Count > 1;
        });
        return window;
    }
}
