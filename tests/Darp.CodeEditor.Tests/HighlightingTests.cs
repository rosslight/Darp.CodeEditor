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

    /// <summary>The Light+ color of XML's tag names and YAML's keys.</summary>
    private static readonly Color s_tag = Color.Parse("#800000");

    /// <summary>The Dark+ color of JSON's property names.</summary>
    private static readonly Color s_darkPropertyName = Color.Parse("#9CDCFE");

    private readonly UiFixture _ui = ui;

    [Fact]
    public async Task Tokenizing_stops_between_lines_once_the_text_changed()
    {
        TextMateGrammar grammar = await JsonGrammar();
        using var changed = new CancellationTokenSource();
        // The text changes as the tokenizer reaches the third line.
        var lines = new ChangingAt(["{", "  \"a\": 1,", "  \"b\": 2,", "}"], 2, changed);

        LineResult[] done = BackgroundTextMateHighlighter.Tokenize(grammar, null, lines, changed.Token);

        done.Length.ShouldBe(2);
        done.ShouldAllBe(line => line.Tokens != null);
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

    /// <summary>The languages of a new editor's first frames.</summary>
    public static TheoryData<string> FirstFrameLanguages => ["json", "yaml"];

    [Theory]
    [MemberData(nameof(FirstFrameLanguages))]
    public Task A_new_editor_of_5000_lines_shows_syntax_colors_in_its_pixels(string language) =>
        _ui.Run(async () =>
        {
            (CodeLanguage codeLanguage, string text, Color syntax) =
                language == "json"
                    ? (CodeLanguage.Json, Texts.Json(5000), s_propertyName)
                    : (CodeLanguage.Yaml, Texts.Yaml(5000), s_tag);
            using var window = new EditorWindow(new CodeEditor { Document = CodeDocument.Create(codeLanguage, text) });

            // Some glyphs' worth of pixels: anti-aliasing blends the edges of the rest.
            await Wait.UntilAsync(() => EditorWindow.CountPixels(window.Render(), syntax) > 100);
        });

    /// <summary>
    /// Lines of 20,000 characters: a JSON string, which tokenizes in about a millisecond, and an array, which takes
    /// seconds and runs out of the background's time.
    /// </summary>
    public static TheoryData<string> LongValues => ["string", "array"];

    [Theory]
    [MemberData(nameof(LongValues))]
    public Task Typing_on_a_line_of_20000_characters_edits_it_and_colors_it_by_its_time(string kind) =>
        _ui.Run(async () =>
        {
            string value =
                kind == "string"
                    ? "\"" + new string('x', 20_000) + "\""
                    : "[" + string.Concat(Enumerable.Repeat("1, ", 6_666)) + "1]";
            CodeDocument document = CodeDocument.Create(
                CodeLanguage.Json,
                $"{{\n  \"long\": {value},\n  \"after\": 1\n}}"
            );
            using var window = new EditorWindow(new CodeEditor { Document = document });
            // Near the line's start: AvaloniaEdit edits and draws only the first 3,000 characters of a longer line.
            window.Editor.Selection = new TextSelection(document.CurrentSnapshot.GetOffset(new TextPosition(2, 100)));

            window.Type("1, ");

            document.CurrentSnapshot.GetText(new TextRange(window.Editor.Selection.Caret - 3, 3)).ShouldBe("1, ");
            // A line that tokenizes in time passes its state on; the next line after one that runs out of time starts
            // from the start of the grammar, where its key is a string.
            Color after = kind == "string" ? s_propertyName : s_string;
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(3).Contains(after);
            });
            if (kind == "string")
                window.ColorsOfLine(2).ShouldContain(s_string);
            else
                window.ColorsOfLine(2).ShouldBe([Avalonia.Media.Colors.Black]);
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
            // Layout passes without the dispatcher's jobs, so without the background's results. The pass that reaches
            // line 4,000 overruns its budget and ends there; the passes after it go on below. On a machine fast enough
            // for that search to fit in the budget, no pass overruns.
            for (int pass = 0; pass < 5 && !window.ColorsOfLine(4001).Contains(s_keyword); pass++)
            {
                window.TextView.Redraw();
                window.Window.UpdateLayout();
            }

            window.ColorsOfLine(4001).ShouldContain(s_keyword);
        });

    [Fact]
    public Task A_document_shown_while_the_previous_ones_grammar_loads_keeps_its_own_colors() =>
        _ui.Run(async () =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(100)) };
            using var window = new EditorWindow(editor);

            // JSON's grammar loads in the background, and no job has run since: its result is still to come.
            editor.Document = CodeDocument.Create(CodeLanguage.Yaml, Texts.Yaml(100));

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(1).Contains(s_tag);
            });
            await Task.Delay(200);
            window.Render();
            window.ColorsOfLine(1).ShouldContain(s_tag);
        });

    [Fact]
    public Task A_theme_set_while_the_grammar_loads_keeps_its_own_colors() =>
        _ui.Run(async () =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(100)) };
            using var window = new EditorWindow(editor);

            // The grammar loads with Light+ in the background, and no job has run since.
            editor.ColorTheme = CodeEditorColorTheme.DarkPlus;

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorsOfLine(2).Contains(s_darkPropertyName);
            });
            await Task.Delay(200);
            window.Render();
            window.ColorsOfLine(2).ShouldContain(s_darkPropertyName);
            window.ColorsOfLine(2).ShouldNotContain(s_propertyName);
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

    /// <summary>Lines whose text changes, cancelling <paramref name="change"/>, as the tokenizer reaches one of them.</summary>
    private sealed class ChangingAt(string[] lines, int index, CancellationTokenSource change) : IReadOnlyList<string>
    {
        private readonly string[] _lines = lines;
        private readonly int _index = index;
        private readonly CancellationTokenSource _change = change;

        public int Count => _lines.Length;

        public string this[int i] => _lines[i];

        public IEnumerator<string> GetEnumerator()
        {
            for (int i = 0; i < _lines.Length; i++)
            {
                if (i == _index)
                    _change.Cancel();
                yield return _lines[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
