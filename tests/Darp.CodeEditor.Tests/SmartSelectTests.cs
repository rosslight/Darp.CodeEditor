using System.Text.Json.Nodes;
using Avalonia.Input;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Shift+Alt+Right and Shift+Alt+Left expand and shrink the selection, as VS Code's smart select does: through the
/// server's selection ranges and the words, and without the server's, through the brackets and the words.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SmartSelectTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>VS Code's keys: on macOS Ctrl+Cmd+Shift with the arrows.</summary>
    private static KeyModifiers SmartSelect =>
        OperatingSystem.IsMacOS()
            ? KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift
            : KeyModifiers.Shift | KeyModifiers.Alt;

    [Fact]
    public Task Without_a_server_the_selection_grows_by_words_and_brackets_and_shrinks_back() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("{\"a\": [1, 2|2]}", CodeLanguage.Json);

            Expansions(window, 6)
                .ShouldBe([
                    "{\"a\": [1, «22»]}",
                    "{\"a\": [«1, 22»]}",
                    "{\"a\": «[1, 22]»}",
                    "{«\"a\": [1, 22]»}",
                    "«{\"a\": [1, 22]}»",
                    "«{\"a\": [1, 22]}»",
                ]);
            window.Press(Key.Left, SmartSelect);
            MarkedText.Of(window).ShouldBe("{«\"a\": [1, 22]»}");
            for (int i = 0; i < 5; i++)
                window.Press(Key.Left, SmartSelect);
            MarkedText.Of(window).ShouldBe("{\"a\": [1, 2|2]}");
        });

    [Theory]
    [InlineData(true, "fooBa|r_baz", "foo«Bar»_baz")]
    [InlineData(false, "fooBa|r_baz", "«fooBar_baz»")]
    [InlineData(true, "|fooBar", "«foo»Bar")]
    public Task The_selection_grows_from_the_part_of_a_word_between_its_humps_first(
        bool subwords,
        string marked,
        string expected
    ) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show(marked + " x", CodeLanguage.PlainText);
            window.Editor.SmartSelect = SmartSelectOptions.Default with { SelectSubwords = subwords };

            window.Press(Key.Right, SmartSelect);

            MarkedText.Of(window).ShouldBe(expected + " x");
        });

    [Theory]
    [InlineData(
        true,
        new[] { "a {\n    «b»\n}", "a {\n«    b»\n}", "a {«\n    b\n»}", "a «{\n    b\n}»", "«a {\n    b\n}»" }
    )]
    [InlineData(false, new[] { "a {\n    «b»\n}", "a {«\n    b\n»}", "a «{\n    b\n}»", "«a {\n    b\n}»" })]
    public Task A_selection_that_grows_onto_other_lines_first_takes_its_lines_whole(
        bool whitespace,
        string[] expected
    ) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("a {\n    |b\n}", CodeLanguage.Json);
            window.Editor.SmartSelect = SmartSelectOptions.Default with
            {
                SelectLeadingAndTrailingWhitespace = whitespace,
            };

            Expansions(window, expected.Length).ShouldBe(expected);
        });

    [Fact]
    public Task A_caret_move_ends_the_ranges_and_the_next_expansion_starts_from_the_new_caret() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("one two|", CodeLanguage.PlainText);

            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("one «two»");
            window.Press(Key.Home);
            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("«one» two");
        });

    [Fact]
    public Task With_a_server_the_selection_grows_by_its_ranges_and_the_words_but_not_by_brackets() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "[alpha be, 1]");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.Selection = new TextSelection(8);

            // The server's word, then its line; the brackets around "alpha be, 1" give no range.
            window.Press(Key.Right, SmartSelect);
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("[alpha «be», 1]");
            (await fake.WaitForAsync("selection ranges ")).ShouldBe("selection ranges 8");
            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("«[alpha be, 1]»");
        });

    [Fact]
    public Task The_servers_ranges_go_from_its_innermost_one_out_through_the_parents() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "alpha beta");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            JsonNode ranges = JsonNode.Parse(
                """
                {
                  "range": { "start": { "line": 0, "character": 6 }, "end": { "line": 0, "character": 8 } },
                  "parent": {
                    "range": { "start": { "line": 0, "character": 2 }, "end": { "line": 0, "character": 10 } }
                  }
                }
                """
            )!;
            using CodeDocument command = await CompletionTests.CommandAsync(
                server,
                fake,
                $"selection-ranges {ranges.ToJsonString()}",
                "set selection ranges"
            );
            window.Editor.Selection = new TextSelection(7);

            window.Press(Key.Right, SmartSelect);
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("alpha «be»ta");
            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("alpha «beta»");
            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("al«pha beta»");
            window.Press(Key.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("«alpha beta»");
        });

    /// <summary>
    /// Ranges that the server answers after the text changed are of text that is gone: they are dropped, and the next
    /// expansion asks again.
    /// </summary>
    [Fact]
    public Task The_servers_ranges_for_text_that_changed_meanwhile_are_dropped() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, new string('a', 100));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            using (
                await CompletionTests.CommandAsync(server, fake, "hold-selection-ranges", "holding selection ranges")
            )
            {
                window.Editor.Selection = new TextSelection(90);
                window.Press(Key.Right, SmartSelect);
                (await fake.WaitForAsync("selection ranges ")).ShouldBe("selection ranges 90");
            }

            document.Replace(new TextRange(0, 100), "b");
            using (
                await CompletionTests.CommandAsync(
                    server,
                    fake,
                    "release-selection-ranges",
                    "released selection ranges"
                )
            ) { }
            // Answered after the late ranges, which the editor has then dropped.
            window.Press(Key.Right, SmartSelect);

            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("«b»");
        });

    /// <summary>Expands the selection <paramref name="count"/> times, and the marked text after each.</summary>
    private static List<string> Expansions(EditorWindow window, int count)
    {
        var expansions = new List<string>();
        for (int i = 0; i < count; i++)
        {
            window.Press(Key.Right, SmartSelect);
            expansions.Add(MarkedText.Of(window));
        }
        return expansions;
    }
}
