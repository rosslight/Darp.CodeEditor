using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// What completion does besides its list: where typing opens it, per language; a typo that a short list tolerates;
/// additional edits that come after the commit; the editor's commands that items run; and the caret moves that end it.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class CompletionBehaviorTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

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
            // The grammar knows the comment.
            await window.TokensKnownAsync();

            window.Editor.Selection = new TextSelection(4);
            window.Type("x");
            (await WaitForLabelsAsync(window)).ShouldBe(["xenon", "xylophone"]);
            window.Type("y");
            await WaitForLabelsAsync(window, ["xylophone"]);
            window.Press(Key.Escape);

            // Typing on at the word's end asks again, and so does a first character before a word.
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

    /// <summary>Languages with JSON's grammar, and where typing opens completion in them.</summary>
    public static TheoryData<string, bool, bool, bool> QuickSuggestionLanguages =>
        new()
        {
            // The default; JSON's, which adds strings; and comments only.
            { "default", true, false, false },
            { "json", true, false, true },
            { "comments", false, true, false },
        };

    [Theory]
    [MemberData(nameof(QuickSuggestionLanguages))]
    public Task Typing_opens_the_list_in_code_comments_and_strings_as_the_languages_quick_suggestions_say(
        string language,
        bool inCode,
        bool inComments,
        bool inStrings
    ) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("xenon"));
            CodeLanguage json = CodeLanguage.Json;
            CodeLanguage codeLanguage = language switch
            {
                "json" => json,
                "comments" => new CodeLanguage("comments", json.Grammar, json.Configuration)
                {
                    QuickSuggestions = new QuickSuggestions(Other: false, Comments: true, Strings: false),
                },
                _ => new CodeLanguage("default", json.Grammar, json.Configuration),
            };
            const string text = "[\n  \"\",\n  \n]\n// \n";
            using CodeDocument document = server.CreateDocument(codeLanguage, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            // The grammar knows the comment and the string.
            await window.TokensKnownAsync();

            (await OpensAsync(document.Text.IndexOf("\"\"", StringComparison.Ordinal) + 1)).ShouldBe(inStrings);
            (await OpensAsync(document.Text.IndexOf("  \n]", StringComparison.Ordinal) + 2)).ShouldBe(inCode);
            (await OpensAsync(document.Text.IndexOf("// ", StringComparison.Ordinal) + 3)).ShouldBe(inComments);

            // Whether typing "x" at the offset asks for completion.
            async Task<bool> OpensAsync(int offset)
            {
                int asked = fake.LinesStartingWith("completion ").Count;
                window.Editor.Selection = new TextSelection(offset);
                window.Type("x");
                await BarrierAsync(server, fake);
                window.Press(Key.Escape);
                return fake.LinesStartingWith("completion ").Count > asked;
            }
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

            // Ranked for what was typed, with "n" and "i" swapped: not the rows for "p", nor a frozen list's.
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return Labels(window) is ["print"]
                    && RowRuns(window, "print").SequenceEqual([("prin", true), ("t", false)]);
            });
            // Asked for there, the list is ranked whole, strictly: it has more than 2,000 items.
            window.Press(Key.Escape);
            window.Press(Key.Space, KeyModifiers.Control);
            await Wait.UntilAsync(() => List(window)?.Message == "No suggestions.");
        });

    [Fact]
    public Task Additional_edits_that_come_after_the_commit_join_its_step_while_it_is_the_last_one_or_take_their_own() =>
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

            // The user typed on after the item, below where they go, before they came: they go in a step of their own.
            using (await CommandAsync(server, fake, "hold-resolve", "holding resolve", 2))
            {
                await OpenAsync(window, 5);
                await fake.WaitForAsync("resolve ", 2);
                window.Press(Key.Enter);
                window.Type("!");
            }
            using (await CommandAsync(server, fake, "release-resolve", "released resolve", 2))
            {
                await Wait.UntilAsync(() => document.Text == "using System;\nx\nConsole!");
                document.Undo();
                document.Text.ShouldBe("x\nConsole!");
            }
        });

    [Fact]
    public Task Each_commit_gets_the_additional_edits_that_come_after_it_also_once_another_commit_followed() =>
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
                      { "label": "Console", "data": { "additionalTextEdits": [
                        { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } },
                          "newText": "using System;\n" } ] } },
                      { "label": "Builder", "data": { "additionalTextEdits": [
                        { "range": { "start": { "line": 0, "character": 0 }, "end": { "line": 0, "character": 0 } },
                          "newText": "using Other;\n" } ] } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x\nCon\nBui");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            using (await CommandAsync(server, fake, "hold-resolve", "holding resolve"))
            {
                await OpenAsync(window, 5);
                await fake.WaitForAsync("resolve ");
                window.Press(Key.Enter);
                await OpenAsync(window, document.Text.Length);
                await fake.WaitForAsync("resolve ", 2);
                window.Press(Key.Enter);
                document.Text.ShouldBe("x\nConsole\nBuilder");
            }
            using (await CommandAsync(server, fake, "release-resolve", "released resolve"))
            {
                await Wait.UntilAsync(() =>
                    document.Text
                        is "using System;\nusing Other;\nx\nConsole\nBuilder"
                            or "using Other;\nusing System;\nx\nConsole\nBuilder"
                );
            }
        });

    [Fact]
    public Task Additional_edits_that_come_late_are_dropped_once_the_commit_is_undone() =>
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
                window.Press(Key.Z, EditorWindow.Command);
                document.Text.ShouldBe(text);
            }
            using (await CommandAsync(server, fake, "release-resolve", "released resolve"))
            {
                // A list shows after the late answer: the client has read it by then.
                await OpenAsync(window, document.Text.Length);
                document.Text.ShouldBe(text);
            }
        });

    [Fact]
    public Task An_items_command_runs_only_once_the_item_went_in() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "key",
                      "command": { "title": "Suggest", "command": "editor.action.triggerSuggest" } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "keXYZ");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            // The word's end is protected: replacing goes over it, so nothing goes in.
            TextArea textArea = EditorWindow.PartOf(window.Editor).TextArea;
            var protectedText = new TextSegmentReadOnlySectionProvider<TextSegment>(textArea.Document);
            protectedText.Segments.Add(new TextSegment { StartOffset = 2, Length = 3 });
            textArea.ReadOnlySectionProvider = protectedText;

            await OpenAsync(window, 2);
            int asked = fake.LinesStartingWith("completion ").Count;
            window.Press(Key.Tab);
            await BarrierAsync(server, fake);

            document.Text.ShouldBe("keXYZ");
            fake.LinesStartingWith("completion ").Count.ShouldBe(asked);
            List(window).ShouldBeNull();
        });

    [Fact]
    public Task Only_an_items_triggerSuggest_command_runs_after_the_commit() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            // As a JSON server writes a property: its value comes next, so completion asks again.
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [
                      { "label": "key", "insertText": "\"key\": $1", "insertTextFormat": 2,
                        "command": { "title": "Suggest", "command": "editor.action.triggerSuggest" } },
                      { "label": "other", "command": { "title": "Undo", "command": "undo" } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });

            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("\"key\": ");
            // Asked again as typing asks, not by key: nothing says "No suggestions."
            (await fake.WaitForAsync("completion ", 2)).ShouldEndWith(" 1 - 7");
            (await WaitForLabelsAsync(window)).ShouldBe(["key", "other"]);
            window.Press(Key.Escape);

            document.Text = "";
            await OpenAsync(window, 0);
            int asked = fake.LinesStartingWith("completion ").Count;
            window.Press(Key.Down);
            window.Press(Key.Enter);
            await BarrierAsync(server, fake);
            document.Text.ShouldBe("other");
            fake.LinesStartingWith("completion ").Count.ShouldBe(asked);
        });

    [Fact]
    public Task The_arrow_keys_keep_the_list_on_its_word() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Conv");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 4);
            await WaitForLabelsAsync(window, ["Convert"]);

            // Left, back past where it was asked: it asks again for the word that is left.
            window.Press(Key.Left);

            await WaitForLabelsAsync(window, ["Console", "Convert"]);
            window.Editor.Selection.ShouldBe(new TextSelection(3));

            // Right, on within the word: it filters.
            window.Press(Key.Right);

            await WaitForLabelsAsync(window, ["Convert"]);
        });

    [Fact]
    public Task A_caret_move_that_is_not_the_keyboards_ends_completion() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Console", "Convert"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, " Co");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);

            // A cursor key filters again; the app's code moving the caret ends completion.
            await OpenAsync(window, 3);
            window.Press(Key.Left);
            (await WaitForLabelsAsync(window)).ShouldBe(["Console", "Convert"]);
            editor.Selection = new TextSelection(3);
            List(window).ShouldBeNull();

            // Another editor of the document types at the line's start: the caret moves with the text.
            await OpenAsync(window, 3);
            document.Replace(new TextRange(0, 0), "x");
            List(window).ShouldBeNull();

            // An undo.
            await OpenAsync(window, 4);
            window.Type("n");
            await WaitForLabelsAsync(window);
            window.Press(Key.Z, EditorWindow.Command);
            document.Text.ShouldBe("x Co");
            List(window).ShouldBeNull();

            // A paste, which asks nothing either.
            await window.Window.Clipboard.ShouldNotBeNull().SetTextAsync("n");
            await OpenAsync(window, 4);
            int asked = fake.LinesStartingWith("completion ").Count;
            window.Press(Key.V, EditorWindow.Command);
            await Wait.UntilAsync(() => document.Text == "x Con");
            List(window).ShouldBeNull();
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(asked);
        });

    [Fact]
    public Task A_click_at_the_caret_ends_completion() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("print", "property"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Type("pri");
            await WaitForLabelsAsync(window, ["print"]);

            window.Click(window.PointAt(3), MouseButton.Left);

            window.Editor.Selection.ShouldBe(new TextSelection(3));
            List(window).ShouldBeNull();
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
}
