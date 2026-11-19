using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The steps that undo takes back, as VS Code puts its undo stops: typing goes on in the step of the typing before it,
/// and a space, Enter, a pair that closes, deleting, a caret move or another edit start the next one.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class UndoTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Undo_takes_back_what_was_typed_after_a_paste_at_once_and_redo_brings_it_back() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            await (window.Window.Clipboard ?? throw new InvalidOperationException("No clipboard.")).SetTextAsync("abc");
            window.Editor.Paste();
            await Wait.UntilAsync(() => DocumentOf(window).Text == "abc");

            window.Type("def");
            window.Press(Key.Z, EditorWindow.Command);
            MarkedText.Of(window).ShouldBe("abc|");

            DocumentOf(window).Redo();
            MarkedText.Of(window).ShouldBe("abcdef|");
        });

    [Fact]
    public Task A_space_starts_a_step_that_the_word_after_it_goes_on_in_and_more_spaces_end_it() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);

            window.Type("abc def  g");

            UndoSteps(window).ShouldBe(["abc def  |", "abc def|", "abc|", "|"]);
        });

    [Fact]
    public Task A_caret_move_starts_a_step() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);

            window.Type("ab");
            window.Press(Key.Left);
            window.Type("c");

            MarkedText.Of(window).ShouldBe("ac|b");
            UndoSteps(window).ShouldBe(["a|b", "|"]);
        });

    [Fact]
    public Task Backspaces_in_a_row_are_one_step_and_one_that_joins_lines_starts_another() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("ab\ncd|", CodeLanguage.PlainText);

            window.Type("e");
            for (int i = 0; i < 5; i++)
                window.Press(Key.Back);

            MarkedText.Of(window).ShouldBe("a|");
            UndoSteps(window).ShouldBe(["ab\n|", "ab\ncde|", "ab\ncd|"]);
        });

    [Fact]
    public Task Deletes_in_a_row_are_one_step() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|abc", CodeLanguage.PlainText);

            window.Press(Key.Delete);
            window.Press(Key.Delete);

            MarkedText.Of(window).ShouldBe("|c");
            UndoSteps(window).ShouldBe(["|abc"]);
        });

    [Fact]
    public Task Enter_starts_a_step_that_the_typing_after_it_goes_on_in() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);

            window.Type("ab");
            window.Press(Key.Enter);
            window.Type("cd");

            UndoSteps(window).ShouldBe(["ab|", "|"]);
        });

    [Fact]
    public Task A_pair_that_closes_starts_a_step_that_the_typing_inside_goes_on_in() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[1, |]", CodeLanguage.Json);

            window.Type("[2");
            MarkedText.Of(window).ShouldBe("[1, [2|]]");
            window.Type("]");

            UndoSteps(window).ShouldBe(["[1, |]"]);
            DocumentOf(window).Redo();
            MarkedText.Of(window).ShouldBe("[1, [2]|]");
        });

    [Fact]
    public Task A_pair_of_several_characters_closes_in_a_step_of_its_own() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("<a b=\"c\">\n|\n</a>", CodeLanguage.Xml);

            window.Type("<!--");
            MarkedText.Of(window).ShouldBe("<a b=\"c\">\n<!--|-->\n</a>");

            // The last character of the opening starts a step, and its closing one more.
            UndoSteps(window)
                .ShouldBe(["<a b=\"c\">\n<!--|\n</a>", "<a b=\"c\">\n<!-|\n</a>", "<a b=\"c\">\n|\n</a>"]);
        });

    [Fact]
    public Task Undo_restores_the_selection_before_the_step_and_redo_the_caret_after_it() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = await ShowColored("[«1», ]", CodeLanguage.Json);

            window.Type("x");
            MarkedText.Of(window).ShouldBe("[x|, ]");
            window.Press(Key.End);
            window.Press(Key.Left);
            window.Type("[");
            MarkedText.Of(window).ShouldBe("[x, [|]]");

            CodeDocument document = DocumentOf(window);
            document.Undo();
            MarkedText.Of(window).ShouldBe("[x, |]");
            document.Undo();
            MarkedText.Of(window).ShouldBe("[«1», ]");
            document.Redo();
            MarkedText.Of(window).ShouldBe("[x|, ]");
            document.Redo();
            MarkedText.Of(window).ShouldBe("[x, [|]]");
        });

    [Fact]
    public Task An_edit_from_elsewhere_ends_the_step_that_typing_goes_on_in() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|xy", CodeLanguage.PlainText);
            CodeDocument document = DocumentOf(window);

            window.Type("ab");
            // After the caret, which stays.
            document.Replace(new TextRange(4, 0), "!");
            window.Type("c");

            MarkedText.Of(window).ShouldBe("abc|xy!");
            UndoSteps(window).ShouldBe(["ab|xy!", "ab|xy", "|xy"]);
        });

    /// <summary>Undoes every step, and the marked text after each.</summary>
    private static List<string> UndoSteps(EditorWindow window)
    {
        CodeDocument document = DocumentOf(window);
        var steps = new List<string>();
        while (document.CanUndo)
        {
            document.Undo();
            steps.Add(MarkedText.Of(window));
        }
        return steps;
    }

    private static CodeDocument DocumentOf(EditorWindow window) =>
        window.Editor.Document ?? throw new InvalidOperationException("No document.");

    private static async Task<EditorWindow> ShowColored(string marked, CodeLanguage language)
    {
        EditorWindow window = MarkedText.Show(marked, language);
        await Wait.UntilAsync(() =>
        {
            window.Render();
            return window.RenderedColors().Count > 1;
        });
        return window;
    }
}
