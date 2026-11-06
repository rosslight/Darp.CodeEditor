using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Search and replace: the bar, its keys, and its limits, which it reports, and which keep Replace All from acting on
/// a partial result.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SearchTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task OpenSearch_searches_for_the_selection_and_selects_matches_as_the_user_types() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("one «two» one\nthree one", CodeLanguage.PlainText);
            SearchBar bar = BarOf(window);

            window.Editor.OpenSearch();

            bar.IsOpen.ShouldBeTrue();
            bar.FindBox.Text.ShouldBe("two");
            bar.FindBox.IsFocused.ShouldBeTrue();
            await Wait.UntilAsync(() => bar.Count == "1 of 1");

            // The first match from where the search started.
            window.Type("one");
            await Wait.UntilAsync(() => bar.Count == "2 of 3");
            window.Editor.Selection.ShouldBe(new TextSelection(8, 11));

            // The text changed: the bar searches again.
            window.Editor.Document.ShouldNotBeNull().Replace(new TextRange(0, 0), "one ");
            await Wait.UntilAsync(() => bar.Count == "3 of 4");
        });

    [Fact]
    public Task The_keys_open_the_bars_go_through_the_matches_and_close() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a b a", CodeLanguage.PlainText);
            SearchBar bar = BarOf(window);
            KeyGesture find = ApplicationCommands.Find.Gesture;
            KeyGesture replace = ApplicationCommands.Replace.Gesture;

            window.Press(find.Key, find.KeyModifiers);
            bar.IsOpen.ShouldBeTrue();
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeFalse();
            window.Type("a");
            await Wait.UntilAsync(() => bar.Count == "1 of 3");

            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));
            window.Press(Key.Enter);
            window.Editor.Selection.ShouldBe(new TextSelection(8, 9));
            window.Press(Key.F3, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));

            window.Press(replace.Key, replace.KeyModifiers);
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeTrue();

            window.Press(Key.Escape);
            bar.IsOpen.ShouldBeFalse();
            EditorWindow.PartOf(window.Editor).TextArea.IsFocused.ShouldBeTrue();

            // F3 opens the bar for the selection, and goes to the next match, with the keyboard in the text.
            window.Editor.Selection = new TextSelection(2, 3);
            window.Press(Key.F3);
            await Wait.UntilAsync(() => window.Editor.Selection == new TextSelection(6, 7));
            bar.FindBox.Text.ShouldBe("b");
            EditorWindow.PartOf(window.Editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task Replace_replaces_the_selected_match_and_Replace_All_the_rest_in_one_undo_step() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|one=1 two=2 three=3", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            SearchBar bar = BarOf(window);

            window.Editor.OpenSearch(replace: true);
            bar.RegexToggle.IsChecked = true;
            window.Type("(\\w+)=(\\d)");
            bar.ReplaceBox.Text = "$2:$1";
            await Wait.UntilAsync(() => bar.Count == "1 of 3" && bar.ReplaceAllButton.IsEnabled);

            Click(bar, "Replace");
            document.Text.ShouldBe("1:one two=2 three=3");
            window.Editor.Selection.ShouldBe(new TextSelection(6, 11));

            await Wait.UntilAsync(() => bar.ReplaceAllButton.IsEnabled);
            Click(bar, "Replace All");
            document.Text.ShouldBe("1:one 2:two 3:three");

            document.Undo();
            document.Text.ShouldBe("1:one two=2 three=3");
        });

    [Fact]
    public Task A_read_only_editor_opens_the_search_bar_without_replacing() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = true;

            window.Editor.OpenSearch(replace: true);

            BarOf(window).IsOpen.ShouldBeTrue();
            BarOf(window).ReplaceBox.IsEffectivelyVisible.ShouldBeFalse();
        });

    [Fact]
    public Task Matches_show_in_the_themes_find_match_colors() =>
        _ui.Run(async () =>
        {
            var editor = new CodeEditor
            {
                Document = CodeDocument.Create(CodeLanguage.PlainText, "abc xyz abc"),
                ColorTheme = new CodeEditorColorTheme
                {
                    Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
                },
            };
            using var window = new EditorWindow(editor);
            editor.Selection = new TextSelection(0, 3);

            editor.OpenSearch();
            await Wait.UntilAsync(() => BarOf(window).Count == "1 of 2");

            // The selected match in the current match's color, once the theme is loaded, the other in the highlight
            // color.
            await Wait.UntilAsync(() => window.RenderAndCountPixels(Color.Parse("#00C000"), new TextRange(0, 3)) > 0);
            window.RenderAndCountPixels(Color.Parse("#0000FF"), new TextRange(8, 3)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(Color.Parse("#0000FF"), new TextRange(4, 3)).ShouldBe(0);
        });

    [Fact]
    public Task A_search_that_finds_more_matches_than_it_keeps_says_so_and_refuses_Replace_All() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Repeat("a\n", TextSearch.MaxMatches + 1));
            using EditorWindow window = MarkedText.Show("|" + text, CodeLanguage.PlainText);
            SearchBar bar = BarOf(window);

            window.Editor.OpenSearch(replace: true);
            bar.ReplaceBox.Text = "b";
            window.Type("a");
            await Wait.UntilAsync(() => bar.Message is not null);

            bar.Message.ShouldNotBeNull().ShouldContain("Replace All is off");
            bar.Count.ShouldNotBeNull().ShouldEndWith("+");
            bar.ReplaceAllButton.IsEnabled.ShouldBeFalse();
            Click(bar, "Replace All");
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe(text);
        });

    [Fact]
    public Task A_pathological_regular_expression_stops_with_a_message_and_Replace_All_is_refused() =>
        _ui.Run(async () =>
        {
            // The prototype's search froze for 21 s on this expression and line.
            string text = new string('a', 29) + "!";
            using EditorWindow window = MarkedText.Show("|" + text, CodeLanguage.PlainText);
            SearchBar bar = BarOf(window);
            window.Editor.OpenSearch(replace: true);
            bar.RegexToggle.IsChecked = true;
            bar.ReplaceBox.Text = "b";

            window.Type("(a+)+$");
            // Not a time limit for the test: a guard against a search that never returns.
            await Wait.UntilAsync(() => bar.Message is not null);

            bar.Message.ShouldNotBeNull().ShouldContain("stopped");
            bar.ReplaceAllButton.IsEnabled.ShouldBeFalse();
            Click(bar, "Replace All");
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe(text);
        });

    private static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();

    /// <summary>Clicks a button of the bar, also a disabled one, as a command could.</summary>
    private static void Click(SearchBar bar, string content) =>
        bar.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => button.Content as string == content)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
