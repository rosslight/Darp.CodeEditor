using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>Ctrl+/ toggles comments as VS Code does, with the language's line comment or else its block comment.</summary>
[Collection(UiFixture.Collection)]
public sealed class CommentTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Theory]
    [InlineData("{\n    \"a\": |1\n}", "{\n    // \"a\": 1\n}")]
    [InlineData("«    a\n\n      b\n»c", "    // a\n\n    //   b\nc")]
    [InlineData("«    // a\n\n    //   b»", "    a\n\n      b")]
    [InlineData("«// a\nb»", "// // a\n// b")]
    [InlineData("«//a»", "a")]
    [InlineData("«\tfoo\n    bar»", "\t// foo\n    // bar")]
    [InlineData("«\tfoo\n  bar»", "// \tfoo\n  // bar")]
    public Task Ctrl_slash_comments_lines_or_uncomments_them_when_all_are_commented(string marked, string expected) =>
        _ui.Run(async () =>
        {
            // Lines of whitespace only take no part; the comment goes at the lines' smallest indentation, by its width:
            // before a tab that ends past it.
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Press(Key.Oem2, EditorWindow.Command);
            document.Text.ShouldBe(expected);

            document.Undo();
            document.Text.ShouldBe(MarkedText.Parse(marked).Text);
        });

    [Fact]
    public Task Comments_on_many_lines_are_one_change_of_the_text() =>
        _ui.Run(async () =>
        {
            // The editor's work per change, such as the highlighter's and the view's, then happens once, not per line.
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("«a\n  b\nc»", CodeLanguage.Json);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            int changes = 0;
            document.Buffer.Changed += (_, _) => changes++;

            window.Press(Key.Oem2, EditorWindow.Command);
            document.Text.ShouldBe("// a\n//   b\n// c");
            window.Press(Key.Oem2, EditorWindow.Command);

            document.Text.ShouldBe("a\n  b\nc");
            changes.ShouldBe(2);
        });

    [Theory]
    [InlineData("<a>\n  <b/>|\n</a>", "<a>\n  <!-- <b/> -->\n</a>")]
    [InlineData("«<a>\n  <b/>\n</a>»", "<!-- <a>\n  <b/>\n</a> -->")]
    [InlineData("<a>\n  <!-- <b/> -->|\n</a>", "<a>\n  <b/>\n</a>")]
    [InlineData("<a>\n  |\n</a>", "<a>\n  <!-- | -->\n</a>")]
    public Task Ctrl_slash_uses_the_block_comment_in_a_language_without_a_line_comment(
        string marked,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Xml.Loaded;
            using EditorWindow window = MarkedText.Show(marked, CodeLanguage.Xml);

            window.Press(Key.Oem2, EditorWindow.Command);

            string result = MarkedText.Of(window);
            (expected.Contains('|') ? result : MarkedText.Parse(result).Text).ShouldBe(expected);
        });
}
