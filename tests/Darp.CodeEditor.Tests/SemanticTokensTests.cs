using Avalonia.Media;

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

            // Inside the token on the first line, and before the token on the second.
            window.Editor.Selection = new TextSelection(2);
            window.Type("x");
            window.Editor.Selection = new TextSelection(11);
            window.Type(" ");
            window.Render();

            // "clxass one\n class two": the changed token goes, the moved one keeps its color.
            window.ColorAt(0).ShouldBe(s_text);
            window.ColorAt(2).ShouldBe(s_text);
            window.ColorAt(11).ShouldBe(s_text);
            window.ColorAt(12).ShouldBe(s_class);
            window.ColorAt(16).ShouldBe(s_class);
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
