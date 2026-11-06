using Avalonia.Media;

namespace Darp.CodeEditor.Tests;

/// <summary>Spaces and tabs show inside the selection, in the color theme's whitespace color, and nowhere else.</summary>
[Collection(UiFixture.Collection)]
public sealed class WhitespaceTests(UiFixture ui)
{
    /// <summary><c>editorWhitespace.foreground</c> of the tests' theme.</summary>
    private static readonly Color s_whitespace = Color.Parse("#FF0000");

    private readonly UiFixture _ui = ui;

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public Task Whitespace_shows_inside_the_selection_only(string whitespace) =>
        _ui.Run(async () =>
        {
            string line = $"a{whitespace}b{whitespace}c";
            var editor = new CodeEditor
            {
                Document = CodeDocument.Create(CodeLanguage.PlainText, $"{line}\n{line}"),
                ColorTheme = new CodeEditorColorTheme
                {
                    Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
                },
            };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => window.RenderAndCountPixels(Colors.Black) > 0);
            window.RenderAndCountPixels(s_whitespace).ShouldBe(0);

            // From the first line's "b" to the second line's "b".
            editor.Selection = new TextSelection(2, line.Length + 3);

            window.RenderAndCountPixels(s_whitespace, new TextRange(1, 1)).ShouldBe(0);
            window.RenderAndCountPixels(s_whitespace, new TextRange(3, 1)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_whitespace, new TextRange(line.Length + 2, 1)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_whitespace, new TextRange(line.Length + 4, 1)).ShouldBe(0);
        });
}
