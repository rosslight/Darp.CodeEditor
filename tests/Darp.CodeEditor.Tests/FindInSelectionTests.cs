using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.SearchBars;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Find in Selection, as VS Code's: Alt+L or the bar's toggle search only the selected lines, which stay highlighted
/// while the bar is open, and <see cref="FindOptions.AutoFindInSelection"/> turns it on as the bar opens.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class FindInSelectionTests(UiFixture ui)
{
    /// <summary><c>editor.findRangeHighlightBackground</c> of the tests' theme.</summary>
    private static readonly Color s_findRange = Color.Parse("#C08040");

    private readonly UiFixture _ui = ui;

    /// <summary>
    /// A selection within lines searches them whole, as in VS Code; the lines show in the theme's find range color while
    /// the bar is open, and the bar searches everywhere again once it closes.
    /// </summary>
    [Fact]
    public Task Alt_L_searches_only_the_selected_lines_and_highlights_them() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("x a\na «a\na» x\na");
            KeyGesture find = ApplicationCommands.Find.Gesture;

            window.Press(Key.L, Toggle);
            window.Press(find.Key, find.KeyModifiers);
            SearchBar bar = BarOf(window);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(true);
            window.Type("a");

            // The first match of the second line, and the two others of the second and the third.
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));
            await Wait.UntilAsync(() => FindRangeOnLine(window, 3) > 0);
            FindRangeOnLine(window, 1).ShouldBe(0);
            FindRangeOnLine(window, 4).ShouldBe(0);

            // Off again, from the query: all five matches, from where the search started, as in VS Code.
            window.Press(Key.L, Toggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("5 of 5");
            bar.FindInSelectionToggle.IsChecked.ShouldBe(false);
            FindRangeOnLine(window, 3).ShouldBe(0);

            // On, and closed: off.
            window.Editor.Selection = new TextSelection(4, 9);
            window.Press(Key.L, Toggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 3");
            window.Press(Key.Escape);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(false);
            FindRangeOnLine(window, 3).ShouldBe(0);
            window.Press(find.Key, find.KeyModifiers);
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 5");
        });

    /// <summary>
    /// As VS Code's toggle: it turns Find in Selection on only for a selection, names its key, and, like the key, ends at
    /// the end of the line before a selection that ends at the start of a line.
    /// </summary>
    [Fact]
    public Task The_toggle_turns_Find_in_Selection_on_for_a_selection() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("|a a\na a\na a");
            // The query is the word at the caret.
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("? of 6");
            window.Editor.Selection = new TextSelection(4);
            bar.FindInSelectionToggle.IsEnabled.ShouldBeFalse();
            ToolTip
                .GetTip(bar.FindInSelectionToggle)
                .ShouldBe($"Find in Selection ({new KeyGesture(Key.L, Toggle).ToString("p", null)})");

            // The second line and the start of the third: the second line only.
            window.Editor.Selection = new TextSelection(4, 8);
            bar.FindInSelectionToggle.IsEnabled.ShouldBeTrue();
            Click(bar.FindInSelectionToggle);

            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 2");
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));
            Click(bar.FindInSelectionToggle);
            await SearchedAsync(bar);
            bar.Count.ShouldBe("5 of 6");
        });

    /// <summary>The scope follows the edits, and Replace All replaces only inside it.</summary>
    [Fact]
    public Task Replace_All_replaces_only_in_the_scope_which_follows_the_edits() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("a\n«a\na»\na");
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Press(Key.L, Toggle);
            window.Editor.OpenSearch(replace: true);
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "a";
            bar.ReplaceBox.Text = "b";
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 2");

            document.Replace(new TextRange(0, 0), "a\n");
            await SearchedAsync(bar);
            bar.ReplaceAllButton.IsEnabled.ShouldBeTrue();
            Click(bar, "Replace All");
            await SearchedAsync(bar);

            document.Text.ShouldBe("a\na\nb\nb\na");
        });

    /// <summary>
    /// As in VS Code, a selection from inside a line to the start of the next spans lines, and searches the first one
    /// whole.
    /// </summary>
    [Fact]
    public Task A_selection_from_inside_a_line_to_the_start_of_the_next_searches_that_line_whole() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("a a\na «a\n»a a");
            window.Press(Key.L, Toggle);
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "a";
            await SearchedAsync(bar);

            bar.Count.ShouldNotBeNull().ShouldEndWith("of 2");
        });

    /// <summary>As VS Code's scope, the scope grows with text typed at either of its edges.</summary>
    [Fact]
    public Task The_scope_grows_with_text_typed_at_its_edges() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("x «a» x");
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Press(Key.L, Toggle);
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.FindBox.Text = "a";
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 1");

            document.Replace(new TextRange(2, 0), "a");
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 2");
            document.Replace(new TextRange(4, 0), "a");
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 3");
        });

    /// <summary>
    /// An edit can leave the scope ending between a <c>\r</c> and its <c>\n</c>: it then ends before the line break,
    /// and the search still finds what is inside it.
    /// </summary>
    [Fact]
    public Task A_scope_that_an_edit_ends_inside_a_line_break_ends_before_it() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("«ab»\nb");
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Press(Key.L, Toggle);
            window.Editor.OpenSearch();
            SearchBar bar = BarOf(window);
            bar.RegexToggle.IsChecked = true;
            bar.FindBox.Text = "a|b";
            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 2");

            // The scope "ab" is now "a\r", and the "b" after it was the scope's.
            document.Replace(new TextRange(1, 1), "\r");

            await SearchedAsync(bar);
            bar.Count.ShouldNotBeNull().ShouldEndWith("of 1");
            window.Press(Key.F3);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 1));
        });

    [Fact]
    public Task AutoFindInSelection_turns_it_on_as_the_bar_opens_as_it_says() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = Show("b a «b\nb» a b");
            KeyGesture find = ApplicationCommands.Find.Gesture;
            window.Editor.Options.Find.AutoFindInSelection.ShouldBe(AutoFindInSelection.Never);

            window.Press(find.Key, find.KeyModifiers);
            SearchBar bar = BarOf(window);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(false);

            // Over several lines.
            window.Press(Key.Escape);
            window.Editor.Options = WithAutoFindInSelection(AutoFindInSelection.Multiline);
            window.Press(find.Key, find.KeyModifiers);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(true);
            window.Press(Key.Escape);
            window.Editor.Selection = new TextSelection(4, 5);
            window.Press(find.Key, find.KeyModifiers);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(false);

            // Whenever something is selected: the query is the selection, which is the scope.
            window.Press(Key.Escape);
            window.Editor.Options = WithAutoFindInSelection(AutoFindInSelection.Always);
            window.Editor.Selection = new TextSelection(4, 5);
            window.Press(find.Key, find.KeyModifiers);
            bar.FindInSelectionToggle.IsChecked.ShouldBe(true);
            bar.FindBox.Text.ShouldBe("b");
            await SearchedAsync(bar);
            bar.Count.ShouldBe("1 of 1");

            static CodeEditorOptions WithAutoFindInSelection(AutoFindInSelection mode) =>
                CodeEditorOptions.Default with
                {
                    Find = new FindOptions { AutoFindInSelection = mode },
                };
        });

    private static EditorWindow Show(string marked)
    {
        (string text, TextSelection selection) = MarkedText.Parse(marked);
        var editor = new CodeEditor
        {
            Document = CodeDocument.Create(CodeLanguage.PlainText, text),
            ColorTheme = new CodeEditorColorTheme
            {
                Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/highlights.json"),
            },
        };
        var window = new EditorWindow(editor);
        editor.Selection = selection;
        return window;
    }

    /// <summary>The pixels of the find range color right of the text of a line, where nothing else is drawn.</summary>
    private static int FindRangeOnLine(EditorWindow window, int line)
    {
        CodeDocument document = window.Editor.Document.ShouldNotBeNull();
        int end = document.CurrentSnapshot.GetOffset(new TextPosition(line, int.MaxValue));
        Rect character = window.CharacterArea(Math.Max(0, end - 1));
        var area = new Rect(character.Right + 20, character.Top, 40, character.Height);
        return window.RenderAndCountPixels(s_findRange, area);
    }
}
