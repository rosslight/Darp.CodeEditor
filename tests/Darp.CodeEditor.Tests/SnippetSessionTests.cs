using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Snippets that completion inserts: indented by their line in one undo step, their first stop selected, Tab and
/// Shift+Tab through the stops, completion inside a stop, the look of the stops, and every way the session ends.
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
    public Task Typing_in_a_stop_replaces_its_text_and_stays_inside_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "");
            await document.Language.Loaded;
            using var window = new EditorWindow(new CodeEditor { Document = document });
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:name} = ${2:value};$0"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            document.Text.ShouldBe("name = value;");

            window.Type("ab");
            document.Text.ShouldBe("ab = value;");
            // Typing on at the stop's end, and a pair that closes there, stay inside it.
            window.Type("[");
            document.Text.ShouldBe("ab[] = value;");

            // An undo takes back a character, and the session goes on.
            document.Undo();
            document.Text.ShouldBe("ab = value;");
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(Selected("ab = value;", "value"));
        });

    [Fact]
    public Task A_snippet_over_the_limits_goes_in_as_plain_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            string snippet = string.Concat(Enumerable.Range(1, Snippet.MaxStops).Select(i => $"${i}"));
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item(snippet));
            await OpenAsync(window, 0);

            window.Press(Key.Enter);

            document.Text.ShouldBe(snippet);
            window.Editor.Selection.ShouldBe(new TextSelection(snippet.Length));
        });

    [Fact]
    public Task Completion_inside_a_stop_keeps_the_session_and_a_snippet_replaces_it() =>
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
            // The new snippet's final stop ends the session.
            window.Press(Key.Tab);
            window.Editor.Selection.ShouldBe(new TextSelection(15));
            window.Press(Key.Tab);
            document.Text.ShouldBe(text + " ");
        });

    [Fact]
    public Task Escape_ends_the_session_after_completion_and_signature_help_and_before_the_hover_and_the_search_bar() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Item("${1:fn}$2;"));
            await OpenAsync(window, 0);
            window.Press(Key.Enter);
            window.Type("call(");
            document.Text.ShouldBe("call(;");
            await Wait.UntilAsync(() => ToolTip(window) is not null);
            await OpenAsync(window, 5);

            window.Press(Key.Escape);
            List(window).ShouldBeNull();
            ToolTip(window).ShouldNotBeNull();
            window.Press(Key.Escape);
            ToolTip(window).ShouldBeNull();
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
                return ToolTip(window) is { } hover
                    && hover
                        .GetVisualDescendants()
                        .OfType<TextBlock>()
                        .Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("is a word in", StringComparison.Ordinal));
            });
            window.Press(Key.Escape);
            ToolTip(window).ShouldNotBeNull();
            window.Press(Key.Escape);
            ToolTip(window).ShouldBeNull();
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

    /// <summary>Signature help's popup; null while it is closed.</summary>
    private static ToolTip? ToolTip(EditorWindow window) =>
        OverlayLayer.GetOverlayLayer(window.Window)?.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault();
}
