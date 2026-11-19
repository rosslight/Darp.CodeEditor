using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using Darp.CodeEditor.Localization;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.SearchBars;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The search bar: its keys, its query from the text, its matches and their colors, and its limits, which it reports,
/// and beyond which F3 still reaches every match.
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

            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);

            bar.IsOpen.ShouldBeTrue();
            bar.FindBox.Text.ShouldBe("two");
            bar.FindBox.IsFocused.ShouldBeTrue();
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");

            // The first match from where the search started.
            window.Type("one");
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(8, 11));
            bar.Count.ShouldBe("2 of 3");

            // The text changed: the bar searches again.
            window.Editor.Document.ShouldNotBeNull().Replace(new TextRange(0, 0), "one ");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("3 of 4");
        });

    /// <summary>
    /// As VS Code's defaults for <c>editor.find.loop</c>, <c>cursorMoveOnType</c> and <c>closeOnResult</c>: typing the
    /// query selects its first match, Enter and Shift+Enter go round past the last and the first match, and the bar
    /// stays open with the keyboard in the query.
    /// </summary>
    [Fact]
    public Task Going_on_from_the_last_match_goes_round_and_the_bar_stays_open() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a b a", CodeLanguage.PlainText);
            CodeEditor editor = window.Editor;
            editor.OpenSearch();
            SearchBar bar = BarOf(window);

            window.Type("b");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 2");
            editor.Selection.ShouldBe(new TextSelection(2, 3));

            window.Press(Key.Enter);
            editor.Selection.ShouldBe(new TextSelection(6, 7));
            window.Press(Key.Enter);
            editor.Selection.ShouldBe(new TextSelection(2, 3));
            window.Press(Key.Enter, KeyModifiers.Shift);
            editor.Selection.ShouldBe(new TextSelection(6, 7));
            bar.IsOpen.ShouldBeTrue();
            bar.FindBox.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task Tab_goes_between_the_query_and_the_replacement_as_in_VS_Code() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one two", CodeLanguage.PlainText);

            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.FindBox.IsFocused.ShouldBeTrue();
            window.Press(Key.Tab);
            bar.ReplaceBox.IsFocused.ShouldBeTrue();
            window.Press(Key.Tab, KeyModifiers.Shift);
            bar.FindBox.IsFocused.ShouldBeTrue();

            // Without the replace row, Tab goes on to the toggles.
            window.Press(Key.Escape);
            window.Editor.OpenSearch();
            window.Press(Key.Tab);
            bar.FindBox.IsFocused.ShouldBeFalse();
            bar.ReplaceBox.IsFocused.ShouldBeFalse();
        });

    [Fact]
    public Task An_editor_makes_its_search_bar_the_first_time_it_opens() =>
        _ui.Run(async () =>
        {
            // Every editor shows; few are searched: those pay for the bar.
            using EditorWindow window = MarkedText.Show("a «b» a b", CodeLanguage.PlainText);
            window.Editor.GetVisualDescendants().OfType<SearchBar>().ShouldBeEmpty();

            window.Press(Key.F3);

            await SearchedAsync(BarOf(window));
            window.Editor.Selection.ShouldBe(new TextSelection(6, 7));
            BarOf(window).IsOpen.ShouldBeTrue();
        });

    [Fact]
    public Task The_keys_open_the_bars_go_through_the_matches_and_close() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a b a", CodeLanguage.PlainText);
            KeyGesture find = ApplicationCommands.Find.Gesture;
            KeyGesture replace = ApplicationCommands.Replace.Gesture;

            // The query is the word at the caret.
            window.Press(find.Key, find.KeyModifiers);
            SearchBar bar = BarOf(window);
            bar.IsOpen.ShouldBeTrue();
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeFalse();
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 3");

            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 1));
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

            // F3 opens the bar again for its query, and goes to the next match, with the keyboard in the text.
            window.Editor.Selection = new TextSelection(2, 3);
            window.Press(Key.F3);
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));
            bar.FindBox.Text.ShouldBe("a");
            EditorWindow.PartOf(window.Editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task AvaloniaEdits_search_commands_open_the_editors_own_bar() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a", CodeLanguage.PlainText);
            TextEditor part = EditorWindow.PartOf(window.Editor);

            ApplicationCommands.Find.Execute(null, part.TextArea);
            SearchBar bar = BarOf(window);
            bar.IsOpen.ShouldBeTrue();
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 2");
            SearchCommands.FindNext.Execute(null, part.TextArea);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 1));
            ApplicationCommands.Replace.Execute(null, part.TextArea);
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeTrue();

            part.SearchPanel.IsOpened.ShouldBeFalse();
        });

    /// <summary>
    /// A new query selects its first match also when a newer search replaces the query's own before its result is in:
    /// the replacement or the text changes right after the query, with no dispatcher pass for the result between.
    /// </summary>
    [Theory]
    [InlineData("replacement")]
    [InlineData("text")]
    public Task A_new_query_selects_its_first_match_also_when_a_newer_search_takes_over(string change) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a b", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);

            bar.FindBox.Text = "b";
            if (change == "replacement")
                bar.ReplaceBox.Text = "c";
            else
                document.Replace(new TextRange(7, 0), " b");
            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();

            window.Editor.Selection.ShouldBe(new TextSelection(2, 3));
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
            await SearchedAsync(BarOf(window));
            BarOf(window).Count.ShouldBe("1 of 2");

            // The selected match in the current match's color, once the theme is loaded, the other in the highlight
            // color.
            await Wait.UntilAsync(() => window.RenderAndCountPixels(Color.Parse("#00C000"), new TextRange(0, 3)) > 0);
            window.RenderAndCountPixels(Color.Parse("#0000FF"), new TextRange(8, 3)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(Color.Parse("#0000FF"), new TextRange(4, 3)).ShouldBe(0);
        });

    /// <summary>
    /// As in VS Code, every match stays within reach beyond those highlighted: F3 and Shift+F3 search the text for
    /// the next and the previous one, and go round only past the last match of all.
    /// </summary>
    [Fact]
    public Task F3_and_Shift_F3_reach_the_matches_beyond_those_highlighted() =>
        _ui.Run(async () =>
        {
            const int Count = TextSearch.MaxMatches + 2;
            using EditorWindow window = MarkedText.Show(
                "|" + string.Concat(Enumerable.Repeat("a\n", Count)),
                CodeLanguage.PlainText
            );
            // The query is the word at the caret.
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 19,999+");

            // The last match highlighted, then the two after it, and round to the first.
            window.Editor.Selection = Match(TextSearch.MaxMatches - 1);
            foreach (int next in (int[])[TextSearch.MaxMatches, Count - 1, 0])
            {
                window.Press(Key.F3);
                await SearchedAsync(bar);
                window.Editor.Selection.ShouldBe(Match(next));
            }
            // Back round to the last match of all, and on back to the last one highlighted.
            foreach (int previous in (int[])[Count - 1, TextSearch.MaxMatches, TextSearch.MaxMatches - 1])
            {
                window.Press(Key.F3, KeyModifiers.Shift);
                await SearchedAsync(bar);
                window.Editor.Selection.ShouldBe(Match(previous));
            }
            bar.Count.ShouldBe("19999 of 19,999+");

            static TextSelection Match(int index) => new(index * 2, index * 2 + 1);
        });

    /// <summary>
    /// Shift+F3 beyond the matches highlighted that runs out of its time says so, and leaves the selection, rather than
    /// select the last match that it reached.
    /// </summary>
    [Fact]
    public Task Shift_F3_beyond_the_matches_highlighted_out_of_its_time_says_so_and_stays() =>
        _ui.Run(async () =>
        {
            string text = string.Concat(Enumerable.Repeat("a\n", TextSearch.MaxMatches + 2));
            var clock = new SteppingTime();
            using var window = new EditorWindow(
                new CodeEditor { Document = new StandaloneDocument(CodeLanguage.PlainText, text, clock) }
            );
            // The query is the word at the caret.
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            await SearchedAsync(bar);
            window.Editor.Selection = new TextSelection(0, 1);
            // Out of time at the search's second look at the clock, after the first match that it reached.
            clock.Step = TextSearch.TimeLimit / 2;

            window.Press(Key.F3, KeyModifiers.Shift);
            await SearchedAsync(bar);

            bar.Message.ShouldNotBeNull().ShouldContain("stopped");
            window.Editor.Selection.ShouldBe(new TextSelection(0, 1));
        });

    /// <summary>
    /// As in VS Code, a regular expression that can match a line break searches across lines, and the match shows over
    /// both lines.
    /// </summary>
    [Fact]
    public Task A_regular_expression_with_a_line_break_finds_and_highlights_matches_across_lines() =>
        _ui.Run(async () =>
        {
            var editor = new CodeEditor
            {
                Document = CodeDocument.Create(CodeLanguage.PlainText, "foo\r\nbar\nfoo bar"),
                ColorTheme = new CodeEditorColorTheme
                {
                    Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
                },
            };
            using var window = new EditorWindow(editor);
            editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;

            bar.FindBox.Text = @"foo\nbar";

            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");
            editor.Selection.ShouldBe(new TextSelection(0, 8));
            await Wait.UntilAsync(() => window.RenderAndCountPixels(Color.Parse("#00C000"), new TextRange(0, 3)) > 0);
            window.RenderAndCountPixels(Color.Parse("#00C000"), new TextRange(5, 3)).ShouldBeGreaterThan(0);
            window.RenderAndCountPixels(Color.Parse("#00C000"), new TextRange(13, 3)).ShouldBe(0);
        });

    /// <summary>
    /// As VS Code's find widget, the bar narrows its query and replacement in a narrow editor, and wraps beyond, so
    /// that every button lies inside the editor and takes a click.
    /// </summary>
    [Theory]
    [InlineData(500, true)]
    [InlineData(300, false)]
    public Task In_a_narrow_editor_every_button_of_the_bar_lies_inside_it_and_takes_a_click(
        double width,
        bool oneLine
    ) =>
        _ui.Run(async () =>
        {
            using var window = new EditorWindow(
                new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "a a") },
                width: width
            );
            window.Editor.Selection = new TextSelection(0, 1);
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.ReplaceBox.Text = "a";
            await SearchedAsync(bar);
            window.Layout();
            var editor = new Rect(window.Editor.Bounds.Size);
            // Close last.
            Button[] buttons =
            [
                .. ButtonsOf(bar).OrderBy(button => ToolTip.GetTip(button) as string == Strings.CloseSearch),
            ];
            buttons.Length.ShouldBe(10);
            // The query gave up width, and Close stays on its line where that is enough.
            bar.FindBox.Bounds.Width.ShouldBeLessThan(220);
            (buttons[^1].TranslatePoint(default, bar.FindBox).ShouldNotBeNull().Y < bar.FindBox.Bounds.Height).ShouldBe(
                oneLine
            );

            foreach (Button button in buttons)
            {
                Point topLeft = button.TranslatePoint(default, window.Editor).ShouldNotBeNull();
                editor.Contains(new Rect(topLeft, button.Bounds.Size)).ShouldBeTrue();
                int clicks = 0;
                button.Click += (_, _) => clicks++;
                button.IsEnabled.ShouldBeTrue();

                window.Click(topLeft + new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), MouseButton.Left);

                clicks.ShouldBe(1);
                await SearchedAsync(bar);
                window.Layout();
            }
            bar.IsOpen.ShouldBeFalse();
        });

    /// <summary>
    /// The query's width follows the editor's alone, not the width it had before, also beside a message wider than the
    /// rows, which wraps on its own.
    /// </summary>
    [Fact]
    public Task The_query_has_the_same_width_in_an_editor_of_a_width_from_a_narrower_or_a_wider_one() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a", CodeLanguage.PlainText);
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            // .NET's message names the pattern.
            bar.FindBox.Text = "(" + new string('a', 100);
            await SearchedAsync(bar);
            bar.Message.ShouldNotBeNull();

            double[] widths = [.. ((int[])[300, 550, 800, 550]).Select(width => QueryWidthAt(width))];

            widths[3].ShouldBe(widths[1]);

            double QueryWidthAt(double width)
            {
                window.Window.Width = width;
                window.Layout();
                return bar.FindBox.Bounds.Width;
            }
        });

    /// <summary>Every button and toggle of the bar has a name for screen readers: its tip's, without its keys.</summary>
    [Fact]
    public Task The_buttons_of_the_bar_have_names_for_screen_readers() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|a", CodeLanguage.PlainText);

            window.Editor.OpenSearch(replace: true);

            ButtonsOf(BarOf(window))
                .Select(button => ControlAutomationPeer.CreatePeerForElement(button).GetName())
                .ShouldBe([
                    Strings.MatchCase,
                    Strings.MatchWholeWord,
                    Strings.UseRegularExpression,
                    Strings.PreviousMatch,
                    Strings.NextMatch,
                    Strings.FindInSelection,
                    Strings.CloseSearch,
                    Strings.PreserveCase,
                    Strings.Replace,
                    Strings.ReplaceAll,
                ]);
        });

    /// <summary>
    /// As in VS Code, Ctrl+Enter, also on macOS, starts a new line in the query, which then grows and searches across
    /// lines; Enter goes on to the next match.
    /// </summary>
    [Fact]
    public Task Ctrl_Enter_starts_a_new_line_in_the_query() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|foo\nbar foo\nbar", CodeLanguage.PlainText);
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            window.Layout();
            double height = bar.FindBox.Bounds.Height;

            window.Type("foo");
            window.Press(Key.Enter, KeyModifiers.Control);
            window.Type("bar");

            bar.FindBox.Text.ShouldBe("foo\nbar");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 2");
            window.Layout();
            bar.FindBox.Bounds.Height.ShouldBeGreaterThan(height);
            window.Press(Key.Enter);
            window.Editor.Selection.ShouldBe(new TextSelection(8, 15));
            bar.FindBox.Text.ShouldBe("foo\nbar");
        });

    /// <summary>
    /// A search that runs out of its time, as one for a regular expression such as <c>(a+)+$</c> does, says so, and
    /// Replace All then replaces nothing.
    /// </summary>
    [Fact]
    public Task A_search_out_of_its_time_says_so_and_Replace_All_is_refused() =>
        _ui.Run(async () =>
        {
            // A clock that runs out at the search's first look at it.
            var clock = new SteppingTime { Step = TextSearch.TimeLimit };
            using var window = new EditorWindow(
                new CodeEditor { Document = new StandaloneDocument(CodeLanguage.PlainText, "aaa", clock) }
            );
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.ReplaceBox.Text = "b";
            bar.FindBox.Text = "a";
            await SearchedAsync(bar);

            bar.Message.ShouldNotBeNull().ShouldContain("stopped");
            bar.ReplaceAllButton.IsEnabled.ShouldBeFalse();
            Click(bar, "Replace All");
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("aaa");
        });

    [Fact]
    public Task An_invalid_regular_expression_says_why_and_matches_nothing() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a (b)", CodeLanguage.PlainText);
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;

            // The whole query at once: "(" alone is invalid too, and its message would end the wait early.
            bar.FindBox.Text = "(b";
            await SearchedAsync(bar);
            bar.Message.ShouldNotBeNull();

            bar.Count.ShouldBeNull();
            bar.ReplaceAllButton.IsEnabled.ShouldBeFalse();
            window.Editor.Selection.ShouldBe(new TextSelection(0));
        });

    [Fact]
    public Task Clearing_the_query_clears_the_matches() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a", CodeLanguage.PlainText);
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 2");

            window.Press(Key.Back);

            bar.Count.ShouldBeNull();
            bar.Message.ShouldBeNull();
        });

    /// <summary>
    /// A handler of the selection that a match takes may show another document: the search of the one before leaves
    /// its caret alone.
    /// </summary>
    [Fact]
    public Task A_document_that_a_handler_of_a_match_shows_keeps_its_caret() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a b a b", CodeLanguage.PlainText);
            CodeEditor editor = window.Editor;
            CodeDocument other = CodeDocument.Create(CodeLanguage.PlainText, "other text");
            editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "b";
            await SearchedAsync(bar);
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == CodeEditor.SelectionProperty && editor.Document != other)
                    editor.Document = other;
            };

            window.Press(Key.F3);

            editor.Document.ShouldBe(other);
            editor.Selection.ShouldBe(new TextSelection(0));
        });

    [Fact]
    public Task A_search_of_the_document_shown_before_never_shows_for_the_next_one() =>
        _ui.Run(async () =>
        {
            // The first document's search can show only after the switch: the UI thread takes its result.
            CodeDocument first = CodeDocument.Create(CodeLanguage.PlainText, "aaa");
            using EditorWindow window = MarkedText.Show("|a", CodeLanguage.PlainText);
            CodeDocument next = window.Editor.Document.ShouldNotBeNull();
            window.Editor.Document = first;
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "a";
            Task firstSearch = bar.LatestSearch;

            window.Editor.Document = next;
            await Wait.ForAsync(firstSearch);
            await SearchedAsync(bar);

            bar.Count.ShouldBe("? of 1");
            bar.Message.ShouldBeNull();
        });

    [Fact]
    public Task An_open_search_bar_closes_once_the_editor_has_no_document() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one", CodeLanguage.PlainText);
            window.Editor.OpenSearch();

            window.Editor.Document = null;
            window.Press(Key.Escape);

            BarOf(window).IsOpen.ShouldBeFalse();
        });

    [Fact]
    public Task Alt_C_W_and_R_toggle_the_options_from_the_text_and_from_the_query_without_moving_the_selection() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|a.b A.b axb zaxb", CodeLanguage.PlainText);

            // From the text, with the bar closed: the next search uses it.
            window.Press(Key.C, Toggle);
            SearchBar bar = BarOf(window);
            bar.IsOpen.ShouldBeFalse();
            bar.MatchCaseToggle.IsChecked.ShouldBe(true);

            // As in VS Code, the toggles name their keys.
            window.Editor.OpenSearch();
            ToolTip
                .GetTip(bar.MatchCaseToggle)
                .ShouldBe($"Match Case ({new KeyGesture(Key.C, Toggle).ToString("p", null)})");
            window.Type("a.b");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");

            // From the query, which keeps its text.
            window.Press(Key.C, Toggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 2");
            window.Press(Key.R, Toggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 4");
            window.Press(Key.W, Toggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            bar.FindBox.Text.ShouldBe("a.b");
            bar.FindBox.IsFocused.ShouldBeTrue();
            window.Editor.Selection.ShouldBe(new TextSelection(0, 3));
        });

    [Fact]
    public Task The_search_takes_the_word_at_the_caret_without_moving_it_or_only_a_selection_as_the_option_says() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("one t|wo one two", CodeLanguage.PlainText);
            window.Editor.Options.Find.SeedSearchStringFromSelection.ShouldBe(SeedSearchString.Always);

            window.Press(Key.F, EditorWindow.Command);
            SearchBar bar = BarOf(window);
            bar.FindBox.Text.ShouldBe("two");
            bar.FindBox.SelectedText.ShouldBe("two");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 2");
            window.Editor.Selection.ShouldBe(new TextSelection(5));

            // Only a selection.
            window.Press(Key.Escape);
            window.Editor.Options = WithSeed(SeedSearchString.Selection);
            window.Editor.Selection = new TextSelection(1);
            window.Press(Key.F, EditorWindow.Command);
            bar.FindBox.Text.ShouldBe("two");
            window.Press(Key.Escape);
            window.Editor.Selection = new TextSelection(0, 3);
            window.Press(Key.F, EditorWindow.Command);
            bar.FindBox.Text.ShouldBe("one");

            // Nothing.
            window.Press(Key.Escape);
            window.Editor.Options = WithSeed(SeedSearchString.Never);
            window.Editor.Selection = new TextSelection(4, 7);
            window.Press(Key.F, EditorWindow.Command);
            bar.FindBox.Text.ShouldBe("one");

            static CodeEditorOptions WithSeed(SeedSearchString seed) =>
                CodeEditorOptions.Default with
                {
                    Find = new FindOptions { SeedSearchStringFromSelection = seed },
                };
        });

    [Fact]
    public Task A_selection_taken_into_a_regular_expression_is_escaped() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«a.b+(c)» axb+c", CodeLanguage.PlainText);
            window.Press(Key.R, Toggle);

            window.Press(Key.F, EditorWindow.Command);

            SearchBar bar = BarOf(window);
            bar.FindBox.Text.ShouldBe(@"a\.b\+\(c\)");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");
        });

    [Fact]
    public Task Ctrl_H_takes_a_selection_and_then_the_keyboard_goes_into_the_replacement_as_it_does_from_the_query() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("one «two» one", CodeLanguage.PlainText);
            KeyGesture find = ApplicationCommands.Find.Gesture;
            KeyGesture replace = ApplicationCommands.Replace.Gesture;

            window.Press(replace.Key, replace.KeyModifiers);
            SearchBar bar = BarOf(window);
            bar.FindBox.Text.ShouldBe("two");
            bar.ReplaceBox.IsFocused.ShouldBeTrue();

            // Without a selection, it takes nothing, and the keyboard goes into the query; again, into the replacement.
            window.Press(Key.Escape);
            window.Editor.Selection = new TextSelection(1);
            window.Press(replace.Key, replace.KeyModifiers);
            bar.FindBox.Text.ShouldBe("two");
            bar.FindBox.IsFocused.ShouldBeTrue();
            window.Press(replace.Key, replace.KeyModifiers);
            bar.ReplaceBox.IsFocused.ShouldBeTrue();

            // Ctrl+F keeps the replace row of the open bar, and takes the word at the caret.
            window.Press(find.Key, find.KeyModifiers);
            bar.ReplaceBox.IsEffectivelyVisible.ShouldBeTrue();
            bar.FindBox.IsFocused.ShouldBeTrue();
            bar.FindBox.Text.ShouldBe("one");
        });

    [Fact]
    public Task F3_keeps_the_query_and_takes_the_word_at_the_caret_or_the_selection_only_without_one() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("a b| a b a", CodeLanguage.PlainText);

            window.Press(Key.F3);
            SearchBar bar = BarOf(window);
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(6, 7));
            bar.FindBox.Text.ShouldBe("b");

            // With the bar closed and a selection, the query stays.
            window.Press(Key.Escape);
            window.Editor.Selection = new TextSelection(0, 1);
            window.Press(Key.F3);
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(2, 3));
            bar.FindBox.Text.ShouldBe("b");

            // So it does with the bar open; an empty query takes the selection.
            window.Editor.Selection = new TextSelection(4, 5);
            window.Press(Key.F3, KeyModifiers.Shift);
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(2, 3));
            bar.FindBox.Text = "";
            window.Editor.Selection = new TextSelection(4, 5);
            window.Press(Key.F3);
            await SearchedAsync(bar);
            window.Editor.Selection.ShouldBe(new TextSelection(8, 9));
            bar.FindBox.Text.ShouldBe("a");
        });
}
