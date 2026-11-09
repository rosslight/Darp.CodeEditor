using System.Text.Json;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// A server's semantic tokens: in the theme's colors over the TextMate colors, asked for by the editors that show a
/// document, again after edits, and never on text that changed since they came.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SemanticTokensTests(UiFixture ui)
{
    /// <summary>Light+'s own semantic color of the type "stringLiteral".</summary>
    private static readonly Color s_stringLiteral = Color.Parse("#A31515");

    /// <summary>Light+'s color of the scope "entity.name.type", which VS Code maps classes to.</summary>
    private static readonly Color s_class = Color.Parse("#267F99");

    /// <summary>Light+'s color of the scope "variable.other.constant", which VS Code maps read-only variables to.</summary>
    private static readonly Color s_readonlyVariable = Color.Parse("#0070C1");

    /// <summary>Light+'s color of the scope "variable", which VS Code maps variables to.</summary>
    private static readonly Color s_variable = Color.Parse("#001080");

    /// <summary>Light+'s text without a color of its own.</summary>
    private static readonly Color s_text = Color.Parse("#000000");

    private readonly UiFixture _ui = ui;

    /// <summary>The semantic tokens a server offers: for ranges and the whole text, or the whole text only.</summary>
    public static TheoryData<string> Offers => ["range", "full"];

    [Theory]
    [MemberData(nameof(Offers))]
    public Task Tokens_color_text_by_the_themes_semantic_colors_and_its_colors_of_VS_Codes_scopes(string offer) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--semantic-tokens", offer);
            await using var server = new LanguageServer(fake.Options);
            string path = Path.Combine(AppContext.BaseDirectory, $"tokens-{offer}.txt");
            // Long words, so that each shows enough pixels of its color: the server names a token's type and modifiers.
            const string text = "stringLiteral_static class_deprecated variable_readonly variable_static plain_words";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text, path);
            using var window = new EditorWindow(new CodeEditor { Document = document });

            (string Word, Color Color)[] words =
            [
                ("stringLiteral_static", s_stringLiteral),
                ("class_deprecated", s_class),
                ("variable_readonly", s_readonlyVariable),
                ("variable_static", s_variable),
                ("plain_words", s_text),
            ];
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return words.All(w => window.ColorAt(text.IndexOf(w.Word, StringComparison.Ordinal)) == w.Color);
            });
            foreach ((string word, Color color) in words)
            {
                var range = new TextRange(text.IndexOf(word, StringComparison.Ordinal), word.Length);
                window.RenderAndCountPixels(color, range).ShouldBeGreaterThan(10, word);
            }
            string uri = new Uri(path).AbsoluteUri;
            fake.LinesStartingWith("semanticTokens/")
                .ShouldAllBe(line =>
                    line == (offer == "range" ? $"semanticTokens/range {uri} 0-0" : $"semanticTokens/full {uri} all")
                );
        });

    [Fact]
    public Task Tokens_follow_typing_without_coloring_changed_text_and_are_asked_for_again() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "class one\nclass two");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorAt(0) == s_class && window.ColorAt(10) == s_class;
            });
            int requests = fake.LinesStartingWith("semanticTokens/").Count;
            using CodeDocument hold = server.CreateDocument(FakeServer.Commands, "hold-semantic-tokens");
            await fake.WaitForAsync("holding semantic tokens");

            // Inside the token on the first line; before the token on the second, a line break and a space.
            window.Editor.Selection = new TextSelection(2);
            window.Type("x");
            window.Editor.Selection = new TextSelection(11);
            window.Press(Key.Enter);
            window.Type(" ");
            window.Render();

            // "clxass one\n\n class two": the changed token goes, the moved one keeps its color.
            document.Text.ShouldBe("clxass one\n\n class two");
            window.ColorAt(0).ShouldBe(s_text);
            window.ColorAt(2).ShouldBe(s_text);
            window.ColorAt(12).ShouldBe(s_text);
            window.ColorAt(13).ShouldBe(s_class);
            window.ColorAt(17).ShouldBe(s_class);
            await fake.WaitForAsync("semanticTokens/", requests + 1);

            // An edit outdates the request under way.
            window.Type("y");
            await fake.WaitForAsync("cancel ");
            await fake.WaitForAsync("semanticTokens/", requests + 2);
        });

    [Fact]
    public Task Scrolling_to_lines_without_tokens_asks_for_them() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string text = string.Concat(Enumerable.Range(0, 300).Select(i => $"class{i} class_static\n"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorAt(text.IndexOf("class_static", StringComparison.Ordinal)) == s_class;
            });
            int last = text.LastIndexOf("class_static", StringComparison.Ordinal);
            window.ColorAt(last).ShouldBeNull();

            window.Editor.ScrollTo(new TextRange(last, 0));

            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorAt(last) == s_class;
            });
            // The lines in view, which end with the last line of text and the empty one after it.
            fake.LinesStartingWith("semanticTokens/range").ShouldContain(line => line.EndsWith("-300"));
        });

    [Fact]
    public void A_line_keeps_at_most_one_token_per_character_of_its_shown_part()
    {
        var legend = new SemanticTokensLegend(["variable"], []);
        // Line 0: 100,000 tokens on its first character. Line 1: a token on each of its first 10,000 characters, most of
        // them in the hidden tail of a long line.
        IEnumerable<int> overlapping = Enumerable.Range(0, 100_000).SelectMany(_ => (int[])[0, 0, 1, 0, 0]);
        IEnumerable<int> wide = Enumerable
            .Range(0, 10_000)
            .SelectMany(i => (int[])[i == 0 ? 1 : 0, i == 0 ? 0 : 1, 1, 0, 0]);
        string json = $"{{ \"data\": [{string.Join(",", overlapping.Concat(wide))}] }}";

        SemanticTokenLines tokens = SemanticTokenLines.Read(JsonElement.Parse(json), legend, 0, 1);

        (tokens.Lines[0].Length / SemanticTokenLines.Stride).ShouldBe(1);
        (tokens.Lines[1].Length / SemanticTokenLines.Stride).ShouldBe(VisibleText.LineLimit);
    }

    [Fact]
    public Task Lines_that_join_keep_at_most_one_token_per_character_of_their_shown_part() =>
        _ui.Run(() =>
        {
            // 30 lines as long as the shown part, a token on each character.
            const int count = 30;
            const int width = VisibleText.LineLimit;
            string text = string.Join("\n", Enumerable.Repeat(new string('x', width), count));
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            SemanticHighlighter highlighter =
                editor.SemanticHighlighter ?? throw new InvalidOperationException("The editor shows no document.");
            int[] line = [.. Enumerable.Range(0, width).SelectMany(column => (int[])[column, 1, 0, 0])];
            var legend = new SemanticTokensLegend(["variable"], []);
            highlighter.Show(
                SemanticHighlighter.Prepare(new SemanticTokenLines(legend, 0, [.. Enumerable.Repeat(line, count)]), 0)
            );

            // Each line joins the first, as Delete at its end does.
            for (int i = 1; i < count; i++)
                document.Replace(new TextRange(i * width, 1), "");

            // Moved past the shown part, the tokens of the joined lines went.
            IReadOnlyList<int> kept = highlighter.TokensOf(0);
            kept.Count.ShouldBe(line.Length);
            kept.Where((_, i) => i % SemanticTokenLines.Stride == 0).Max().ShouldBe(width - 1);
        });

    [Fact]
    public Task A_line_draws_at_most_250_tokens_in_their_colors() =>
        _ui.Run(() =>
        {
            // A token on each character, of two types in two colors that take turns: a run of text each.
            const int width = VisibleText.LineLimit;
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, new string('x', width));
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            SemanticHighlighter highlighter =
                editor.SemanticHighlighter ?? throw new InvalidOperationException("The editor shows no document.");
            int[] line = [.. Enumerable.Range(0, width).SelectMany(column => (int[])[column, 1, column % 2, 0])];
            var legend = new SemanticTokensLegend(["class", "variable"], []);

            highlighter.Show(SemanticHighlighter.Prepare(new SemanticTokenLines(legend, 0, [line]), 0));
            window.Render();

            // The colored tokens, and the rest of the line in its TextMate color: each run costs the layout.
            int runs = window.TextView.VisualLines[0].TextLines.Sum(row => row.TextRuns.Count);
            runs.ShouldBeGreaterThanOrEqualTo(SemanticHighlighter.MaxColoredTokens);
            runs.ShouldBeLessThanOrEqualTo(SemanticHighlighter.MaxColoredTokens + 3);
        });

    [Fact]
    public async Task Token_colors_cost_nothing_for_modifiers_no_selector_names_or_types_past_the_first_1000()
    {
        ColorThemeFile theme = (await CodeEditorColorTheme.LightPlus.Loaded).ShouldNotBeNull();
        // Modifiers that no selector names, and more types than are colored, as a server may choose them.
        string[] types = [.. Enumerable.Range(0, 100_000).Select(i => i == 0 ? "variable" : $"type{i}")];
        string[] modifiers = ["readonly", .. Enumerable.Range(1, 31).Select(i => $"modifier{i}")];
        var styles = new SemanticTokenStyles(theme, "plaintext", new SemanticTokensLegend(types, modifiers));
        IBrush? readOnly = styles.Foreground(0, 1);

        int others = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i < 100_000; i++)
        {
            if (styles.Foreground(0, 1 | (i << 1)) != readOnly)
                others++;
            if (styles.Foreground(SemanticTokenStyles.MaxTypes + i, 0) is not null)
                others++;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A color looked up for each combination took about 100 bytes each.
        allocated.ShouldBeLessThan(100_000);
        others.ShouldBe(0);
        readOnly.ShouldNotBeNull();
    }

    [Fact]
    public void Removed_text_is_read_back_to_its_last_line_break_within_the_shown_part()
    {
        // A line break and a hidden tail of ten million characters, as when they are deleted.
        var removed = new StringTextSource("ab\n" + new string('x', 10_000_000));

        SemanticHighlighter.ShownLastLineLength(removed).ShouldBe(VisibleText.LineLimit);
        SemanticHighlighter.ShownLastLineLength(new StringTextSource("ab\ncd")).ShouldBe(2);
        SemanticHighlighter.ShownLastLineLength(new StringTextSource("ab\r\n")).ShouldBe(0);
        SemanticHighlighter.ShownLastLineLength(new StringTextSource("abc")).ShouldBe(3);
    }

    [Fact]
    public Task Only_editors_that_show_a_document_ask_for_its_tokens_and_hover() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string hiddenPath = Path.Combine(AppContext.BaseDirectory, "hidden.txt");
            string shownPath = Path.Combine(AppContext.BaseDirectory, "shown.txt");
            using CodeDocument hidden = server.CreateDocument(CodeLanguage.PlainText, "class hidden", hiddenPath);
            using CodeDocument withoutWindow = server.CreateDocument(CodeLanguage.PlainText, "class alone");
            using CodeDocument shown = server.CreateDocument(CodeLanguage.PlainText, "class shown", shownPath);
            await Wait.UntilAsync(() => hidden.Diagnostics.Count == 1 && withoutWindow.Diagnostics.Count == 1);
            // An editor out of the visual tree shows nothing.
            var detached = new CodeEditor { Document = withoutWindow };

            using var window = new EditorWindow(new CodeEditor { Document = shown });

            await fake.WaitForAsync("semanticTokens/");
            // The server reads in order: a request about another document, sent before, would be in the log by now.
            fake.LinesStartingWith("semanticTokens/")
                .ShouldAllBe(line => line.Contains(new Uri(shownPath).AbsoluteUri));
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            GC.KeepAlive(detached);
        });
}
