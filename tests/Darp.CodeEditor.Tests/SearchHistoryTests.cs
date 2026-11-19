using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The histories of the search bar's query and replacement, as VS Code's: they take the text after a pause, and Up and
/// Down go through them.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SearchHistoryTests(UiFixture ui)
{
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(500);

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task The_query_goes_into_its_history_after_a_pause_and_Up_and_Down_go_through_it() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            await using var server = new LanguageServer(new FakeServer().Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two three");
            var history = new SearchHistory();
            using var window = new EditorWindow(new CodeEditor { Document = document, FindHistory = history });
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "";

            window.Type("one");
            Advance(time, s_pause - TimeSpan.FromMilliseconds(1));
            history.Entries.ShouldBeEmpty();
            Advance(time, TimeSpan.FromMilliseconds(1));
            history.Entries.ShouldBe(["one"]);
            bar.FindBox.Text = "two";
            Advance(time, s_pause);
            history.Entries.ShouldBe(["one", "two"]);

            // Up shows the entry before the text's own, and stays at the first.
            window.Press(Key.Up);
            bar.FindBox.Text.ShouldBe("one");
            window.Press(Key.Up);
            bar.FindBox.Text.ShouldBe("one");
            // Down goes on, and past the latest empties the query; Alt works too.
            window.Press(Key.Down, KeyModifiers.Alt);
            bar.FindBox.Text.ShouldBe("two");
            window.Press(Key.Down);
            bar.FindBox.Text.ShouldBe("");

            // A text that is not in the history goes into it as Up shows the entry before.
            window.Type("three");
            window.Press(Key.Up);
            bar.FindBox.Text.ShouldBe("two");
            history.Entries.ShouldBe(["one", "two", "three"]);
            // Showing an entry adds nothing after the pause.
            Advance(time, s_pause);
            history.Entries.ShouldBe(["one", "two", "three"]);
        });

    [Fact]
    public Task The_replacement_has_a_history_of_its_own_and_a_query_of_several_lines_goes_up_and_down_its_lines() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            await using var server = new LanguageServer(new FakeServer().Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            var findHistory = new SearchHistory(["a\nb"]);
            var replaceHistory = new SearchHistory(["x"]);
            using var window = new EditorWindow(
                new CodeEditor
                {
                    Document = document,
                    FindHistory = findHistory,
                    ReplaceHistory = replaceHistory,
                }
            );
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);

            window.Press(Key.Tab);
            bar.ReplaceBox.IsFocused.ShouldBeTrue();
            window.Press(Key.Up);
            bar.ReplaceBox.Text.ShouldBe("x");

            window.Press(Key.Tab, KeyModifiers.Shift);
            bar.FindBox.Text = "";
            window.Press(Key.Up);
            bar.FindBox.Text.ShouldBe("a\nb");
            // From the end of the second line, Up moves to the first line; from the start of the text, it goes on.
            window.Press(Key.Up);
            bar.FindBox.Text.ShouldBe("a\nb");
            bar.FindBox.CaretIndex.ShouldBeLessThan(2);
            bar.FindBox.CaretIndex = 0;
            window.Press(Key.Down);
            bar.FindBox.Text.ShouldBe("a\nb");
            bar.FindBox.CaretIndex.ShouldBeGreaterThan(1);
            bar.FindBox.CaretIndex = 3;
            window.Press(Key.Down);
            bar.FindBox.Text.ShouldBe("");
        });

    [Fact]
    public Task Editors_share_one_history_by_default_and_without_one_keep_their_own() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            await using var server = new LanguageServer(new FakeServer().Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            using var first = new EditorWindow(new CodeEditor { Document = document });
            using var second = new EditorWindow(new CodeEditor { Document = document });
            first.Editor.FindHistory.ShouldBeSameAs(second.Editor.FindHistory);
            first.Editor.ReplaceHistory.ShouldNotBeSameAs(first.Editor.FindHistory);
            string query = $"shared {Guid.NewGuid()}";

            first.Editor.OpenSearch();
            BarOf(first).FindBox.Text = query;
            Advance(time, s_pause);
            second.Window.Activate();
            second.Editor.OpenSearch();
            BarOf(second).FindBox.Text = "";
            second.Press(Key.Up);
            BarOf(second).FindBox.Text.ShouldBe(query);

            // Without a history, the editor's bar keeps one of its own, which the shared one doesn't see.
            second.Editor.FindHistory = null;
            string own = $"own {Guid.NewGuid()}";
            BarOf(second).FindBox.Text = own;
            Advance(time, s_pause);
            first.Editor.FindHistory.ShouldNotBeNull().Entries.ShouldNotContain(own);
            BarOf(second).FindBox.Text = "";
            second.Press(Key.Up);
            BarOf(second).FindBox.Text.ShouldBe(own);
        });

    [Fact]
    public Task The_query_says_that_Up_and_Down_go_through_its_history_while_it_has_one_and_the_keyboard() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            window.Editor.FindHistory = new SearchHistory();
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.PlaceholderText.ShouldBe("Find");

            window.Editor.FindHistory.Add("one");
            window.Press(Key.Escape);
            window.Editor.OpenSearch();
            bar.FindBox.PlaceholderText.ShouldBe("Find (⇅ for history)");
            window.Press(Key.Escape);
            bar.FindBox.PlaceholderText.ShouldBe("Find");
        });

    /// <summary>Moves the clock on, and runs what the timers due meanwhile posted.</summary>
    private static void Advance(ManualTime time, TimeSpan span)
    {
        time.Advance(span);
        Dispatcher.UIThread.RunJobs();
    }

    private static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();
}
