using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Snippets that completion inserts, as VS Code inserts them: indented by their line in one undo step, their first stop
/// selected, Tab and Shift+Tab through the stops, mirrors that follow, completion inside a stop, the variables of the
/// editor, the look of the stops, and every way the session ends.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SnippetSessionTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_snippet_goes_in_indented_by_its_line_in_one_step_with_its_first_stop_selected() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "{\n    ife\n}");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using (
                await SetCompletionAsync(
                    server,
                    fake,
                    Parse(
                        """
                        { "items": [ { "label": "ifelse", "insertTextFormat": 2,
                          "insertText": "if (${1:condition}) {\n\t$0\n} else {\n\t$2\n}",
                          "additionalTextEdits": [ { "range": { "start": { "line": 0, "character": 0 },
                            "end": { "line": 0, "character": 0 } }, "newText": "// $1\n" } ] } ] }
                        """
                    )
                )
            )
            {
                await OpenAsync(window, 9);
                window.Press(Key.Enter);

                // The additional edit is plain text, before the snippet.
                const string text = "// $1\n{\n    if (condition) {\n        \n    } else {\n        \n    }\n}";
                document.Text.ShouldBe(text);
                window.Editor.Selection.ShouldBe(Selected(text, "condition"));
                document.Undo();
                document.Text.ShouldBe("{\n    ife\n}");
            }

            // A text edit, grown by what was typed since completion was asked.
            document.Text = "{\n    l\n}";
            using (
                await SetCompletionAsync(
                    server,
                    fake,
                    Parse(
                        """
                        { "items": [ { "label": "let", "insertTextFormat": 2, "textEdit": {
                          "range": { "start": { "line": 1, "character": 4 }, "end": { "line": 1, "character": 5 } },
                          "newText": "let ${1:name} = $1;$0" } } ] }
                        """
                    )
                )
            )
            {
                await OpenAsync(window, 7);
                window.Type("e");
                window.Press(Key.Enter);

                document.Text.ShouldBe("{\n    let name = name;\n}");
                window.Editor.Selection.ShouldBe(new TextSelection(10, 14));
            }
        });

    [Fact]
    public Task Tab_and_Shift_Tab_move_through_the_stops_and_the_final_stop_ends_the_session() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "    ca");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item("call(${1:first}, ${2:second})$0", "call")
            );
            await OpenAsync(window, 6);
            window.Press(Key.Enter);
            const string text = "    call(first, second)";
            document.Text.ShouldBe(text);
            window.Editor.Selection.ShouldBe(Selected(text, "first"));

            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(Selected(text, "second"));
            window.Press(Key.Tab, KeyModifiers.Shift);
            window.Editor.Selection.ShouldBe(Selected(text, "first"));
            // On the first stop, Shift+Tab is indentation's, as in VS Code: the stops follow the text.
            window.Press(Key.Tab, KeyModifiers.Shift);
            document.Text.ShouldBe("call(first, second)");
            window.Editor.Selection.ShouldBe(Selected("call(first, second)", "first"));
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(Selected("call(first, second)", "second"));

            // The final stop: the caret goes there, and Tab indents again.
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(19));
            window.Press(Key.Tab);
            document.Text.ShouldBe("call(first, second) ");
        });

    [Fact]
    public Task Typing_in_a_stop_replaces_its_text_and_changes_its_mirrors_in_the_same_undo_step() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "");
            await document.Language.Loaded;
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:name} = $1 + ${1:other};$0"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("name = name + name;");

            window.Type("ab");
            document.Text.ShouldBe("ab = ab + ab;");
            // Typing on at the stop's end, and a pair that closes there, stay inside it.
            window.Type("[");
            document.Text.ShouldBe("ab[] = ab[] + ab[];");

            // An undo takes back a character with its mirrors, and the session goes on.
            document.Undo();
            document.Text.ShouldBe("ab = ab + ab;");
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(13));
        });

    [Fact]
    public Task A_mirror_that_changes_inside_another_stop_changes_that_stops_mirrors_too() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:a${2:b}} = $1; $2"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("ab = ab; b");

            // In the stop inside the first placeholder: both of their mirrors follow.
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(1, 2));
            window.Type("x");
            document.Text.ShouldBe("ax = ax; x");

            // In the second stop's mirror, which VS Code would leave with a cursor of its own: its stop changes, and so
            // does the first placeholder's mirror.
            window.Editor.Selection = new TextSelection(9, 10);
            window.Type("y");
            document.Text.ShouldBe("ay = ay; y");
            document.Undo();
            document.Text.ShouldBe("ax = ax; x");
        });

    [Theory]
    // The stop inside the placeholder follows first, so that the placeholder's mirror has its new text.
    [InlineData("${1:a${2:b} $2} = $1", 1, "x", "ax x = ax x")]
    // A mirror right after its placeholder does not take what is typed at the placeholder's end.
    [InlineData("${1:a}$1", 0, "xyz", "xyzxyz")]
    // Empty mirrors side by side each take what is typed, and stay apart.
    [InlineData("$1$1$1", 0, "abcd", "abcdabcdabcd")]
    public Task Mirrors_follow_nested_and_neighbouring_stops_as_in_VS_Code(
        string snippet,
        int tabs,
        string typed,
        string text
    ) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item(snippet));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            for (int i = 0; i < tabs; i++)
                window.Press(Key.Tab);

            window.Type(typed);

            document.Text.ShouldBe(text);
        });

    [Fact]
    public Task Copies_into_mirrors_change_only_the_mirrors_not_the_text_between_them() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:a} $1 ${2:x} $1"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Press(Key.Tab);
            // A stop between the mirrors grows long, on lines short enough to show whole: a long line's hidden tail takes
            // no edits.
            string between = string.Concat(Enumerable.Repeat(new string('x', 99) + "\n", 10_000));
            document.Replace(new TextRange(4, 1), between);
            window.Press(Key.Tab, KeyModifiers.Shift);
            editor.Selection.ShouldBe(new TextSelection(0, 1));
            int largest = 0;
            void OnChanging(object? sender, AvaloniaEdit.Document.DocumentChangeEventArgs e) =>
                largest = Math.Max(largest, Math.Max(e.RemovalLength, e.InsertionLength));
            document.Buffer.Changing += OnChanging;

            window.Type("b");

            document.Buffer.Changing -= OnChanging;
            document.Text.ShouldBe("b b " + between + " b");
            largest.ShouldBe(1);
        });

    [Fact]
    public Task Typing_over_a_placeholder_drops_the_stops_inside_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:a${2}b} + ${3:c}"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 2));

            window.Type("x");
            window.Press(Key.Tab);

            document.Text.ShouldBe("x + c");
            window.Editor.Selection.ShouldBe(new TextSelection(4, 5));
        });

    [Fact]
    public Task A_snippet_has_as_many_stops_as_it_likes_and_mirrors_copy_whatever_goes_in() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            // 40 numbers, the first with 30 mirrors.
            string snippet =
                "${1:a}"
                + string.Concat(Enumerable.Repeat(" $1", 30))
                + string.Concat(Enumerable.Range(2, 39).Select(i => $" ${{{i}:s{i}}}"));
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item(snippet));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            editor.Selection.ShouldBe(new TextSelection(0, 1));

            // 2,000 characters that go in over the first stop, as a paste, go into each of its mirrors.
            string pasted = new('b', 2_000);
            document.Replace(new TextRange(0, 1), pasted);

            string mirrored = pasted + string.Concat(Enumerable.Repeat(" " + pasted, 30));
            string rest = string.Concat(Enumerable.Range(2, 39).Select(i => $" s{i}"));
            document.Text.ShouldBe(mirrored + rest);
            // Tab selects each of the other stops, and then the final one.
            int start = mirrored.Length + 1;
            for (int i = 2; i <= 40; i++)
            {
                window.Press(Key.Tab);
                int length = $"s{i}".Length;
                editor.Selection.ShouldBe(new TextSelection(start, start + length));
                start += length + 1;
            }
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(document.Text.Length));
        });

    [Theory]
    [InlineData("${1/foo/bar/}$0", "foo", "bar")]
    [InlineData("$1 baz ${1/foo/bar/}$0", "foo", "foo baz bar")]
    [InlineData("${1:foo} ${1/(.*)/${1:/upcase}/}$0", "bar", "bar BAR")]
    public Task Tab_transforms_the_stops_of_the_number_it_leaves(string snippet, string typed, string text) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item(snippet));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);

            window.Type(typed);
            window.Press(Key.Tab);

            document.Text.ShouldBe(text);
            editor.Selection.ShouldBe(new TextSelection(text.Length));
        });

    [Fact]
    public Task A_transformation_applies_as_Tab_or_Shift_Tab_leaves_its_number_and_not_on_Escape() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item(@"${1:name} : ${2:type}${3/\s:=(.*)/${1:+ :=}${1}/};" + "\n$0")
            );

            // An empty stop with a transformation is a stop like any other; its text is transformed as Tab leaves it.
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Type("clk");
            window.Press(Key.Tab);
            window.Type("std_logic");
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(15));
            window.Type(" := '1'");
            window.Press(Key.Tab);
            document.Text.ShouldBe(NewLines.In("clk : std_logic := '1';\n", start: ""));
            editor.Selection.ShouldBe(new TextSelection(document.Text.Length));

            // Shift+Tab transforms too; Escape does not.
            document.Text = "";
            using CodeDocument mirrored = await SetCompletionAsync(
                server,
                fake,
                Item("${1:a} ${2:b} ${2/(.*)/${1:/upcase}/}")
            );
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Press(Key.Tab);
            window.Type("x");
            document.Text.ShouldBe("a x x");
            window.Press(Key.Tab, KeyModifiers.Shift);
            document.Text.ShouldBe("a x X");
            editor.Selection.ShouldBe(new TextSelection(0, 1));
            window.Press(Key.Tab);
            window.Type("y");
            window.Press(Key.Escape);
            document.Text.ShouldBe("a y y");
        });

    [Fact]
    public Task A_transformations_lines_take_the_indentation_of_the_snippets_line_in_the_documents_indentation() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "{\n  \n}");
            document.Indentation = Indentation.Spaces(2);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item("function animate () {\n\tvar ${1:a} = 12;\n\tconsole.log(${1/(.*)/\n\t\t$1\n\t/})\n}")
            );
            await OpenAsync(window, 4);
            window.Press(Key.Enter);
            document.Text.ShouldBe("{\n  function animate () {\n    var a = 12;\n    console.log(a)\n  }\n}");

            window.Type("bbb");
            window.Press(Key.Tab);

            document.Text.ShouldBe(
                "{\n  function animate () {\n    var bbb = 12;\n    console.log(\n      bbb\n    )\n  }\n}"
            );
        });

    [Fact]
    public Task A_transformed_stop_next_to_another_keeps_apart_from_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:{}${2:fff}${1/{/}/}"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("{fff{");

            window.Press(Key.Tab);

            document.Text.ShouldBe("{fff}");
            editor.Selection.ShouldBe(new TextSelection(1, 4));
            window.Type("ggg");
            window.Press(Key.Tab);
            document.Text.ShouldBe("{ggg}");
            editor.Selection.ShouldBe(new TextSelection(5));
        });

    [Fact]
    public Task A_choice_offers_its_options_in_the_list_and_committing_one_goes_on_to_the_next_stop() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item("${1|red,green,blue|} ${2|x,y|} $1$0", "color")
            );
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            int asked = fake.LinesStartingWith("completion ").Count;

            // Its options, in their order, the first selected, while the choice's text is selected.
            document.Text.ShouldBe("red x red");
            editor.Selection.ShouldBe(new TextSelection(0, 3));
            await WaitForLabelsAsync(window, ["red", "green", "blue"]);
            List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(0);

            // Committing one puts it in, with its mirrors, and selects the next stop, a choice too.
            window.Press(Key.Down);
            window.Press(Key.Enter);
            document.Text.ShouldBe("green x green");
            editor.Selection.ShouldBe(new TextSelection(6, 7));
            await WaitForLabelsAsync(window, ["x", "y"]);

            // Shift+Tab goes back, and shows the first choice's options again; typing filters them.
            window.Press(Key.Tab, KeyModifiers.Shift);
            editor.Selection.ShouldBe(new TextSelection(0, 5));
            await WaitForLabelsAsync(window, ["red", "green", "blue"]);
            window.Type("b");
            await WaitForLabelsAsync(window, ["blue"]);
            window.Press(Key.Tab);
            document.Text.ShouldBe("blue x blue");
            editor.Selection.ShouldBe(new TextSelection(5, 6));

            // Escape closes the list and keeps the session: Tab goes on to the final stop.
            await WaitForLabelsAsync(window, ["x", "y"]);
            window.Press(Key.Escape);
            List(window).ShouldBeNull();
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(document.Text.Length));

            // The server was not asked while a choice was selected.
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(asked);
        });

    [Fact]
    public Task The_clipboard_variable_is_the_clipboards_text_read_as_the_list_shows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item("[$CLIPBOARD|${CLIPBOARD/(.*)/${1:/upcase}/}|${CLIPBOARD:none}]")
            );

            // One line, without its line break.
            await Clipboard(window).SetTextAsync("clipped\r\n");
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("[clipped|CLIPPED|clipped]");

            // Lines, in the document's line breaks.
            document.Text = "";
            await Clipboard(window).SetTextAsync("one\r\ntwo");
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe(NewLines.In("[one\ntwo|ONE\ntwo|one\ntwo]", start: ""));

            // None.
            document.Text = "";
            await Clipboard(window).SetTextAsync("");
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("[||none]");
        });

    [Fact]
    public Task The_selected_text_is_the_selection_that_typing_replaced_before_completion_committed_the_snippet() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "    foo\n    bar\nbaz");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item("try {\n\t${1:$TM_SELECTED_TEXT}\n}$0", "try")
            );

            // Typing over the two lines opens completion; typing on in it keeps what the first key replaced.
            editor.Selection = new TextSelection(4, 15);
            window.Type("t");
            await WaitForLabelsAsync(window, ["try"]);
            window.Type("r");
            window.Press(Key.Enter);

            // Its lines after the first take the level that the snippet adds.
            const string text = "    try {\n        foo\n        bar\n    }\nbaz";
            document.Text.ShouldBe(text);
            editor.Selection.ShouldBe(Selected(text, "foo\n        bar"));

            // Typing that replaces no selection forgets it.
            window.Press(Key.Escape);
            document.Text = "t";
            editor.Selection = new TextSelection(1);
            window.Type("r");
            await WaitForLabelsAsync(window, ["try"]);
            window.Press(Key.Enter);
            document.Text.ShouldBe(NewLines.In("try {\n    \n}", start: "t"));
        });

    [Fact]
    public Task Completion_inside_a_stop_keeps_the_session_and_a_snippet_joins_it_after_that_stop() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "pair", "insertTextFormat": 2, "insertText": "${1:a} + ${2:b}" },
                                 { "label": "cat" },
                                 { "label": "sum", "insertTextFormat": 2, "insertText": "sum(${1:x}, ${2:y})" } ] }
                    """
                )
            );
            await OpenAsync(window, 0);
            await WaitForLabelsAsync(window, ["cat", "pair", "sum"]);
            window.Press(Key.Down);
            window.Press(Key.Enter);
            document.Text.ShouldBe("a + b");

            // Typing "c" over the stop asks for completion, as it starts a word.
            window.Type("c");
            await WaitForLabelsAsync(window, ["cat"]);
            window.Press(Key.Enter);
            document.Text.ShouldBe("cat + b");
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(Selected("cat + b", "b"));

            window.Type("s");
            await WaitForLabelsAsync(window, ["sum"]);
            window.Press(Key.Enter);
            const string text = "cat + sum(x, y)";
            document.Text.ShouldBe(text);
            window.Editor.Selection.ShouldBe(Selected(text, "x"));
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(Selected(text, "y"));
            // The joined snippet's final stop, then the outer snippet's, which ends the session.
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(15));
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(15));
            window.Press(Key.Tab);
            document.Text.ShouldBe(text + " ");
        });

    [Fact]
    public Task Escape_ends_the_session_after_completion_and_signature_help_and_before_the_hover_and_the_search_bar() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:fn}$2;"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Type("call(");
            document.Text.ShouldBe("call(;");
            time.Advance(TimeSpan.FromMilliseconds(120));
            await Wait.UntilAsync(() => PopupFrames.Find(window, "SignatureHelp") is not null);
            await OpenAsync(window, 5);

            window.Press(Key.Escape);
            List(window).ShouldBeNull();
            PopupFrames.Find(window, "SignatureHelp").ShouldNotBeNull();
            window.Press(Key.Escape);
            PopupFrames.Find(window, "SignatureHelp").ShouldBeNull();
            // The session runs: Tab goes to the second stop, and Shift+Tab back to the first.
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(5));
            window.Press(Key.Tab, KeyModifiers.Shift);
            editor.Selection.ShouldBe(new TextSelection(0, 5));

            // With the keyboard in the search bar, Escape closes the bar, and the session goes on.
            editor.OpenSearch();
            SearchBar bar = editor.GetVisualDescendants().OfType<SearchBar>().Single();
            window.Press(Key.Escape);
            bar.IsOpen.ShouldBeFalse();
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(5));

            // With the keyboard in the text, Escape ends the session before the hover closes, and the hover before the
            // bar.
            editor.OpenSearch();
            EditorWindow.PartOf(editor).TextArea.Focus();
            window.MoveMouse(window.PointAt(1));
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return PopupFrames.Find(window, "Hover") is { } hover
                    && hover
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("is a word in", StringComparison.Ordinal));
            });
            window.Press(Key.Escape);
            PopupFrames.Find(window, "Hover").ShouldNotBeNull();
            window.Press(Key.Escape);
            PopupFrames.Find(window, "Hover").ShouldBeNull();
            bar.IsOpen.ShouldBeTrue();
            window.Press(Key.Escape);
            bar.IsOpen.ShouldBeFalse();
            window.Press(Key.Tab);
            document.Text.ShouldBe("call(   ;");
        });

    [Fact]
    public Task The_session_ends_when_the_selection_leaves_the_stops_the_insertion_is_undone_or_the_text_is_replaced() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, " end");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:a}+${2:b}"));

            // The caret moves out of the stops.
            await InsertAsync();
            editor.Selection = new TextSelection(7);
            window.Press(Key.Tab);
            document.Text.ShouldBe("a+b end ");

            // Undoing a character typed in a stop keeps the session; undoing the insertion ends it.
            document.Text = " end";
            await InsertAsync();
            window.Type("x");
            document.Undo();
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(2, 3));
            document.Undo();
            document.Text.ShouldBe(" end");
            editor.Selection = new TextSelection(0);
            window.Press(Key.Tab);
            document.Text.ShouldBe("     end");

            // The text is replaced, as by code or another editor of the document.
            document.Text = " end";
            await InsertAsync();
            document.Text = "a+b end";
            editor.Selection = new TextSelection(0);
            window.Press(Key.Tab);
            document.Text.ShouldBe("    a+b end");

            // The editor becomes read-only, and editable again.
            document.Text = " end";
            await InsertAsync();
            editor.IsReadOnly = true;
            window.Press(Key.Tab);
            editor.Selection.ShouldBe(new TextSelection(0, 1));
            editor.IsReadOnly = false;
            editor.Selection = new TextSelection(0);
            window.Press(Key.Tab);
            document.Text.ShouldBe("    a+b end");

            // The editor stops showing the document, and shows it again.
            document.Text = " end";
            await InsertAsync();
            using CodeDocument other = CodeDocument.Create(CodeLanguage.PlainText);
            editor.Document = other;
            editor.Document = document;
            window.Layout();
            window.Press(Key.Tab);
            document.Text.ShouldBe("    a+b end");

            async Task InsertAsync()
            {
                await OpenAsync(window, 0);
                window.Press(Key.Enter);
                document.Text.ShouldBe("a+b end");
                editor.Selection.ShouldBe(new TextSelection(0, 1));
            }
        });

    [Fact]
    public Task Variables_take_the_caret_word_and_line_and_the_documents_file_language_and_clock() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  wo\n}");
            document.FilePath = Path.Combine(Path.GetTempPath(), "settings.json");
            await document.Language.Loaded;
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Clipboard(window).SetTextAsync("copied" + Environment.NewLine);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Item(
                    "[$TM_CURRENT_WORD|$TM_CURRENT_LINE|$TM_LINE_NUMBER|$TM_FILENAME_BASE|${TM_SELECTED_TEXT:none}|"
                        + "$LINE_COMMENT$BLOCK_COMMENT_END|$CURRENT_SECONDS_UNIX|$CLIPBOARD$WORKSPACE_NAME]",
                    "word"
                )
            );

            await OpenAsync(window, 6);
            window.Press(Key.Enter);

            // Before the commit, at the caret: the word that the item replaces.
            document.Text.ShouldBe("{\n  [wo|  wo|2|settings|none|//*/|1767225600|copied]\n}");
        });

    [Fact]
    public Task Stops_show_as_boxes_in_the_themes_colors_while_the_session_runs() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor
            {
                Document = document,
                ColorTheme = new CodeEditorColorTheme
                {
                    Source = new Uri("avares://Darp.CodeEditor.Tests/Themes/marks.json"),
                },
            };
            using var window = new EditorWindow(editor);
            // The final stop between spaces: text drawn with colored edges, as on Windows, cannot reach its border's color.
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:aaaa} ${2:bbbb}  $0  "));
            Color stop = Color.Parse("#00FF80");
            Color final = Color.Parse("#804000");
            var aroundFinal = new TextRange(10, 3);

            await OpenAsync(window, 0);
            window.Press(Key.Enter);

            // The second stop, as the first is selected, in the theme's colors once it is loaded; the final stop's border.
            await Wait.UntilAsync(() => window.RenderAndCountPixels(stop, new TextRange(5, 4)) > 0);
            window.RenderAndCountPixels(final, aroundFinal).ShouldBeGreaterThan(0);
            window.Press(Key.Escape);
            window.RenderAndCountPixels(stop).ShouldBe(0);
            window.RenderAndCountPixels(final, aroundFinal).ShouldBe(0);
        });

    /// <summary>A complete list of one item, whose text is <paramref name="snippet"/> in the snippet syntax.</summary>
    private static JsonObject Item(string snippet, string label = "snippet") =>
        new()
        {
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["label"] = label,
                    ["insertTextFormat"] = 2,
                    ["insertText"] = snippet,
                }
            ),
        };

    /// <summary>The selection of <paramref name="word"/>'s first occurrence in <paramref name="text"/>, the caret at its end.</summary>
    private static TextSelection Selected(string text, string word)
    {
        int start = text.IndexOf(word, StringComparison.Ordinal);
        return new TextSelection(start, start + word.Length);
    }

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");
}
