using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The other occurrences of the selected text, highlighted as VS Code's <c>editor.selectionHighlight</c> does them.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SelectionHighlightTests(UiFixture ui)
{
    /// <summary><c>editor.selectionHighlightBackground</c> of the tests' theme.</summary>
    private static readonly Color s_highlight = Color.Parse("#4080C0");

    private readonly UiFixture _ui = ui;

    /// <summary>The modifiers of the search bar's toggles: Alt, and Alt+Cmd on macOS.</summary>
    private static KeyModifiers Toggle =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Alt | KeyModifiers.Meta : KeyModifiers.Alt;

    [Fact]
    public Task Selecting_text_highlights_its_other_occurrences_in_the_themes_color() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("foo bar «foo» baz\nfoo");

            window.Editor.SelectionHighlights.ShouldBe([new TextRange(0, 3), new TextRange(16, 3)]);
            await Wait.UntilAsync(() => window.RenderAndCountPixels(s_highlight, new TextRange(16, 3)) > 0);
            window.RenderAndCountPixels(s_highlight, new TextRange(0, 3)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(s_highlight, new TextRange(8, 3)).ShouldBe(0);
        });

    /// <summary>
    /// As in VS Code: not for a caret, spaces and tabs, a selection longer than the limit or over several lines unless
    /// the options say so, nor with the option off.
    /// </summary>
    [Fact]
    public Task Only_a_selection_that_VS_Code_highlights_has_its_occurrences_highlighted() =>
        _ui.Run(() =>
        {
            using EditorWindow window = Show("|a  b\na  b\n");
            CodeEditor editor = window.Editor;
            editor.SelectionHighlight.ShouldBeTrue();
            editor.SelectionHighlightMaxLength.ShouldBe(200);
            editor.SelectionHighlightMultiline.ShouldBeFalse();

            editor.Selection = new TextSelection(0, 1);
            editor.SelectionHighlights.ShouldBe([new TextRange(5, 1)]);
            editor.Selection = new TextSelection(1, 3);
            editor.SelectionHighlights.ShouldBeEmpty();
            editor.Selection = new TextSelection(0, 5);
            editor.SelectionHighlights.ShouldBeEmpty();

            editor.SelectionHighlightMultiline = true;
            editor.SelectionHighlights.ShouldBe([new TextRange(5, 5)]);
            editor.SelectionHighlightMaxLength = 4;
            editor.SelectionHighlights.ShouldBeEmpty();
            editor.SelectionHighlightMaxLength = 0;
            editor.SelectionHighlights.ShouldBe([new TextRange(5, 5)]);

            editor.SelectionHighlight = false;
            editor.SelectionHighlights.ShouldBeEmpty();
            editor.SelectionHighlight = true;
            editor.Selection = new TextSelection(0);
            editor.SelectionHighlights.ShouldBeEmpty();
        });

    /// <summary>
    /// By default the occurrences match as the search bar's options say, as in VS Code, also while it is closed; or as
    /// the match mode says. The bar's own query shows its matches instead.
    /// </summary>
    [Fact]
    public Task Occurrences_match_by_the_search_options_or_the_match_mode() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("Foo «foo» FOO food");
            CodeEditor editor = window.Editor;
            editor.SelectedTextMatchMode.ShouldBe(SelectedTextMatchMode.FindOptions);
            editor.SelectionHighlights.ShouldBe([new TextRange(0, 3), new TextRange(8, 3), new TextRange(12, 3)]);

            window.Press(Key.C, Toggle);
            editor.SelectionHighlights.ShouldBe([new TextRange(12, 3)]);
            window.Press(Key.W, Toggle);
            window.Press(Key.C, Toggle);
            editor.SelectionHighlights.ShouldBe([new TextRange(0, 3), new TextRange(8, 3)]);
            editor.SelectedTextMatchMode = SelectedTextMatchMode.CaseSensitive;
            editor.SelectionHighlights.ShouldBe([new TextRange(12, 3)]);

            // The bar searches for the selection, with the same options: its matches show it.
            editor.SelectedTextMatchMode = SelectedTextMatchMode.FindOptions;
            window.Press(Key.W, Toggle);
            editor.OpenSearch();
            await Wait.UntilAsync(() => editor.SelectionHighlights.Count == 0);
        });

    /// <summary>
    /// As in VS Code, at half their color. The pixels are read between the occurrence's characters, where only the
    /// highlight draws: the characters' edges blend the text's color with the highlight's, which with some fonts comes
    /// within the tolerance of the full color.
    /// </summary>
    [Fact]
    public Task Without_the_keyboard_in_the_text_the_highlights_fade() =>
        _ui.Run(async () =>
        {
            (string text, TextSelection selection) = MarkedText.Parse("«a    a»\n\na    a");
            var other = new TextBox();
            var editor = new CodeEditor
            {
                Document = CodeDocument.Create(CodeLanguage.PlainText, text),
                ColorTheme = Theme(),
                Height = 300,
            };
            using var window = new EditorWindow(editor, new StackPanel { Children = { editor, other } });
            TextArea textArea = EditorWindow.PartOf(editor).TextArea;
            editor.Selection = selection;
            editor.SelectionHighlights.ShouldBe([new TextRange(8, 6)]);
            // The middle two of the other occurrence's spaces, a space away from its characters.
            var between = new TextRange(10, 2);
            // Once the theme is loaded; the full color needs the keyboard in the text all along.
            await Wait.UntilAsync(() => !textArea.IsFocused || window.RenderAndCountPixels(s_highlight, between) > 0);
            textArea.IsFocused.ShouldBeTrue();

            other.Focus().ShouldBeTrue();

            textArea.IsFocused.ShouldBeFalse();
            window.RenderAndCountPixels(s_highlight, between).ShouldBe(0);
            // Half of the color over the white background.
            window.RenderAndCountPixels(Color.Parse("#9FBFDF"), between).ShouldBeGreaterThan(0);
        });

    private static EditorWindow Show(string marked)
    {
        (string text, TextSelection selection) = MarkedText.Parse(marked);
        var editor = new CodeEditor
        {
            Document = CodeDocument.Create(CodeLanguage.PlainText, text),
            ColorTheme = Theme(),
        };
        var window = new EditorWindow(editor);
        editor.Selection = selection;
        return window;
    }

    private static CodeEditorColorTheme Theme() =>
        new() { Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/highlights.json") };
}
