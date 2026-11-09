using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Completion;

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
    public Task Typing_that_starts_a_word_opens_the_list_but_not_inside_a_word_or_a_comment() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("xenon", "xylophone"));
            const string text = "[\n  ,\n  ab\n]\n// note\n";
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
            Labels(window).ShouldBe(["xylophone"]);
            window.Press(Key.Escape);

            // On in the same word, before a word, and in a comment: no request.
            window.Type("z");
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("ab", StringComparison.Ordinal));
            window.Type("q");
            window.Editor.Selection = new TextSelection(document.Text.IndexOf("note", StringComparison.Ordinal) + 4);
            window.Type(" w");
            await BarrierAsync(server, fake);
            fake.LinesStartingWith("completion ").Count.ShouldBe(1);
            document.Text.ShouldBe("[\n  xyz,\n  qab\n]\n// note w\n");
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
            Labels(window).ShouldBe(["Console", "Convert", "readConsole"]);
            List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(1);
            window.Render();
            RowRuns(window, "readConsole").ShouldBe([("read", false), ("Con", true), ("sole", false)]);
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

            // But not "Write", whose own replace the server's: the list closes, as nothing matches any more.
            await OpenAsync(window, 10);
            window.Type(";");
            document.Text.ShouldBe("Console.Wr;");
            Labels(window).ShouldBeEmpty();
        });

    [Fact]
    public Task Edits_replace_their_range_also_with_what_was_typed_since_and_an_insert_replace_edit_inserts() =>
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

                // VS Code's default: an insert and replace edit inserts.
                await OpenAsync(window, 7);
                window.Press(Key.Down);
                window.Press(Key.Enter);
                document.Text.ShouldBe("log.writeAllXYZ");
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
            // The code keeps the editor's foreground, also where its grammar gives no color.
            window.RenderAndCountPixels(Colors.Black, BoundsInWindow(Details(window)!)).ShouldBeGreaterThan(20);
            fake.LinesStartingWith("resolve ").Select(l => l.Split(' ')[^1]).ShouldBe(["Console", "Convert"]);
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
            await server.StartAsync();

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
    public Task The_list_looks_like_the_apps_flyouts_in_the_editors_theme_variant_and_stays_inside_the_window() =>
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

            FlyoutPresenter flyout = Flyout(window).ShouldNotBeNull();
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

    /// <summary>Makes the fake server answer completion with <paramref name="result"/>, until the document is disposed.</summary>
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

    /// <summary>The labels of the items that the list shows, in order; none while it is closed.</summary>
    internal static IReadOnlyList<string> Labels(EditorWindow window) =>
        List(window) is { ItemsSource: IEnumerable<ShownItem> items } ? [.. items.Select(i => i.Item.Label)] : [];

    /// <summary>The completion list in the window's overlay layer; null while it is closed.</summary>
    internal static ListBox? List(EditorWindow window) =>
        OverlayLayer.GetOverlayLayer(window.Window)?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault();

    /// <summary>The flyout around the list; null while it is closed.</summary>
    internal static FlyoutPresenter? Flyout(EditorWindow window) => List(window)?.FindAncestorOfType<FlyoutPresenter>();

    internal static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    private static string Id(string line) => line.Split(' ')[1];

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
    private static FlyoutPresenter? Details(EditorWindow window) =>
        OverlayLayer
            .GetOverlayLayer(window.Window)
            ?.GetVisualDescendants()
            .OfType<FlyoutPresenter>()
            .Skip(1)
            .FirstOrDefault(f => f.IsVisible);

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

    private static TextBlock LabelOf(EditorWindow window, string label) =>
        Row(window, label).GetVisualDescendants().OfType<TextBlock>().Single();

    private static ListBoxItem Row(EditorWindow window, string label) =>
        List(window)
            .ShouldNotBeNull()
            .GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Single(item => item.Content is ShownItem shown && shown.Item.Label == label);

    private static Point RowCenter(EditorWindow window, int index)
    {
        window.Render();
        ListBox list = List(window).ShouldNotBeNull();
        return BoundsInWindow(list.ContainerFromIndex(index).ShouldNotBeNull()).Center;
    }

    /// <summary>The pixels of <paramref name="color"/> in the icon column of the row at <paramref name="index"/>.</summary>
    private static int IconPixels(EditorWindow window, int index, Color color)
    {
        window.Render();
        CompletionIcon icon = List(window)
            .ShouldNotBeNull()
            .ContainerFromIndex(index)
            .ShouldNotBeNull()
            .GetVisualDescendants()
            .OfType<CompletionIcon>()
            .Single();
        return window.RenderAndCountPixels(color, BoundsInWindow(icon));
    }
}
