using System.Text.Json.Nodes;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Keys = Avalonia.Input.Key;

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
            window.Press(Keys.Left, SmartSelect);
            MarkedText.Of(window).ShouldBe("{«\"a\": [1, 22]»}");
            for (int i = 0; i < 5; i++)
                window.Press(Keys.Left, SmartSelect);
            MarkedText.Of(window).ShouldBe("{\"a\": [1, 2|2]}");
        });

    /// <summary>
    /// The lines of the range before, from their first text to their last and then whole, as VS Code selects leading and
    /// trailing whitespace by default.
    /// </summary>
    [Fact]
    public Task A_selection_that_grows_onto_other_lines_first_takes_its_lines_whole() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("a {\n    |b\n}", CodeLanguage.Json);

            Expansions(window, 5)
                .ShouldBe([
                    "a {\n    «b»\n}",
                    "a {\n«    b»\n}",
                    "a {«\n    b\n»}",
                    "a «{\n    b\n}»",
                    "«a {\n    b\n}»",
                ]);
        });

    [Fact]
    public Task A_caret_move_ends_the_ranges_and_the_next_expansion_starts_from_the_new_caret() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("one two|", CodeLanguage.PlainText);

            window.Press(Keys.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("one «two»");
            window.Press(Keys.Home);
            window.Press(Keys.Right, SmartSelect);
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
            window.Press(Keys.Right, SmartSelect);
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("[alpha «be», 1]");
            (await fake.WaitForAsync("selection ranges ")).ShouldBe("selection ranges 8");
            window.Press(Keys.Right, SmartSelect);
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
            JsonNode ranges =
                JsonNode.Parse(
                    """
                    {
                      "range": { "start": { "line": 0, "character": 6 }, "end": { "line": 0, "character": 8 } },
                      "parent": {
                        "range": { "start": { "line": 0, "character": 2 }, "end": { "line": 0, "character": 10 } }
                      }
                    }
                    """
                ) ?? throw new InvalidDataException();
            using CodeDocument command = await CompletionTests.CommandAsync(
                server,
                fake,
                $"selection-ranges {ranges.ToJsonString()}",
                "set selection ranges"
            );
            window.Editor.Selection = new TextSelection(7);

            window.Press(Keys.Right, SmartSelect);
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("alpha «be»ta");
            window.Press(Keys.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("alpha «beta»");
            window.Press(Keys.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("al«pha beta»");
            window.Press(Keys.Right, SmartSelect);
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
                window.Press(Keys.Right, SmartSelect);
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
            window.Press(Keys.Right, SmartSelect);

            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("«b»");
        });

    /// <summary>Ranges that come once the editor shows another document belong to the document it no longer shows.</summary>
    [Fact]
    public Task The_servers_ranges_for_a_document_the_editor_no_longer_shows_are_dropped() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument first = server.CreateDocument(CodeLanguage.PlainText, "alpha beta");
            using CodeDocument second = server.CreateDocument(CodeLanguage.PlainText, "gamma delta");
            using var window = new EditorWindow(new CodeEditor { Document = first });
            await CompletionTests.StartServingAsync(server, first);
            await Wait.UntilAsync(() => second.Features is not null);
            using (
                await CompletionTests.CommandAsync(server, fake, "hold-selection-ranges", "holding selection ranges")
            )
            {
                window.Editor.Selection = new TextSelection(8);
                window.Press(Keys.Right, SmartSelect);
                (await fake.WaitForAsync("selection ranges ")).ShouldBe("selection ranges 8");
            }

            window.Editor.Document = second;
            window.Editor.Selection = new TextSelection(1);
            using (
                await CompletionTests.CommandAsync(
                    server,
                    fake,
                    "release-selection-ranges",
                    "released selection ranges"
                )
            ) { }
            window.Press(Keys.Right, SmartSelect);

            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("«gamma» delta");
        });

    /// <summary>
    /// A caret that moved away and back starts another expansion: the answer for the one before it is dropped, as it
    /// neither starts nor moves along the ranges of the new one.
    /// </summary>
    [Fact]
    public Task An_expansion_answered_after_the_caret_moved_away_and_back_is_dropped() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "alpha beta");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            using (
                await CompletionTests.CommandAsync(server, fake, "hold-selection-ranges", "holding selection ranges")
            )
            {
                window.Editor.Selection = new TextSelection(8);
                window.Press(Keys.Right, SmartSelect);
                await fake.WaitForAsync("selection ranges ");
                window.Press(Keys.Home);
                window.Editor.Selection = new TextSelection(8);
                window.Press(Keys.Right, SmartSelect);
                await fake.WaitForAsync("selection ranges ", 2);
            }

            using (
                await CompletionTests.CommandAsync(
                    server,
                    fake,
                    "release-selection-ranges",
                    "released selection ranges"
                )
            ) { }
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            // Both answers are in once a later one is, as the editor reads them in order, and handed on once the
            // dispatcher ran what they posted.
            await document.HoverAsync(document.CurrentSnapshot, new TextPosition(1, 1), CancellationToken.None);
            await EditorWindow.RunPostedJobsAsync();

            MarkedText.Of(window).ShouldBe("alpha «beta»");
        });

    /// <summary>
    /// Keys that come while the ranges are asked for wait for them, and go through them in their order, with the one
    /// request: Shrink, then Expand, selects the word. Expand first would end at the caret, as dropping both would.
    /// </summary>
    [Fact]
    public Task Keys_while_the_ranges_are_asked_for_go_through_them_in_order_with_one_request() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "alpha beta");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            using (
                await CompletionTests.CommandAsync(server, fake, "hold-selection-ranges", "holding selection ranges")
            )
            {
                window.Editor.Selection = new TextSelection(8);
                window.Press(Keys.Left, SmartSelect);
                window.Press(Keys.Right, SmartSelect);
                await CompletionTests.BarrierAsync(server, fake);
                fake.LinesStartingWith("selection ranges ").Count.ShouldBe(1);
            }

            using (
                await CompletionTests.CommandAsync(
                    server,
                    fake,
                    "release-selection-ranges",
                    "released selection ranges"
                )
            ) { }
            await Wait.UntilAsync(() => !window.Editor.Selection.IsEmpty);
            MarkedText.Of(window).ShouldBe("alpha «beta»");

            window.Press(Keys.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("«alpha beta»");
            fake.LinesStartingWith("selection ranges ").Count.ShouldBe(1);
        });

    /// <summary>A selection whose anchor alone moves is another selection: shrinking starts from it.</summary>
    [Fact]
    public Task Moving_only_the_anchor_ends_the_ranges() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("one tw|o", CodeLanguage.PlainText);
            window.Press(Keys.Right, SmartSelect);
            MarkedText.Of(window).ShouldBe("one «two»");

            // The text area's own selection, which leaves the caret where it is.
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            textArea.Selection = Selection.Create(textArea, 0, 7);
            window.Press(Keys.Left, SmartSelect);

            MarkedText.Of(window).ShouldBe("«one two»");
        });

    /// <summary>A rectangle selection keeps its own keys, which grow it on Windows and Linux.</summary>
    [Fact]
    public Task A_rectangle_selection_does_not_expand_and_stays_a_rectangle() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|ab\ncd\nef", CodeLanguage.PlainText);
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            textArea.Caret.Position = new TextViewPosition(2, 2);
            textArea.Selection = new RectangleSelection(
                textArea,
                new TextViewPosition(1, 1),
                new TextViewPosition(2, 2)
            );

            window.Press(Keys.Right, SmartSelect);

            textArea.Selection.ShouldBeOfType<RectangleSelection>();
        });

    [Theory]
    [InlineData(false, KeyModifiers.Shift | KeyModifiers.Alt)]
    [InlineData(true, KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)]
    public void By_default_Shift_Alt_Right_and_Left_expand_and_shrink_the_selection(bool mac, KeyModifiers modifiers)
    {
        var keymap = new CodeEditorKeymap(CodeEditorKeymap.DefaultBindings(mac));

        LineCommandTests.Commands(keymap, Keys.Right, modifiers).ShouldBe([CodeEditorCommands.SmartSelectExpand]);
        LineCommandTests.Commands(keymap, Keys.Left, modifiers).ShouldBe([CodeEditorCommands.SmartSelectShrink]);
    }

    /// <summary>Expands the selection <paramref name="count"/> times, and the marked text after each.</summary>
    private static List<string> Expansions(EditorWindow window, int count)
    {
        var expansions = new List<string>();
        for (int i = 0; i < count; i++)
        {
            window.Press(Keys.Right, SmartSelect);
            expansions.Add(MarkedText.Of(window));
        }
        return expansions;
    }
}
