using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>Shift+Alt+A toggles the language's block comment around the selection.</summary>
[Collection(UiFixture.Collection)]
public sealed class BlockCommentTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>The keys of the block comment: Ctrl+Shift+A on Linux.</summary>
    private static KeyModifiers BlockComment =>
        OperatingSystem.IsLinux() ? KeyModifiers.Control | KeyModifiers.Shift : KeyModifiers.Shift | KeyModifiers.Alt;

    [Theory]
    // VS Code's tests, with JSON's tokens.
    [InlineData("fi|rst", 2, 2, "fi/* | */rst")]
    [InlineData("first\n\tsecond line", 6, 0, "/* «first\n» */\tsecond line")]
    [InlineData("first", 5, 0, "/* «first» */")]
    [InlineData("/*first*/", 7, 2, "«first»")]
    [InlineData("first", 5, 2, "fi/* «rst» */")]
    [InlineData("fi/* rst */", 5, 8, "fi«rst»")]
    [InlineData("/* first*/", 9, 0, "«first»")]
    [InlineData("first\n\tsecond line", 9, 0, "/* «first\n\tse» */cond line")]
    // Another end token between the tokens around the selection: it gets a comment of its own.
    [InlineData("/* a */ b  /* c */", 8, 9, "/* a */ /* «b» */  /* c */")]
    // A start token up to its length after the caret counts.
    [InlineData("/* a */ b /* c */", 9, 9, "/* a */ b «c»")]
    [InlineData("/* a */", 4, 4, "«a»")]
    // A comment with one space: the space goes once, and the text around stays.
    [InlineData("a/* */b", 4, 4, "a|b")]
    public Task Shift_Alt_A_puts_a_block_comment_around_the_selection_or_removes_the_one_it_is_in(
        string text,
        int anchor,
        int caret,
        string expected
    ) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show(text.Replace("|", "") + "|", CodeLanguage.Json);
            window.Editor.Selection = new TextSelection(anchor, caret);
            string before = MarkedText.Of(window);

            window.Press(Key.A, BlockComment);

            MarkedText.Of(window).ShouldBe(expected);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            document.Undo();
            MarkedText.Of(window).ShouldBe(before);
        });

    [Fact]
    public Task Shift_Alt_A_uses_the_languages_block_comment() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Xml.Loaded;
            using EditorWindow window = MarkedText.Show("<a>«b»</a>", CodeLanguage.Xml);

            window.Press(Key.A, BlockComment);

            MarkedText.Of(window).ShouldBe("<a><!-- «b» --></a>");
        });

    [Fact]
    public Task Shift_Alt_A_does_nothing_in_a_language_without_a_block_comment_or_in_a_read_only_editor() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Yaml.Loaded;
            using EditorWindow yaml = MarkedText.Show("a: «b»", CodeLanguage.Yaml);
            yaml.Press(Key.A, BlockComment);
            MarkedText.Of(yaml).ShouldBe("a: «b»");

            await CodeLanguage.Json.Loaded;
            using EditorWindow json = MarkedText.Show("«1»", CodeLanguage.Json);
            json.Editor.IsReadOnly = true;
            json.Press(Key.A, BlockComment);
            MarkedText.Of(json).ShouldBe("«1»");
        });

    [Theory]
    [InlineData(false, false, KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData(true, false, KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData(false, true, KeyModifiers.Control | KeyModifiers.Shift)]
    public void By_default_Shift_Alt_A_toggles_the_block_comment_and_Ctrl_Shift_A_on_Linux(
        bool mac,
        bool linux,
        KeyModifiers modifiers
    ) =>
        LineCommandTests
            .Commands(new CodeEditorKeymap(CodeEditorKeymap.LineBindings(mac, linux)), Key.A, modifiers)
            .ShouldBe([CodeEditorCommands.BlockComment]);
}
