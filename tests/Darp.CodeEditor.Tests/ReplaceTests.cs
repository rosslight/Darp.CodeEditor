using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.SearchBars;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Replace and Replace All from the search bar, as VS Code's: each one undo step, with the replacement's substitutions
/// and the case of the match, also beyond the matches highlighted, and only where the text may change.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class ReplaceTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Replace_replaces_the_selected_match_and_Replace_All_the_rest_in_one_undo_step() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|one=1 two=2 three=3", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            window.Type("(\\w+)=(\\d)");
            bar.ReplaceBox.Text = "$2:$1";
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();

            Click(bar, "Replace");
            document.Text.ShouldBe("1:one two=2 three=3");
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(6, 11));

            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();
            Click(bar, "Replace All");
            await SearchedAsync(bar);
            document.Text.ShouldBe("1:one 2:two 3:three");

            document.Undo();
            document.Text.ShouldBe("1:one two=2 three=3");
        });

    /// <summary>
    /// As in VS Code, Replace goes on from the end of the replacement, in the text as it is after it: a match that the
    /// replacement ended, such as one that a lookbehind found, is not selected.
    /// </summary>
    [Fact]
    public Task Replace_goes_on_from_the_end_of_the_replacement_in_the_text_as_it_is_now() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|aaa aa", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            bar.ReplaceBox.Text = "b";
            bar.FindBox.Text = "(?<=a)a";
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();

            Click(bar, "Replace");

            document.Text.ShouldBe("aba aa");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");
            window.Editor.Selection.ShouldBe(new TextSelection(5, 6));
        });

    /// <summary>
    /// Replace replaces a match that F3 reached beyond those highlighted, with its own captures, and goes on to the
    /// next one.
    /// </summary>
    [Fact]
    public Task Replace_replaces_a_selected_match_beyond_those_highlighted() =>
        _ui.Run(async () =>
        {
            const int Count = TextSearch.MaxMatches + 2;
            string text = string.Concat(Enumerable.Repeat("a1\n", Count));
            using EditorWindow window = MarkedText.Show("|" + text, CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            bar.ReplaceBox.Text = "$2$1";
            bar.FindBox.Text = @"(a)(\d)";
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 19,999+");
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();
            window.Editor.Selection = Match(TextSearch.MaxMatches);

            Click(bar, "Replace");

            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(Match(TextSearch.MaxMatches + 1));
            document.Text.Substring(TextSearch.MaxMatches * 3, 5).ShouldBe("1a\na1");

            static TextSelection Match(int index) => new(index * 3, index * 3 + 2);
        });

    /// <summary>
    /// As in VS Code, the replace bar doesn't open in a read-only editor, and an editor that becomes read-only hides
    /// the replace row, which stays hidden once the editor can be edited again.
    /// </summary>
    [Fact]
    public Task A_read_only_editor_never_shows_the_replace_row() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one", CodeLanguage.PlainText);
            KeyGesture replace = ApplicationCommands.Replace.Gesture;
            window.Editor.IsReadOnly = true;

            window.Press(replace.Key, replace.KeyModifiers);
            window.Editor.OpenSearch(replace: true);
            window.Editor.GetVisualDescendants().OfType<SearchBar>().ShouldBeEmpty();

            window.Editor.IsReadOnly = false;
            window.Press(replace.Key, replace.KeyModifiers);
            SearchBar bar = BarOf(window);
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeTrue();
            window.Editor.IsReadOnly = true;
            bar.IsOpen.ShouldBeTrue();
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeFalse();
            window.Editor.IsReadOnly = false;
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeFalse();
        });

    /// <summary>
    /// As VS Code, the bar highlights only so many matches, and says so in the tip of the count, but Replace All
    /// replaces all of them, in one undo step.
    /// </summary>
    [Fact]
    public Task Replace_All_replaces_also_the_matches_beyond_those_highlighted() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Repeat("a\n", TextSearch.MaxMatches + 2));
            using EditorWindow window = MarkedText.Show("|" + text, CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();

            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.ReplaceBox.Text = "b";
            window.Type("a");
            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();

            bar.Count.ShouldBe("1 of 19,999+");
            bar.CountTip.ShouldBe(
                "Only the first 19,999 matches are highlighted, but Replace All replaces all of them."
            );
            bar.Message.ShouldBeNull();
            Click(bar, "Replace All");
            await SearchedAsync(bar);
            document.Text.ShouldBe(string.Concat(Enumerable.Repeat("b\n", TextSearch.MaxMatches + 2)));
            document.Undo();
            document.Text.ShouldBe(text);
        });

    /// <summary>
    /// Replace All beyond the matches highlighted searches the rest of the text in the background, as the search does,
    /// so that a costly expression there leaves the UI thread free; out of time, it replaces nothing.
    /// </summary>
    [Fact]
    public Task Replace_All_beyond_the_matches_highlighted_searches_in_the_background() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Repeat("x\n", TextSearch.MaxMatches + 1));
            // Still for the search; then running out at Replace All's first look at it.
            var clock = new SteppingTime { UiThread = Environment.CurrentManagedThreadId };
            using var window = new EditorWindow(
                new CodeEditor { Document = new StandaloneDocument(CodeLanguage.PlainText, text, clock) }
            );
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.ReplaceBox.Text = "y";
            bar.FindBox.Text = "x";
            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();
            clock.Step = TextSearch.TimeLimit;

            Click(bar, "Replace All");

            // The click returns before the search ends, whose result the UI thread takes later.
            bar.Message.ShouldBeNull();
            await SearchedAsync(bar);
            bar.Message.ShouldNotBeNull().ShouldContain("stopped");
            document.Text.ShouldBe(text);
            clock.ReadOnUiThread.ShouldBeFalse();
        });

    [Fact]
    public Task Empty_matches_are_found_gone_through_and_replaced() =>
        _ui.Run(async () =>
        {
            // ^ matches the start of every line, whatever its line break.
            using EditorWindow window = MarkedText.Show("|a\nb\r\nc", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            bar.ReplaceBox.Text = "> ";
            bar.FindBox.Text = "^";
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();

            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(2));
            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(5));
            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(0));

            Click(bar, "Replace All");
            await SearchedAsync(bar);
            document.Text.ShouldBe("> a\n> b\r\n> c");
        });

    [Fact]
    public Task Alt_P_preserves_the_case_of_each_match_that_it_replaces() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|Foo FOO foo", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            window.Type("foo");
            bar.ReplaceBox.Text = "bar";

            window.Press(Key.P, Toggle);

            bar.PreserveCaseToggle.IsChecked.ShouldBe(true);
            ToolTip
                .GetTip(bar.PreserveCaseToggle)
                .ShouldBe($"Preserve Case ({new KeyGesture(Key.P, Toggle).ToString("p", null)})");
            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();
            Click(bar, "Replace All");
            await SearchedAsync(bar);
            document.Text.ShouldBe("Bar BAR bar");
        });
}
