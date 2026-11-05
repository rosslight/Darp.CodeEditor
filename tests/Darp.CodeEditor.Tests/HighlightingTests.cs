using Avalonia.Media;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using TextMateSharp.Internal.Grammars;
using TextMateSharp.Internal.Types;

namespace Darp.CodeEditor.Tests;

/// <summary>Background TextMate highlighting: the lessons of the prototype, and colors in the frames that show text.</summary>
[Collection(UiFixture.Collection)]
public sealed class HighlightingTests(UiFixture ui)
{
    /// <summary>Light+ colors of JSON, and of the test grammar's keywords.</summary>
    private static readonly Color s_comment = Color.Parse("#008000");
    private static readonly Color s_string = Color.Parse("#A31515");
    private static readonly Color s_propertyName = Color.Parse("#0451A5");
    private static readonly Color s_number = Color.Parse("#098658");
    private static readonly Color s_keyword = Color.Parse("#0000FF");

    /// <summary>The Light+ color of XML's tag names.</summary>
    private static readonly Color s_tag = Color.Parse("#800000");

    private readonly UiFixture _ui = ui;

    [Fact]
    public async Task Tokenizing_stops_between_lines_once_the_text_changed()
    {
        TextMateGrammar grammar = await JsonGrammar();
        using var changed = new CancellationTokenSource();
        await changed.CancelAsync();

        LineResult[] lines = BackgroundTextMateHighlighter.Tokenize(
            grammar,
            null,
            ["{", "  \"a\": 1", "}"],
            changed.Token
        );

        lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_line_that_runs_out_of_time_stays_uncolored_and_the_next_starts_from_the_start_of_the_grammar()
    {
        TextMateGrammar grammar = await JsonGrammar();

        LineResult[] lines = BackgroundTextMateHighlighter.Tokenize(
            grammar,
            null,
            ["{", SlowLine, "  \"after\": 2"],
            TestContext.Current.CancellationToken
        );

        lines[1].ShouldBe(new LineResult(null, null));
        // In the object, the key would be a property name; from the start of the grammar, it is a string.
        HashSet<Color> after = Colors(grammar, lines[2]);
        after.ShouldContain(s_string);
        after.ShouldContain(s_number);
        after.ShouldNotContain(s_propertyName);
    }

    [Fact]
    public Task A_long_line_that_tokenizes_in_time_passes_its_state_on() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(
                CodeLanguage.Json,
                $"{{\n  \"before\": 1,\n{LongStringLine},\n  \"after\": 2\n}}"
            );
            using var window = new EditorWindow(new CodeEditor { Document = document });

            // The UI thread leaves the long line to the background, and colors the next one from a guess first.
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(4).Contains(s_propertyName);
            });

            window.ColorsOfLine(3).ShouldContain(s_propertyName);
            window.ColorsOfLine(3).ShouldContain(s_string);
        });

    [Fact]
    public Task The_first_lines_that_come_into_view_are_colored_without_waiting_for_the_background() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(5000));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(2).Contains(s_propertyName);
            });
            TextSnapshot text = document.CurrentSnapshot;

            window.Editor.ScrollTo(new TextRange(text.GetOffset(new TextPosition(4000, 1)), 0));
            // A layout without the dispatcher's jobs: the background's results cannot arrive in between.
            window.Window.UpdateLayout();

            int top = window.TextView.VisualLines[0].FirstDocumentLine.LineNumber;
            top.ShouldBeGreaterThan(3900);
            // Without the lines above, the line starts from the start of the grammar: its key is colored as a string.
            window.ColorsOfLine(top + 1).ShouldContain(s_string);
        });

    [Fact]
    public Task A_line_that_overruns_the_UI_threads_budget_leaves_it_tokenizing_the_others() =>
        _ui.Run(async () =>
        {
            var language = new CodeLanguage(
                "backtracking",
                new Uri("avares://Darp.CodeEditor.Tests/Languages/backtracking.tmLanguage.json")
            );
            // One regex search on line 4,000 runs for tens of milliseconds: TextMate cannot stop inside it. The
            // background tokenizes the lines near the view only, so it has not been there.
            string text = string.Join(
                '\n',
                Enumerable.Range(1, 5000).Select(i => i == 4000 ? new string('a', 22) : "key")
            );
            CodeDocument document = CodeDocument.Create(language, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(2).Contains(s_keyword);
            });

            window.Editor.ScrollTo(new TextRange(document.CurrentSnapshot.GetOffset(new TextPosition(4000, 1)), 0));
            // The first pass reaches line 4,000, which overruns its budget, and ends there.
            window.Window.UpdateLayout();
            window.ColorsOfLine(4001).ShouldNotContain(s_keyword);
            // The next pass, without the dispatcher's jobs, so without the background's results.
            window.TextView.Redraw();
            window.Window.UpdateLayout();

            window.ColorsOfLine(4001).ShouldContain(s_keyword);
        });

    [Fact]
    public Task An_xml_document_shows_its_tags_and_comments_in_their_colors() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Xml, "<root a=\"v\">\n  <!-- note -->\n</root>");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(1).Contains(s_tag) && window.ColorsOfLine(2).Contains(s_comment);
            });
        });

    [Fact]
    public Task An_edit_that_changes_the_state_at_a_lines_end_recolors_the_lines_below() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(100));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).Contains(s_propertyName);
            });

            document.Replace(new TextRange(document.CurrentSnapshot.GetOffset(new TextPosition(2, 1)), 0), "/*");
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).SetEquals([s_comment]);
            });

            document.Undo();
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(20).Contains(s_propertyName);
            });
        });

    /// <summary>A property of 20,000 characters whose value is one string: it tokenizes in about a millisecond.</summary>
    private static string LongStringLine { get; } = $"  \"long\": \"{new string('x', 20_000)}\"";

    /// <summary>A property of 20,000 characters whose value is an array: tokenizing it takes seconds.</summary>
    private static string SlowLine { get; } = "  \"slow\": [" + string.Concat(Enumerable.Repeat("1, ", 6_666)) + "1],";

    private static async Task<TextMateGrammar> JsonGrammar()
    {
        IRawGrammar grammar = (await CodeLanguage.Json.Loaded).Grammar.ShouldNotBeNull();
        ColorThemeFile theme = (await CodeEditorColorTheme.LightPlus.Loaded).ShouldNotBeNull();
        return TextMateGrammar.Load(theme, grammar);
    }

    /// <summary>The colors of a tokenized line, as the editor shows them.</summary>
    private static HashSet<Color> Colors(TextMateGrammar grammar, LineResult line)
    {
        int[] tokens = line.Tokens.ShouldNotBeNull();
        var colors = new HashSet<Color>();
        for (int i = 1; i < tokens.Length; i += 2)
        {
            if (grammar.Brushes[EncodedTokenAttributes.GetForeground(tokens[i])] is ISolidColorBrush brush)
                colors.Add(brush.Color);
        }
        return colors;
    }
}
