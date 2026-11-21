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
}
