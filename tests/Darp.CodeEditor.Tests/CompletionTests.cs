using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Completion: how it opens, filters and ranks the server's items, commits them as one step, resolves them, asks again,
/// and ends; in the app's flyout look with VS Code's icons, inside the window and in the editor's theme variant.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class CompletionTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Ctrl_Space_opens_the_servers_list() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Math", "Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x = ");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);

            // Control also on macOS, where Cmd takes its place for other keys.
            window.Press(Key.Space, KeyModifiers.Control);

            (await WaitForLabelsAsync(window)).ShouldBe(["Console", "Convert", "Math"]);
            (await fake.WaitForAsync("completion ")).ShouldEndWith(" 1 - 4");
        });

    [Fact]
    public Task A_trigger_character_opens_the_list() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Length", "Count"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "items");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(5);

            window.Type(".");

            (await WaitForLabelsAsync(window)).ShouldBe(["Count", "Length"]);
            (await fake.WaitForAsync("completion ")).ShouldEndWith(" 2 . 6");
        });

    [Fact]
    public Task A_trigger_character_typed_while_a_list_shows_asks_again_and_its_list_shows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument words = await SetCompletionAsync(server, fake, Items("date", "datetime"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("da");
            (await WaitForLabelsAsync(window)).ShouldBe(["date", "datetime"]);
            // The server answers the next request only once released: the word's list, filtered for the dot, has
            // nothing left by then.
            using CodeDocument hold = await CommandAsync(server, fake, "hold-completion", "holding completion");

            window.Type(".");

            string request = await fake.WaitForAsync("completion ", 2);
            request.ShouldEndWith(" 2 . 3");
            using CodeDocument members = await SetCompletionAsync(server, fake, Items("today", "year"));
            using CodeDocument release = await CommandAsync(server, fake, "release-completion", "released completion");
            // Ending completion would cancel the request, and its answer would never show.
            string cancel = $"cancel {Id(request)}";
            await Wait.UntilAsync(() => Labels(window).SequenceEqual(["today", "year"]) || fake.Lines.Contains(cancel));
            fake.Lines.ShouldNotContain(cancel);
            Labels(window).ShouldBe(["today", "year"]);
        });

    [Fact]
    public Task A_commit_character_that_is_also_a_trigger_character_commits_and_then_asks_again() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument words = await SetCompletionAsync(
                server,
                fake,
                new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject { ["label"] = "date", ["commitCharacters"] = new JsonArray(".") }
                    ),
                }
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("da");
            (await WaitForLabelsAsync(window)).ShouldBe(["date"]);
            using CodeDocument members = await SetCompletionAsync(server, fake, Items("today", "year"));

            window.Type(".");

            // As in VS Code: the dot commits the item, goes in, and asks again as a trigger character.
            document.Text.ShouldBe("date.");
            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 2 . 5");
            (await WaitForLabelsAsync(window)).ShouldBe(["today", "year"]);
        });

    [Fact]
    public Task Typing_at_a_words_end_or_its_first_character_opens_the_list_but_not_inside_a_word_a_number_or_a_comment() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("xenon", "xylophone"));
            const string text = "[\n  ,\n  ab,\n  cd,\n  \n]\n// note\n";
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            // JSON's comment color: the grammar knows the comment.
            int comment = text.IndexOf("note", StringComparison.Ordinal);
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return window.ColorAt(comment) == Color.Parse("#008000");
            });

            window.Editor.Selection = new TextSelection(4);
            window.Type("x");
            (await WaitForLabelsAsync(window)).ShouldBe(["xenon", "xylophone"]);
            window.Type("y");
            await WaitForLabelsAsync(window, ["xylophone"]);
            window.Press(Key.Escape);

            // As in VS Code: typing on at the word's end asks again, and so does a first character before a word.
            window.Type("l");
            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 1 - 7");
            await WaitForLabelsAsync(window, ["xylophone"]);
            window.Press(Key.Escape);
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("ab", StringComparison.Ordinal));
            window.Type("q");
            (await fake.WaitForAsync("completion ", 3)).ShouldEndWith(" 1 - 12");
            window.Press(Key.Escape);

            // Inside a word, in a number, and in a comment: no request.
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("cd", StringComparison.Ordinal) + 1);
            window.Type("m");
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("\n]", StringComparison.Ordinal));
            window.Type("12");
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("note", StringComparison.Ordinal) + 4);
            window.Type(" w");
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(3);
            document.Text.ShouldBe("[\n  xyl,\n  qab,\n  cmd,\n  12\n]\n// note w\n");
        });

    [Fact]
    public Task Backspace_alone_opens_nothing_and_typing_after_it_opens_the_list_again() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("print", "property"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("pri");
            await WaitForLabelsAsync(window, ["print"]);
            window.Press(Key.Escape);

            window.Press(Key.Back);
            window.Press(Key.Back);
            await BarrierAsync(server, fake);
            List(window).ShouldBeNull();
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);

            window.Type("r");

            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 1 - 2");
            await WaitForLabelsAsync(window, ["print", "property"]);
        });

    [Fact]
    public Task Typing_filters_and_ranks_the_list_on_the_client_with_the_matched_characters_in_bold() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [
                      { "label": "readConsole", "tags": [1] }, { "label": "Console" },
                      { "label": "Convert", "preselect": true }, { "label": "Math", "deprecated": true },
                      { "label": "Cos", "sortText": "0" } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x = ");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);

            window.Press(Key.Space, KeyModifiers.Control);

            // Before anything is typed: by sort text, then label; the preselected item selected.
            (await WaitForLabelsAsync(window)).ShouldBe(["Cos", "Console", "Convert", "Math", "readConsole"]);
            List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(2);
            // Deprecated by its flag or its tag: struck through.
            window.Render();
            LabelOf(window, "Math").TextDecorations.ShouldBeSameAs(TextDecorations.Strikethrough);
            LabelOf(window, "readConsole").TextDecorations.ShouldBeSameAs(TextDecorations.Strikethrough);
            LabelOf(window, "Console").TextDecorations.ShouldBeNull();

            window.Type("Con");

            // Prefixes first, equal scores in their order, the preselected one selected among the best; "Cos" lacks
            // the "n". The client filters: no other request.
            await WaitForLabelsAsync(window, ["Console", "Convert", "readConsole"]);
            List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(1);
            window.Render();
            RowRuns(window, "readConsole").ShouldBe([("read", false), ("Con", true), ("sole", false)]);
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
        });

    [Fact]
    public Task A_typo_of_two_neighbouring_characters_finds_the_item_while_typing_and_on_Ctrl_Space() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("print", "property"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("pr");
            await WaitForLabelsAsync(window, ["print", "property"]);

            window.Type("nit");

            await WaitForLabelsAsync(window, ["print"]);
            window.Render();
            RowRuns(window, "print").ShouldBe([("print", true)]);
            window.Press(Key.Escape);
            await OpenAsync(window, 5);
            Labels(window).ShouldBe(["print"]);
        });

    [Fact]
    public Task Typing_on_ranks_what_shows_so_that_a_list_of_over_2000_items_tolerates_a_typo_once_typing_shortened_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string[] labels = [.. Enumerable.Range(0, 2000).Select(i => $"x{i}"), "print"];
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items(labels));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("p");
            await WaitForLabelsAsync(window, ["print"]);

            window.Type("rni");

            await WaitForLabelsAsync(window, ["print"]);
            // Asked for there, the list is ranked whole, strictly: it has more than 2,000 items.
            window.Press(Key.Escape);
            window.Press(Key.Space, KeyModifiers.Control);
            await Wait.UntilAsync(() => List(window)?.Message == "No suggestions.");
        });

    [Fact]
    public Task Typing_filters_the_list_off_the_UI_thread_and_the_list_shows_the_newest_result() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert", "Math"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 0);

            // Typed, and no other work run on the UI thread since: the list still shows what it showed.
            TypeAlone(window, "C");
            TypeAlone(window, "o");
            TypeAlone(window, "n");
            Labels(window).ShouldBe(["Console", "Convert", "Math"]);

            // Then the filter of what was typed last.
            await WaitForLabelsAsync(window, ["Console", "Convert"]);
            Row(window, "Console").Label.Inlines.ShouldNotBeNull().OfType<Run>().First().Text.ShouldBe("Con");
        });

    [Fact]
    public Task While_a_filter_runs_a_commit_takes_the_shown_item_if_it_still_matches_and_otherwise_waits_or_types() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Con");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            // "Console" shows selected, and still matches "Cons": Enter commits it at once.
            await OpenAsync(window, 3);
            TypeAlone(window, "s");
            window.Press(Key.Enter);
            document.Text.ShouldBe("Console");

            // It does not match "Conv": Enter commits what the filter of "Conv" selects, once it has run.
            document.Text = "Con";
            await OpenAsync(window, 3);
            TypeAlone(window, "v");
            window.Press(Key.Enter);
            await Wait.UntilAsync(() => document.Text == "Convert");

            // A commit character, which the server gives every item, goes in as text then.
            document.Text = "Con";
            await OpenAsync(window, 3);
            TypeAlone(window, "v");
            TypeAlone(window, ";");
            document.Text.ShouldBe("Conv;");
        });

    [Fact]
    public Task An_edit_that_leaves_the_caret_drops_the_commit_that_waits_for_a_filter() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Con");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 3);
            // "Console" shows selected and does not match "Conv": Enter waits for the filter of "Conv".
            TypeAlone(window, "v");
            PressAlone(window, Key.Enter);

            // As code replaces the "v" with an "s": the caret stays where it was.
            document.Replace(new TextRange(3, 1), "s");
            window.Editor.Selection.Caret.ShouldBe(4);

            // The list then shows the filter of "Cons", and nothing is committed.
            await Wait.UntilAsync(() => document.Text != "Cons" || Labels(window).SequenceEqual(["Console"]));
            document.Text.ShouldBe("Cons");
        });

    [Fact]
    public Task An_edit_by_another_editor_of_the_document_drops_the_commit_that_waits_for_a_filter() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Con\nx");
            var editor = new CodeEditor { Document = document };
            var other = new CodeEditor { Document = document };
            Grid.SetColumn(other, 1);
            using var window = new EditorWindow(
                editor,
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { editor, other } }
            );
            await OpenAsync(window, 3);
            TypeAlone(window, "v");
            PressAlone(window, Key.Enter);

            // The other editor types on the next line, without taking the keyboard focus.
            other.Selection = new TextSelection(document.Text.Length);
            TextArea otherTextArea = EditorWindow.PartOf(other).TextArea;
            otherTextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "y" });
            document.Text.ShouldBe("Conv\nxy");

            await Wait.UntilAsync(() => document.Text != "Conv\nxy" || Labels(window).SequenceEqual(["Convert"]));
            document.Text.ShouldBe("Conv\nxy");
        });

    [Fact]
    public Task An_edit_that_starts_far_before_the_caret_costs_the_UI_thread_only_what_matching_reads() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options);
            // A line of two million characters, and an item whose edit starts at its start: its typed text is the line.
            const int length = 2_000_000;
            string label = "w" + new string('a', 300);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject
                        {
                            ["label"] = label,
                            ["textEdit"] = new JsonObject
                            {
                                ["range"] = new JsonObject
                                {
                                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                                    ["end"] = new JsonObject { ["line"] = 0, ["character"] = length },
                                },
                                ["newText"] = "word",
                            },
                        }
                    ),
                }
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "w" + new string('a', length));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, length);

            // The caret moves on in the line's collapsed tail, where only code puts it, and the list filters again;
            // Enter, while that filter runs, checks the item against the text typed.
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.Editor.Selection = new TextSelection(length + 1);
            long moving = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            PressAlone(window, Key.Enter);
            long committing = GC.GetAllocatedBytesForCurrentThread() - before;

            document.Text.ShouldBe("word");
            // A copy of the line takes two bytes a character, 4 MB; what matching reads, a few hundred bytes. The
            // commit's edit of the line takes the editor about a third of a byte a character.
            moving.ShouldBeLessThan(1_000_000);
            committing.ShouldBeLessThan(length);
        });

    [Fact]
    public Task The_word_an_item_without_an_edit_replaces_reaches_back_1000_characters() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options);
            string label = new string('a', 200) + "bc";
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items(label));
            // A word of two million characters: as a hovered word, it reaches back from the caret 1,000 of them.
            const int length = 2_000_000;
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, new string('a', length));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, length);

            window.Press(Key.Enter);

            document.Text.ShouldBe(new string('a', length - 1_000) + label);
        });

    [Fact]
    public Task A_read_only_editor_neither_completes_nor_commits() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("alpha"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "a");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await OpenAsync(window, 1);

            editor.IsReadOnly = true;

            List(window).ShouldBeNull();
            window.Press(Key.Tab);
            window.Press(Key.Space, KeyModifiers.Control);
            await BarrierAsync(server, fake);
            window.Render();
            List(window).ShouldBeNull();
            document.Text.ShouldBe("a");
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
        });

    [Fact]
    public Task Enter_Tab_a_click_and_commit_characters_commit_as_one_step_that_the_user_undoes() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            // "Write" commits with "(", "WriteLine" with the server's ";".
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "WriteLine", "insertText": "WriteLine()" },
                                 { "label": "Write", "commitCharacters": ["("] } ] }
                    """
                )
            );
            const string text = "Console.Wr";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 10);
            window.Press(Key.Enter);
            document.Text.ShouldBe("Console.Write");
            window.Editor.Selection.Caret.ShouldBe(13);
            document.Undo();
            document.Text.ShouldBe(text);

            await OpenAsync(window, 10);
            window.Press(Key.Down);
            window.Press(Key.Tab);
            document.Text.ShouldBe("Console.WriteLine()");
            document.Undo();
            document.Text.ShouldBe(text);

            await OpenAsync(window, 10);
            window.Click(RowCenter(window, 1), MouseButton.Left);
            document.Text.ShouldBe("Console.WriteLine()");
            document.Undo();

            // Its own commit character commits "Write", and then goes in.
            await OpenAsync(window, 10);
            window.Type("(");
            document.Text.ShouldBe("Console.Write(");
            document.Undo();
            document.Text.ShouldBe("Console.Write");
            document.Undo();
            document.Text.ShouldBe(text);

            // The server's commit character commits "WriteLine", which names none of its own.
            await OpenAsync(window, 10);
            window.Press(Key.Down);
            window.Type(";");
            document.Text.ShouldBe("Console.WriteLine();");
            document.Undo();
            document.Undo();
            document.Text.ShouldBe(text);

            // But not "Write", whose own replace the server's: nothing matches any more, and the caret left the word,
            // which ends even the list asked for by key, as in VS Code.
            await OpenAsync(window, 10);
            window.Type(";");
            document.Text.ShouldBe("Console.Wr;");
            await Wait.UntilAsync(() => List(window) is null);
        });

    [Fact]
    public Task Edits_replace_their_range_also_with_what_was_typed_since_and_an_insert_replace_edit_inserts_or_replaces() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "log.wriXYZ");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using (
                await SetCompletionAsync(
                    server,
                    fake,
                    Parse(
                        """
                        { "items": [
                          { "label": "write", "textEdit": { "range": { "start": { "line": 0, "character": 4 },
                            "end": { "line": 0, "character": 7 } }, "newText": "write" } },
                          { "label": "writeAll", "textEdit": {
                            "insert": { "start": { "line": 0, "character": 4 }, "end": { "line": 0, "character": 7 } },
                            "replace": { "start": { "line": 0, "character": 4 }, "end": { "line": 0, "character": 10 } },
                            "newText": "writeAll" } } ] }
                        """
                    )
                )
            )
            {
                await OpenAsync(window, 7);
                window.Press(Key.Enter);
                document.Text.ShouldBe("log.writeXYZ");
                document.Undo();
                // A text edit's range is also what replacing goes over.
                await OpenAsync(window, 7);
                window.Press(Key.Tab);
                document.Text.ShouldBe("log.writeXYZ");
                document.Undo();

                // An insert and replace edit: Enter inserts over its insert range, Tab replaces its replace range.
                await OpenAsync(window, 7);
                window.Press(Key.Down);
                window.Press(Key.Enter);
                document.Text.ShouldBe("log.writeAllXYZ");
                document.Undo();
                await OpenAsync(window, 7);
                window.Press(Key.Down);
                window.Press(Key.Tab);
                document.Text.ShouldBe("log.writeAll");
                window.Editor.Selection.ShouldBe(new TextSelection(12));
            }

            document.Text = "log.w";
            using (
                await SetCompletionAsync(
                    server,
                    fake,
                    Parse(
                        """
                        { "items": [ { "label": "write", "textEdit": { "range": { "start": { "line": 0, "character": 4 },
                          "end": { "line": 0, "character": 5 } }, "newText": "write" } } ] }
                        """
                    )
                )
            )
            {
                await OpenAsync(window, 5);
                window.Type("ri");
                window.Press(Key.Enter);
                document.Text.ShouldBe("log.write");
            }
        });

    [Fact]
    public Task Enter_inserts_before_the_rest_of_the_word_and_Tab_and_Shift_Enter_replace_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("println"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "print");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            // Inside a word, Enter inserts and keeps the rest of the word.
            await OpenAsync(window, 3);
            window.Press(Key.Enter);
            document.Text.ShouldBe("printlnnt");
            window.Editor.Selection.ShouldBe(new TextSelection(7));
            document.Undo();

            // Tab replaces the rest of the word, and so does Shift+Enter, also after typing on.
            await OpenAsync(window, 3);
            window.Press(Key.Tab);
            document.Text.ShouldBe("println");
            window.Editor.Selection.ShouldBe(new TextSelection(7));
            document.Text = "print";
            await OpenAsync(window, 3);
            window.Type("n");
            await WaitForLabelsAsync(window, ["println"]);
            window.Press(Key.Enter, KeyModifiers.Shift);
            document.Text.ShouldBe("println");

            // At a word's end, both do the same.
            document.Text = "pri";
            await OpenAsync(window, 3);
            window.Press(Key.Enter);
            document.Text.ShouldBe("println");
            document.Text = "pri";
            await OpenAsync(window, 3);
            window.Press(Key.Tab);
            document.Text.ShouldBe("println");

            // Without a list, the keys go on: Enter makes a new line, and Tab indents.
            window.Press(Key.Enter);
            window.Press(Key.Tab);
            document.Text.ShouldStartWith("println\n");
            document.Text.Length.ShouldBeGreaterThan("println\n".Length);
            document.Text.Trim().ShouldBe("println");
        });

    [Fact]
    public Task Tab_bound_to_accepting_the_selected_item_inserts_as_in_VS_Code() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("println"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "print");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Keymap = CodeEditorKeymap.FromJson(
                """[ { "key": "tab", "command": "acceptSelectedSuggestion" } ]"""
            );

            await OpenAsync(window, 3);
            window.Press(Key.Tab);
            document.Text.ShouldBe("printlnnt");
            document.Undo();
            await OpenAsync(window, 3);
            window.Press(Key.Enter, KeyModifiers.Shift);
            document.Text.ShouldBe("println");
        });

    [Fact]
    public Task An_edit_before_where_completion_was_asked_asks_again_so_that_the_commit_replaces_the_word() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, " Co");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 3);

            // As another editor of the document types at the line's start: the caret moves with the text.
            document.Replace(new TextRange(0, 0), "x");

            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 1 - 4");
            (await WaitForLabelsAsync(window)).ShouldBe(["Console"]);
            window.Press(Key.Enter);
            document.Text.ShouldBe("x Console");
        });

    [Fact]
    public Task The_selected_items_detail_and_documentation_show_beside_the_list_once_resolved() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [
                      { "label": "Console", "kind": 7, "data": { "detail": "class System.Console",
                        "documentation": { "kind": "markdown", "value": "Represents **the console**." } } },
                      { "label": "Convert", "kind": 7, "data": { "detail": "class System.Convert" } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Co");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 2);

            await Wait.UntilAsync(() => DetailTexts(window).Contains("class System.Console"));
            DetailTexts(window).ShouldContain("Represents the console.");
            Details(window)
                .ShouldNotBeNull()
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .SelectMany(t => t.Inlines?.OfType<Run>() ?? [])
                .ShouldContain(run => run.Text == "the console" && run.FontWeight == FontWeight.Bold);
            window.Press(Key.Down);
            await Wait.UntilAsync(() => DetailTexts(window).Contains("class System.Convert"));
            // The detail is plain text, dimmed in the theme's descriptionForeground, not colored as code.
            window
                .RenderAndCountPixels(Color.Parse("#717171"), BoundsInWindow(Details(window)!))
                .ShouldBeGreaterThan(20);
            fake.LinesStartingWith("resolve ").Select(l => l.Split(' ')[^1]).ShouldBe(["Console", "Convert"]);
        });

    [Fact]
    public Task Selecting_an_item_whose_resolve_is_done_cancels_the_resolve_of_the_one_left() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "alpha", "data": { "detail": "the alpha" } },
                                 { "label": "beta", "data": { "detail": "the beta" } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 0);
            await Wait.UntilAsync(() => DetailTexts(window).Contains("the alpha"));
            using CodeDocument hold = await CommandAsync(server, fake, "hold-resolve", "holding resolve");
            window.Press(Key.Down);
            string beta = Id(await fake.WaitForAsync("resolve ", 2));

            // Back to "alpha", resolved already.
            window.Press(Key.Up);

            await BarrierAsync(server, fake);
            fake.Lines.ShouldContain($"cancel {beta}");
            DetailTexts(window).ShouldContain("the alpha");
        });

    [Theory]
    [InlineData(420, "below")]
    [InlineData(720, "beside, narrower")]
    [InlineData(900, "beside")]
    public Task The_details_fit_beside_the_list_where_there_is_room_and_below_it_otherwise(int width, string where) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string documentation = string.Join(" ", Enumerable.Repeat("Writes the **value** to the output.", 12));
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject
                        {
                            ["label"] = "WriteLine",
                            ["data"] = new JsonObject
                            {
                                ["detail"] = "void Console.WriteLine(string value)",
                                ["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation },
                            },
                        }
                    ),
                }
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "    Wr");
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: width, height: 500);

            await OpenAsync(window, 6);
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return DetailTexts(window).Contains("void Console.WriteLine(string value)");
            });

            Rect list = BoundsInWindow(Flyout(window).ShouldNotBeNull());
            Rect details = BoundsInWindow(Details(window).ShouldNotBeNull());
            var client = new Rect(window.Window.ClientSize);
            client.Contains(list).ShouldBeTrue($"{list} in {client}");
            client.Contains(details).ShouldBeTrue($"{details} in {client}");
            // The documentation wraps to the width the details have, and scrolls where it is taller.
            foreach (TextBlock text in Details(window)!.GetVisualDescendants().OfType<TextBlock>())
            {
                Rect bounds = BoundsInWindow(text);
                (bounds.Left >= details.Left && bounds.Right <= details.Right).ShouldBeTrue($"{bounds} in {details}");
            }
            // The list stays at the word it completes, where the window has room for it: its labels line up with it.
            Point word = window.PointAt(4);
            if (width > CompletionView.DefaultWidth + word.X)
                (list.Left + 1 + CompletionListBox.LabelInset).ShouldBeInRange(word.X - 10, word.X);
            if (where == "below")
            {
                details.Top.ShouldBeGreaterThanOrEqualTo(list.Bottom);
            }
            else
            {
                details.Left.ShouldBeGreaterThanOrEqualTo(list.Right);
                details.Top.ShouldBe(list.Top, tolerance: 1);
            }
        });

    [Fact]
    public Task A_long_detail_documentation_and_label_show_only_as_much_as_a_hover() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string label = "Long" + new string('x', 5_000);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                new JsonObject
                {
                    ["items"] = new JsonArray(
                        new JsonObject
                        {
                            ["label"] = label,
                            ["data"] = new JsonObject
                            {
                                ["detail"] = string.Join("\n", Enumerable.Range(0, 2_000).Select(i => $"line {i}")),
                                ["documentation"] = new JsonObject
                                {
                                    ["kind"] = "markdown",
                                    ["value"] = string.Concat(Enumerable.Repeat("**word** ", 2_000)),
                                },
                            },
                        }
                    ),
                }
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 0);
            await Wait.UntilAsync(() => DetailTexts(window).Count > 0);

            // The detail and the documentation share a hover's budget: the detail alone uses up its lines.
            IReadOnlyList<string> details = DetailTexts(window);
            details.Sum(text => text.Length).ShouldBeLessThanOrEqualTo(HoverBudget.MaxCharacters + 1);
            details[0].Split('\n').Length.ShouldBe(HoverBudget.MaxLines);
            details[^1].ShouldEndWith("…");
            // A row shows the start of a long label.
            string shown = LabelOf(window, label).Inlines.ShouldNotBeNull().Text ?? "";
            shown.Length.ShouldBe(257);
            shown.ShouldEndWith("…");
        });

    [Fact]
    public Task Additional_edits_commit_with_the_item_in_one_step() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, ItemWithImport());
            const string text = "x\nCon";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 5);
            await Wait.UntilAsync(() => DetailTexts(window).Contains("class System.Console"));
            window.Press(Key.Enter);

            document.Text.ShouldBe("using System;\nx\nConsole");
            window.Editor.Selection.Caret.ShouldBe(document.Text.Length);
            document.Undo();
            document.Text.ShouldBe(text);
        });

    [Fact]
    public Task Additional_edits_that_come_after_the_commit_join_its_step_while_it_is_the_last_one() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, ItemWithImport());
            const string text = "x\nCon";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });

            using (await CommandAsync(server, fake, "hold-resolve", "holding resolve"))
            {
                await OpenAsync(window, 5);
                await fake.WaitForAsync("resolve ");
                window.Press(Key.Enter);
                document.Text.ShouldBe("x\nConsole");
            }
            using (await CommandAsync(server, fake, "release-resolve", "released resolve"))
            {
                await Wait.UntilAsync(() => document.Text == "using System;\nx\nConsole");
                document.Undo();
                document.Text.ShouldBe(text);
            }

            // The user typed on before they came: they are dropped.
            using (await CommandAsync(server, fake, "hold-resolve", "holding resolve", 2))
            {
                await OpenAsync(window, 5);
                await fake.WaitForAsync("resolve ", 2);
                window.Press(Key.Enter);
                window.Type("!");
            }
            using (await CommandAsync(server, fake, "release-resolve", "released resolve", 2))
            {
                // A list shows after the late answer: the client has read it by then.
                await OpenAsync(window, document.Text.Length);
                document.Text.ShouldBe("x\nConsole!");
            }
        });

    [Fact]
    public Task An_incomplete_list_is_asked_for_again_as_the_user_types() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse("""{ "isIncomplete": true, "items": [ { "label": "alpha" }, { "label": "alps" } ] }""")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 0);
            window.Type("a");
            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 3 - 1");
            window.Type("l");
            (await fake.WaitForAsync("completion ", 3)).ShouldEndWith(" 3 - 2");

            (await WaitForLabelsAsync(window)).ShouldBe(["alpha", "alps"]);
        });

    [Fact]
    public Task A_newer_request_and_Escape_cancel_the_request_under_way_and_another_selection_its_resolve() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow x");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await StartServingAsync(server, document);

            // The server never answers completion in this document.
            window.Editor.Selection = new TextSelection(6);
            window.Press(Key.Space, KeyModifiers.Control);
            string first = Id(await fake.WaitForAsync("completion "));
            window.Type(".");
            string second = Id(await fake.WaitForAsync("completion ", 2));
            await fake.WaitForAsync($"cancel {first}");
            window.Press(Key.Escape);
            await fake.WaitForAsync($"cancel {second}");
            List(window).ShouldBeNull();

            document.Text = "x";
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("alpha", "beta"));
            using CodeDocument hold = await CommandAsync(server, fake, "hold-resolve", "holding resolve");
            await OpenAsync(window, 0);
            string alpha = Id(await fake.WaitForAsync("resolve "));
            window.Press(Key.Down);
            await fake.WaitForAsync("resolve ", 2);
            await fake.WaitForAsync($"cancel {alpha}");
        });

    [Fact]
    public Task The_document_an_editor_shows_goes_first_as_documents_nobody_shows_ask_nothing_and_left_requests_are_cancelled() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("alpha"));
            string large = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"));
            CodeDocument[] background =
            [
                .. Enumerable.Range(0, 4).Select(_ => server.CreateDocument(CodeLanguage.PlainText, large)),
            ];
            string slowPath = Path.Combine(AppContext.BaseDirectory, "slow.txt");
            string shownPath = Path.Combine(AppContext.BaseDirectory, "shown.txt");
            using CodeDocument slow = server.CreateDocument(CodeLanguage.PlainText, "slow " + large, slowPath);
            using CodeDocument shown = server.CreateDocument(CodeLanguage.PlainText, "a", shownPath);
            var left = new CodeEditor { Document = slow };
            var right = new CodeEditor { Document = shown };
            Grid.SetColumn(right, 1);
            using var window = new EditorWindow(
                left,
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { left, right } }
            );

            // The other editor's request is under way, and the server never answers it.
            left.Selection = new TextSelection(4);
            window.Press(Key.Space, KeyModifiers.Control);
            string waiting = Id(await fake.WaitForAsync("completion "));

            right.Focus();
            right.Selection = new TextSelection(1);
            window.Press(Key.Space, KeyModifiers.Control);

            (await WaitForLabelsAsync(window)).ShouldBe(["alpha"]);
            await fake.WaitForAsync($"cancel {waiting}");
            string[] asked = [.. fake.LinesStartingWith("completion ").Select(l => l.Split(' ')[2])];
            asked.ShouldBe([new Uri(slowPath).AbsoluteUri, new Uri(shownPath).AbsoluteUri]);
            foreach (CodeDocument document in background)
                document.Dispose();
        });

    [Fact]
    public Task Icons_take_the_color_themes_symbol_colors_and_VS_Codes_where_it_sets_none() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            // A method and a class, below a third item, so that neither is on the selection's background.
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "a", "kind": 1 }, { "label": "bMethod", "kind": 2 },
                                 { "label": "cClass", "kind": 7 } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = editor };
            using var window = new EditorWindow(editor, scope);

            // Light+ sets none: VS Code's light colors.
            await WaitForBackgroundAsync(window, Colors.White);
            await OpenAsync(window, 0);
            IconPixels(window, 1, Color.Parse("#652D90")).ShouldBeGreaterThan(10);
            IconPixels(window, 2, Color.Parse("#D67E00")).ShouldBeGreaterThan(10);
            window.Press(Key.Escape);

            // Dark+ sets none either: VS Code's dark colors.
            scope.RequestedThemeVariant = ThemeVariant.Dark;
            await WaitForBackgroundAsync(window, Color.Parse("#1E1E1E"));
            await OpenAsync(window, 0);
            IconPixels(window, 1, Color.Parse("#B180D7")).ShouldBeGreaterThan(10);
            IconPixels(window, 2, Color.Parse("#EE9D28")).ShouldBeGreaterThan(10);
            window.Press(Key.Escape);

            // A theme with a method color of its own.
            editor.ColorTheme = new CodeEditorColorTheme
            {
                Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
            };
            await WaitForBackgroundAsync(window, Colors.White);
            await OpenAsync(window, 0);
            IconPixels(window, 1, Color.Parse("#FF0080")).ShouldBeGreaterThan(10);
            IconPixels(window, 2, Color.Parse("#D67E00")).ShouldBeGreaterThan(10);
        });

    [Fact]
    public Task The_list_takes_the_color_theme_of_the_editors_theme_variant_and_stays_inside_the_window() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Items([.. Enumerable.Range(0, 30).Select(i => $"item{i:D2}")])
            );
            string text = string.Concat(Enumerable.Range(0, 8).Select(_ => "\n")) + new string(' ', 40);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            // A dark editor in a light window, near the window's bottom and right edges.
            using var window = new EditorWindow(
                editor,
                new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = editor },
                width: 420,
                height: 240
            );

            await OpenAsync(window, text.Length);

            PopupFrame flyout = Flyout(window).ShouldNotBeNull();
            window.Window.ActualThemeVariant.ShouldBe(ThemeVariant.Light);
            flyout.ActualThemeVariant.ShouldBe(ThemeVariant.Dark);
            Color background = flyout.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color;
            (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B).ShouldBeLessThan(128);
            Rect bounds = BoundsInWindow(flyout);
            window.RenderAndCountPixels(background, bounds).ShouldBeGreaterThan(100);
            new Rect(window.Window.ClientSize).Contains(bounds).ShouldBeTrue($"{bounds} in {window.Window.ClientSize}");
            // No room below the caret's line: above it.
            bounds.Bottom.ShouldBeLessThanOrEqualTo(window.PointAt(text.Length).Y);
        });

    [Fact]
    public Task Completion_ends_on_Escape_leaving_the_word_focus_loss_scrolling_and_a_server_change() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console"));
            string text = "Con\n" + string.Concat(Enumerable.Range(0, 200).Select(i => $"line{i}\n"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });

            await OpenAsync(window, 3);
            window.Press(Key.Escape);
            List(window).ShouldBeNull();

            await OpenAsync(window, 3);
            window.Press(Key.Home);
            List(window).ShouldBeNull();

            await OpenAsync(window, 3);
            button.Focus();
            window.Layout();
            List(window).ShouldBeNull();

            editor.Focus();
            await OpenAsync(window, 3);
            editor.ScrollTo(new TextRange(text.Length, 0));
            window.Layout();
            List(window).ShouldBeNull();

            editor.ScrollTo(new TextRange(0, 0));
            window.Layout();
            await OpenAsync(window, 3);
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");
            await Wait.UntilAsync(() => List(window) is null);
        });

    [Fact]
    public Task A_click_that_moves_the_caret_ends_completion_and_asks_nothing() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("print", "property"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("pri");
            await WaitForLabelsAsync(window, ["print"]);

            // Between "p" and "r": a quarter of a character right of their boundary.
            window.Click(window.PointAt(1) - new Point(window.TextView.WideSpaceWidth / 4, 0), MouseButton.Left);

            window.Editor.Selection.ShouldBe(new TextSelection(1));
            List(window).ShouldBeNull();
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
        });

    [Theory]
    [InlineData(Key.Down, 2)]
    [InlineData(Key.PageDown, 2)]
    [InlineData(Key.Up, 0)]
    [InlineData(Key.PageUp, 0)]
    public Task With_one_item_the_keys_that_select_move_the_caret_and_leaving_the_line_ends_completion(
        Key key,
        int line
    ) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("ZeroDivisionError"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "above\n\nbelow");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(6);
            window.Type("Zero");
            await WaitForLabelsAsync(window, ["ZeroDivisionError"]);

            // As in VS Code: selecting needs more than one item, so the key goes on to the text.
            window.Press(key);

            window.Editor.CaretPosition.Line.ShouldBe(line + 1);
            List(window).ShouldBeNull();
            document.Text.ShouldBe("above\nZero\nbelow");
        });

    /// <summary>Makes the fake server answer completion with <paramref name="result"/>, until the document is disposed.</summary>
    [Fact]
    public Task Asked_by_key_an_empty_answer_says_No_suggestions_until_the_next_key() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items());
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(3);

            window.Press(Key.Space, KeyModifiers.Control);

            await Wait.UntilAsync(() => List(window)?.Message == "No suggestions.");
            Labels(window).ShouldBeEmpty();
            window.Press(Key.Right);
            List(window).ShouldBeNull();
            // Escape closes it too, and goes no further.
            window.Press(Key.Space, KeyModifiers.Control);
            await Wait.UntilAsync(() => List(window)?.Message == "No suggestions.");
            window.Press(Key.Escape);
            List(window).ShouldBeNull();
        });

    [Fact]
    public Task Opened_by_typing_an_empty_incomplete_answer_shows_nothing_and_typing_on_in_its_word_asks_no_more() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            // As a server answers in a comment or a string: nothing, but incomplete.
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                new JsonObject { ["isIncomplete"] = true, ["items"] = new JsonArray() }
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            window.Type("a");
            await fake.WaitForAsync("completion ");
            await BarrierAsync(server, fake);
            window.Type("bcd");
            await BarrierAsync(server, fake);

            List(window).ShouldBeNull();
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);

            // Backspace asks nothing; typing after it asks again, and so does a new word.
            window.Press(Key.Back);
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
            window.Type("x");
            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 1 - 4");
            await BarrierAsync(server, fake);
            window.Type("y");
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(2);
            window.Type(" z");
            (await fake.WaitForAsync("completion ", 3)).ShouldEndWith(" 1 - 7");
        });

    [Fact]
    public Task Asked_by_key_a_list_that_typing_filters_empty_stays_frozen_while_the_caret_is_in_a_word() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("print", "property"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "pri");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 3);

            window.Type("zzzz");

            // As in VS Code: the list stays as it was, without the matched characters in bold.
            await WaitForFrozenAsync(window, "print");
            Labels(window).ShouldBe(["print"]);
            List(window).ShouldNotBeNull().Message.ShouldBeNull();
            // Typing back to a match shows the matches again; the caret leaving the word ends it.
            for (int i = 0; i < 4; i++)
                window.Press(Key.Back);
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return RowRuns(window, "print").SequenceEqual([("pri", true), ("nt", false)]);
            });
            window.Type("zz");
            await WaitForFrozenAsync(window, "print");
            window.Type(" ");
            await Wait.UntilAsync(() => List(window) is null);
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
        });

    [Fact]
    public Task Asked_by_key_a_list_with_no_match_for_the_word_says_No_suggestions_and_typing_on_keeps_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("alpha", "beta"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "q");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(1);

            window.Press(Key.Space, KeyModifiers.Control);

            await Wait.UntilAsync(() => List(window)?.Message == "No suggestions.");
            window.Type("q");
            await BarrierAsync(server, fake);
            window.Layout();
            List(window).ShouldNotBeNull().Message.ShouldBe("No suggestions.");
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
        });

    [Fact]
    public Task A_frozen_list_commits_its_item_over_what_was_typed_by_key_and_click_but_not_by_commit_characters() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse("""{ "items": [ { "label": "print", "commitCharacters": ["("] } ] }""")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "pri");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 3);
            window.Type("zz");
            await WaitForFrozenAsync(window, "print");
            window.Press(Key.Enter);
            document.Text.ShouldBe("print");

            document.Text = "pri";
            await OpenAsync(window, 3);
            window.Type("zz");
            await WaitForFrozenAsync(window, "print");
            window.Click(RowCenter(window, 0), MouseButton.Left);
            document.Text.ShouldBe("print");

            // Also while the filter of what was typed last still runs: once it has, the item commits.
            document.Text = "pri";
            await OpenAsync(window, 3);
            TypeAlone(window, "z");
            PressAlone(window, Key.Enter);
            await Wait.UntilAsync(() => document.Text == "print");

            // As in VS Code, its commit characters go in as text.
            document.Text = "pri";
            await OpenAsync(window, 3);
            window.Type("zz");
            await WaitForFrozenAsync(window, "print");
            window.Type("(");
            document.Text.ShouldBe("prizz(");
        });

    [Fact]
    public Task Ctrl_Space_toggles_the_details_while_the_list_shows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse("""{ "items": [ { "label": "Console", "detail": "class System.Console" } ] }""")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Co");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 2);
            await Wait.UntilAsync(() => DetailTexts(window).Contains("class System.Console"));

            window.Press(Key.Space, KeyModifiers.Control);

            Details(window).ShouldBeNull();
            // The selected row shows the detail instead, and the list stays.
            await Wait.UntilAsync(() => Row(window, "Console").Right.Text == "class System.Console");
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
            window.Press(Key.Space, KeyModifiers.Control);
            await Wait.UntilAsync(() => DetailTexts(window).Contains("class System.Console"));
        });

    /// <summary>
    /// Starts the server, and waits until <paramref name="document"/> is open in it: editors ask only then, once the
    /// start's settings went out.
    /// </summary>
    internal static async Task StartServingAsync(LanguageServer server, CodeDocument document)
    {
        await server.StartAsync();
        await Wait.UntilAsync(() => document.Features is not null);
    }

    internal static async Task<CodeDocument> SetCompletionAsync(LanguageServer server, FakeServer fake, JsonNode result)
    {
        int set = fake.LinesStartingWith("set completion").Count;
        CodeDocument command = server.CreateDocument(FakeServer.Commands, $"completion {result.ToJsonString()}");
        await fake.WaitForAsync("set completion", set + 1);
        return command;
    }

    /// <summary>Runs a command of the fake server, and waits for its <paramref name="count"/>-th line.</summary>
    internal static async Task<CodeDocument> CommandAsync(
        LanguageServer server,
        FakeServer fake,
        string command,
        string line,
        int count = 1
    )
    {
        CodeDocument document = server.CreateDocument(FakeServer.Commands, command);
        await fake.WaitForAsync(line, count);
        return document;
    }

    /// <summary>
    /// Waits until the fake server has read everything sent so far: it reads in order, so a request sent before is
    /// in its log by then.
    /// </summary>
    internal static async Task BarrierAsync(LanguageServer server, FakeServer fake)
    {
        string mark = $"barrier {Guid.NewGuid():N}";
        using CodeDocument barrier = server.CreateDocument(FakeServer.Commands, $"stderr {mark}");
        await fake.WaitForAsync(mark);
    }

    /// <summary>Puts the caret at <paramref name="offset"/>, presses Ctrl+Space and waits for the list.</summary>
    internal static async Task OpenAsync(EditorWindow window, int offset)
    {
        window.Editor.Selection = new TextSelection(offset);
        window.Press(Key.Space, KeyModifiers.Control);
        await WaitForLabelsAsync(window);
    }

    /// <summary>A complete list of items with these labels.</summary>
    internal static JsonObject Items(params string[] labels) =>
        new()
        {
            ["isIncomplete"] = false,
            ["items"] = new JsonArray([.. labels.Select(label => (JsonNode)new JsonObject { ["label"] = label })]),
        };

    internal static JsonNode Parse(string json) => JsonNode.Parse(json) ?? throw new InvalidDataException();

    /// <summary>Waits until the list shows, and returns the labels of its items, in order.</summary>
    internal static async Task<IReadOnlyList<string>> WaitForLabelsAsync(EditorWindow window)
    {
        await Wait.UntilAsync(() =>
        {
            window.Render();
            return List(window) is { ItemCount: > 0 };
        });
        return Labels(window);
    }

    /// <summary>Waits until the list shows the items with <paramref name="labels"/>, in order: filters run off the UI thread.</summary>
    internal static Task WaitForLabelsAsync(EditorWindow window, IReadOnlyList<string> labels) =>
        Wait.UntilAsync(() =>
        {
            window.Render();
            return Labels(window).SequenceEqual(labels);
        });

    /// <summary>Waits until the list shows <paramref name="label"/> frozen: without its matched characters in bold.</summary>
    private static Task WaitForFrozenAsync(EditorWindow window, string label) =>
        Wait.UntilAsync(() =>
        {
            window.Render();
            return Labels(window).Contains(label) && RowRuns(window, label).SequenceEqual([(label, false)]);
        });

    /// <summary>The labels of the items that the list shows, in order; none while it is closed.</summary>
    internal static IReadOnlyList<string> Labels(EditorWindow window) =>
        List(window) is { Items: var items } ? [.. items.Select(i => i.Item.Label)] : [];

    /// <summary>The completion list in the window's overlay layer; null while it is closed.</summary>
    internal static CompletionListBox? List(EditorWindow window) =>
        OverlayLayer
            .GetOverlayLayer(window.Window)
            ?.GetVisualDescendants()
            .OfType<CompletionListBox>()
            .FirstOrDefault(list => list.IsEffectivelyVisible);

    /// <summary>The frame around the list; null while it is closed.</summary>
    internal static PopupFrame? Flyout(EditorWindow window) => PopupFrames.Find(window, "CompletionList");

    internal static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    private static string Id(string line) => line.Split(' ')[1];

    /// <summary>
    /// Presses <paramref name="key"/> in the editor without running the UI thread's other work, as
    /// <see cref="TypeAlone"/> types.
    /// </summary>
    private static void PressAlone(EditorWindow window, Key key)
    {
        TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
        textArea.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    }

    /// <summary>
    /// Types <paramref name="text"/> into the editor without running the UI thread's other work, as the headless
    /// window's input does before each event: a filter's result that is due cannot show before the next check.
    /// </summary>
    private static void TypeAlone(EditorWindow window, string text)
    {
        TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
        textArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = text });
    }

    /// <summary>Waits until the text area shows a color theme's background: its colors are the editor's then.</summary>
    private static Task WaitForBackgroundAsync(EditorWindow window, Color background) =>
        Wait.UntilAsync(() =>
        {
            window.Render();
            return EditorWindow.PartOf(window.Editor).Background is ISolidColorBrush { Color: var color }
                && color == background;
        });

    /// <summary>An item that adds an import at the top, once resolved.</summary>
    private static JsonNode ItemWithImport() =>
        Parse(
            """
            { "items": [ { "label": "Console", "data": { "detail": "class System.Console", "additionalTextEdits": [
              { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } },
                "newText": "using System;\n" } ] } } ] }
            """
        );

    /// <summary>The details beside the list; null while none show.</summary>
    private static PopupFrame? Details(EditorWindow window) => PopupFrames.Find(window, "CompletionDetails");

    private static IReadOnlyList<string> DetailTexts(EditorWindow window) =>
        Details(window)
            ?.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Inlines?.Text ?? t.Text ?? "")
            .ToList()
        ?? [];

    /// <summary>The runs of the label of the row of <paramref name="label"/>, each with whether it is bold.</summary>
    private static IReadOnlyList<(string Text, bool Bold)> RowRuns(EditorWindow window, string label) =>
        [
            .. LabelOf(window, label)
                .Inlines.ShouldNotBeNull()
                .OfType<Run>()
                .Select(run => (run.Text ?? "", run.FontWeight == FontWeight.Bold)),
        ];

    private static TextBlock LabelOf(EditorWindow window, string label) => Row(window, label).Label;

    private static CompletionListBox.Row Row(EditorWindow window, string label) =>
        List(window)
            .ShouldNotBeNull()
            .GetVisualDescendants()
            .OfType<CompletionListBox.Row>()
            .Single(row => row.IsVisible && row.Item?.Item.Label == label);

    private static Point RowCenter(EditorWindow window, int index)
    {
        window.Render();
        CompletionListBox list = List(window).ShouldNotBeNull();
        Rect row = list.RowBounds(index).ShouldNotBeNull();
        return list.TranslatePoint(row.Center, window.Window).ShouldNotBeNull();
    }

    /// <summary>The pixels of <paramref name="color"/> in the icon column of the row at <paramref name="index"/>.</summary>
    private static int IconPixels(EditorWindow window, int index, Color color)
    {
        window.Render();
        CompletionIcon icon = List(window)
            .ShouldNotBeNull()
            .RowOf(index)
            .ShouldNotBeNull()
            .GetVisualDescendants()
            .OfType<CompletionIcon>()
            .Single();
        return window.RenderAndCountPixels(color, BoundsInWindow(icon));
    }
}
