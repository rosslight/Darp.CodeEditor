using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Hover: the diagnostics' messages above the server's Markdown, in the editor's own popup in its color theme's hover
/// colors, after VS Code's delay on the editor's clock, which stays inside the window, takes the editor's theme variant,
/// stays once clicked into, and closes when it is no longer wanted.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class HoverTests(UiFixture ui)
{
    /// <summary>The Light+ colors of JSON: property names, and strings and numbers out of an object.</summary>
    private static readonly Color s_propertyName = Color.Parse("#0451A5");

    private static readonly Color s_string = Color.Parse("#A31515");
    private static readonly Color s_number = Color.Parse("#098658");

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Hover_shows_the_diagnostics_above_the_servers_Markdown_with_its_code_highlighted_inside_the_window() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "{\n  \"key\": 1\n}";
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, text);
            var editor = new CodeEditor { Document = document };
            // Near the window's right edge, so that the popup has to move left to stay inside.
            using var window = new EditorWindow(editor, width: 300, height: 200);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1 && window.ColorAt(5) == s_propertyName);

            PopupFrame popup = await HoverAsync(
                window,
                text.IndexOf("key", StringComparison.Ordinal),
                "is a word in",
                time
            );

            Control[] parts = [.. ((Panel)Content(popup)).Children];
            // The diagnostic's icon, message and code, a line, the server's paragraph, and its code block.
            parts[0].GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            parts[0].GetVisualDescendants().OfType<TextBlock>().Select(TextOf).ShouldBe([$"{text} (echo)"]);
            parts[1].ShouldBeAssignableTo<Border>().ShouldNotBeNull().Height.ShouldBe(1);
            TextOf(parts[2]).ShouldBe("key is a word in json.");
            ((TextBlock)parts[2]).Inlines.ShouldNotBeNull().OfType<Run>().First().FontWeight.ShouldBe(FontWeight.Bold);
            // Code sits on the popup's background, in the editor's font: no box of the editor's background.
            var code = parts[3].ShouldBeAssignableTo<TextBlock>().ShouldNotBeNull();
            TextOf(code).ShouldBe("  \"key\": 1");
            code.FontFamily.ShouldBe(editor.FontFamily);
            // A code block starts from the start of the grammar: the key is a string out of an object.
            Run[] runs = [.. code.Inlines.ShouldNotBeNull().OfType<Run>()];
            runs.ShouldContain(run => run.Text == "\"key\"" && (run.Foreground as ISolidColorBrush)!.Color == s_string);
            runs.ShouldContain(run => run.Text == "1" && (run.Foreground as ISolidColorBrush)!.Color == s_number);
            Rect bounds = BoundsInWindow(popup);
            new Rect(window.Window.ClientSize).Contains(bounds).ShouldBeTrue(bounds.ToString());
        });

    [Fact]
    public Task A_diagnostic_shows_its_icon_message_source_code_link_and_related_places_as_in_VS_Code() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--diagnostic-details");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            string path = Path.Combine(AppContext.BaseDirectory, "details.txt");
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text, path);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            Diagnostic diagnostic = document.Diagnostics[0];
            diagnostic.Source.ShouldBe("fake");
            diagnostic.CodeDescription.ShouldBe(new Uri("https://example.com/echo"));
            diagnostic
                .RelatedInformation.Select(r => (r.Start, r.Message))
                .ShouldBe([(new TextPosition(1, 2), "related here"), (new TextPosition(3, 4), "elsewhere")]);

            PopupFrame popup = await HoverAsync(window, 4, "two is a word in", time);

            Control part = ((Panel)Content(popup)).Children[0];
            part.GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            TextBlock[] texts = [.. part.GetVisualDescendants().OfType<TextBlock>()];
            texts
                .Select(TextOf)
                .ShouldBe([$"{text} fake(echo)", "details.txt(1, 2): related here", "other.txt(3, 4): elsewhere"]);
            texts.ShouldAllBe(block => block.FontFamily == editor.FontFamily);
            // The code, underlined in the link color, at the 60% of its source.
            Run code = texts[0].Inlines.ShouldNotBeNull().OfType<Run>().Single(run => run.Text == "echo");
            code.TextDecorations.ShouldBe(TextDecorations.Underline);
            code.Foreground.ShouldBeAssignableTo<ISolidColorBrush>().ShouldNotBeNull().Opacity.ShouldBe(0.6);
            texts[0]
                .Inlines.ShouldNotBeNull()
                .OfType<Run>()
                .Single(run => run.Text == " fake")
                .Foreground.ShouldNotBeNull()
                .Opacity.ShouldBe(0.6);

            // A place in the document is a link there, in the link color; one elsewhere, which the editor cannot show,
            // is not (Q121).
            Run[] places = [.. texts.Skip(1).Select(block => block.Inlines.ShouldNotBeNull().OfType<Run>().First())];
            places[0].Foreground.ShouldBe(PopupColors.Default.Link);
            places[1].IsSet(TextElement.ForegroundProperty).ShouldBeFalse();
            window.Click(TextPointAt(texts[2], 2), MouseButton.Left);
            Popup(window).ShouldNotBeNull();
            window.Click(TextPointAt(texts[1], 2), MouseButton.Left);
            Popup(window).ShouldBeNull();
            editor.Selection.ShouldBe(new TextSelection(1));
        });

    [Fact]
    public Task Inline_code_shows_in_the_editors_font_on_the_themes_code_background() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            // A dark color theme in a light app: popups take the color theme's colors.
            var editor = new CodeEditor { Document = document, ColorTheme = CodeEditorColorTheme.DarkPlus };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            PopupFrame popup = await HoverAsync(window, 5, "key is a word in", time);

            var paragraph = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => TextOf(t) == "key is a word in json.");
            Run code = paragraph.Inlines.ShouldNotBeNull().OfType<Run>().Single(run => run.Text == "json");
            code.FontFamily.ShouldBe(editor.FontFamily);
            // VS Code's textCodeBlock.background of dark themes, on editorHoverWidget.background.
            code.Background.ShouldBeAssignableTo<ISolidColorBrush>()
                .ShouldNotBeNull()
                .Color.ShouldBe(Color.Parse("#660A0A0A"));
            popup
                .Background.ShouldBeAssignableTo<ISolidColorBrush>()
                .ShouldNotBeNull()
                .Color.ShouldBe(Color.Parse("#252526"));
            // At the paragraph's size, so that it fits its line.
            code.IsSet(TextElement.FontSizeProperty).ShouldBeFalse();
        });

    [Fact]
    public Task Code_without_a_color_of_its_own_shows_in_the_editors_foreground() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            // Plain text has no grammar: no token of its code block has a color of its own.
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "some plain words");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            PopupFrame popup = await HoverAsync(window, 1, "some is a word in", time);

            // In Light+'s black text: the code block, in the editor's font, after the diagnostic.
            TextBlock code = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Last(text => text.FontFamily == window.Editor.FontFamily);
            code.Inlines.ShouldNotBeNull().OfType<Run>().Single().IsSet(TextElement.ForegroundProperty).ShouldBeFalse();
            window.RenderAndCountPixels(Colors.Black, BoundsInWindow(code)).ShouldBeGreaterThan(0);
        });

    [Fact]
    public Task An_empty_diagnostic_at_a_lines_end_shows_where_its_squiggle_is() =>
        _ui.Run(async () =>
        {
            // As a missing semicolon: no server, only the diagnostic.
            var time = new ManualTime();
            var document = new StandaloneDocument(CodeLanguage.PlainText, "let a = 1\nlet b = 2", time);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            document.SetDiagnostics([
                new Diagnostic(
                    document.CurrentSnapshot,
                    new TextRange(9, 0),
                    DiagnosticSeverity.Error,
                    null,
                    "';' expected"
                ),
            ]);

            await HoverAsync(window, 9, "';' expected", time);
        });

    [Fact]
    public Task The_popup_takes_the_editors_theme_variant() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            var editor = new CodeEditor { Document = document };
            // A dark editor in a light window.
            using var window = new EditorWindow(
                editor,
                new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = editor }
            );
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            PopupFrame popup = await HoverAsync(window, 5, "is a word in", time);

            window.Window.ActualThemeVariant.ShouldBe(ThemeVariant.Light);
            popup.ActualThemeVariant.ShouldBe(ThemeVariant.Dark);
            // Dark+'s hover background, as VS Code defaults it, in the frame's pixels.
            Color background = popup.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color;
            background.ShouldBe(Color.Parse("#252526"));
            Rect bounds = BoundsInWindow(popup);
            window.RenderAndCountPixels(background, bounds).ShouldBeGreaterThan(100);
        });

    [Fact]
    public Task The_popup_stays_over_its_word_and_itself_and_closes_when_the_pointer_leaves_both() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two\nthree";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var outside = new Border { Width = 100, Background = Brushes.Gray };
            DockPanel.SetDock(outside, Dock.Right);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { outside, editor } });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            int three = text.IndexOf("three", StringComparison.Ordinal);
            int two = text.IndexOf("two", StringComparison.Ordinal);

            // Over another character of the word, and over the popup, it stays.
            PopupFrame popup = await HoverAsync(window, three, "three is a word in", time);
            window.MoveMouse(window.PointAt(three + 3));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            Popup(window).ShouldBeSameAs(popup);

            // Out of the popup to the side of the editor: it closes after a moment, VS Code's 300 ms.
            window.MoveMouse(outside.TranslatePoint(new Point(50, 50), window.Window)!.Value);
            Pass(window, time, TimeSpan.FromMilliseconds(299));
            Popup(window).ShouldBeSameAs(popup);
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            Popup(window).ShouldBeNull();

            // Over another word, though under the same diagnostic: it stays a moment, so that the pointer can cross
            // other text on its way into the popup, and keeps it then.
            popup = await HoverAsync(window, 0, "one is a word in", time);
            window.MoveMouse(window.PointAt(two));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(window.PointAt(two));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task Clicked_into_to_select_its_text_the_popup_stays_wherever_the_pointer_goes_until_a_click_outside_or_typing() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two\nthree";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            int three = text.IndexOf("three", StringComparison.Ordinal);
            SelectableTextBlock paragraph = Paragraph(await HoverAsync(window, three, "three is a word in", time));
            Rect box = BoundsInWindow(paragraph);
            var from = new Point(box.Left + 1, box.Center.Y);
            var to = new Point(box.Left + 40, box.Center.Y);
            window.MoveMouse(from);

            // A drag that selects its text, out over its edge and back: the popup stays.
            window.Window.MouseDown(from, MouseButton.Left);
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseMove(window.PointAt(0), RawInputModifiers.LeftMouseButton);
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(to, MouseButton.Left);
            window.Layout();
            IsShown(paragraph).ShouldBeTrue();
            paragraph.SelectedText.ShouldNotBeEmpty();

            // Clicked into, it stays also over other text, which shows no hover of its own, as in VS Code.
            window.MoveMouse(window.PointAt(0));
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            IsShown(paragraph).ShouldBeTrue();
            paragraph.SelectedText.ShouldNotBeEmpty();

            // A click outside it ends it.
            window.Click(window.PointAt(0), MouseButton.Left);
            await Wait.UntilAsync(() => Popup(window) is null);
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();

            // So does typing, which goes into the text.
            paragraph = Paragraph(await HoverAsync(window, three, "three is a word in", time));
            box = BoundsInWindow(paragraph);
            window.MoveMouse(box.Center);
            window.Click(box.Center, MouseButton.Left);
            window.MoveMouse(window.PointAt(0));
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            IsShown(paragraph).ShouldBeTrue();
            int caret = editor.Selection.Caret;
            window.Type("x");
            Popup(window).ShouldBeNull();
            document.Text.ShouldBe(text.Insert(caret, "x"));
        });

    [Fact]
    public Task A_click_in_the_editor_and_a_key_in_the_text_end_the_hover_unless_the_key_starts_a_sequence() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two three";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            int two = text.IndexOf("two", StringComparison.Ordinal);

            // A click, also on the hovered word; until the pointer moves, it shows no other.
            await HoverAsync(window, two, "two is a word in", time);
            int asked = fake.LinesStartingWith("hover ").Count;
            window.Click(window.PointAt(two), MouseButton.Left);
            Popup(window).ShouldBeNull();
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").Count.ShouldBe(asked);

            // An arrow key, which moves no caret of a hover the mouse opened.
            await HoverAsync(window, two, "two is a word in", time);
            window.Press(Key.Right);
            Popup(window).ShouldBeNull();

            // Not the first key of a sequence, which may show the hover.
            await HoverAsync(window, two, "two is a word in", time);
            window.Press(Key.K, EditorWindow.Command);
            Popup(window).ShouldNotBeNull();
        });

    [Fact]
    public Task No_hover_starts_while_the_mouse_drags_a_selection_nor_where_it_rests_after() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two three";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            int three = text.IndexOf("three", StringComparison.Ordinal);

            // Each pause is longer than the pointer rests before a hover starts.
            window.MoveMouse(window.PointAt(0));
            window.Window.MouseDown(window.PointAt(0), MouseButton.Left);
            window.Window.MouseMove(window.PointAt(three), RawInputModifiers.LeftMouseButton);
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            window.Window.MouseUp(window.PointAt(three), MouseButton.Left);
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            editor.Selection.IsEmpty.ShouldBeFalse();

            // Once the pointer moves on, resting it starts one again.
            await HoverAsync(window, three, "three is a word in", time);
        });

    [Fact]
    public Task Escape_ends_the_hover_along_with_the_search_bar_or_completion() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument answer = await CompletionTests.SetCompletionAsync(
                server,
                fake,
                CompletionTests.Items("Console")
            );
            const string text = "one two\nCon";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);

            // Opening the search bar ends the hover; Escape in the query ends one shown since, and the bar.
            await HoverAsync(window, 0, "one is a word in", time);
            window.Press(Key.F, EditorWindow.Command);
            Popup(window).ShouldBeNull();
            SearchBar bar = editor.GetVisualDescendants().OfType<SearchBar>().Single();
            bar.FindBox.IsFocused.ShouldBeTrue();
            await HoverAsync(window, 4, "two is a word in", time);
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            bar.IsOpen.ShouldBeFalse();

            // In the text, it ends completion's list too.
            await CompletionTests.OpenAsync(window, text.Length);
            await HoverAsync(window, 0, "one is a word in", time);
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            CompletionTests.List(window).ShouldBeNull();
        });

    [Fact]
    public Task Ctrl_K_Ctrl_I_shows_the_hover_at_the_caret_and_again_puts_the_keyboard_into_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            // Right after the word, as typing leaves the caret.
            editor.Selection = new TextSelection(3);

            // At once: the clock stands still.
            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);

            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Texts(popup).ShouldContain($"{text} (echo)");
            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);
            popup.IsKeyboardFocusWithin.ShouldBeTrue();
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task A_hover_with_the_keyboard_in_it_shows_the_themes_focus_outline_inside_its_border() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            editor.Selection = new TextSelection(1);
            // Light+'s focusBorder, as VS Code's light themes default it.
            Color focusBorder = Color.Parse("#0090F1");

            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);
            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Rect bounds = BoundsInWindow(popup);
            window.RenderAndCountPixels(focusBorder, bounds, tolerance: 2).ShouldBe(0);

            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);
            popup.IsKeyboardFocusWithin.ShouldBeTrue();
            // A pixel inside the border, all around.
            Rect inside = bounds.Deflate(1);
            int outline = window.RenderAndCountPixels(focusBorder, bounds, tolerance: 2);
            outline.ShouldBeGreaterThan((int)(inside.Width + inside.Height));
            window.RenderAndCountPixels(focusBorder, inside.Deflate(1), tolerance: 2).ShouldBe(0);

            window.Press(Key.Escape);
            popup = await HoverAsync(window, 4, "two is a word in", time);
            window.RenderAndCountPixels(focusBorder, BoundsInWindow(popup), tolerance: 2).ShouldBe(0);
        });

    [Fact]
    public Task Ctrl_K_Ctrl_I_puts_the_keyboard_into_a_hover_the_mouse_opened_which_then_stays_where_the_pointer_goes() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            await HoverAsync(window, 4, "two is a word in", time);

            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);

            // The word under the pointer's, not the caret's.
            PopupFrame popup = Popup(window).ShouldNotBeNull();
            popup.IsKeyboardFocusWithin.ShouldBeTrue();
            Texts(popup).ShouldContain(t => t.Contains("two is a word in", StringComparison.Ordinal));
            window.MoveMouse(window.PointAt(0) + new Point(0, 200));
            // Longer than the hover waits for the pointer to come back.
            Pass(window, time, TimeSpan.FromMilliseconds(500));
            Popup(window).ShouldNotBeNull().IsKeyboardFocusWithin.ShouldBeTrue();
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task The_popup_closes_on_typing_Escape_scrolling_and_focus_loss() =>
        _ui.Run(async () =>
        {
            // Without diagnostics: the echoed whole text would make a long hover.
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            string text = string.Concat(Enumerable.Range(0, 200).Select(i => $"word{i}\n"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });
            await server.StartAsync();

            await HoverAsync(window, 1, "word0 is a word in", time);
            window.Type("x");
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word in", time);
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word in", time);
            editor.ScrollTo(new TextRange(text.Length, 0));
            window.Layout();
            Popup(window).ShouldBeNull();

            editor.ScrollTo(new TextRange(0, 0));
            await HoverAsync(window, 1, "xword0 is a word in", time);
            button.Focus();
            window.Layout();
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task The_server_is_asked_halfway_through_the_hover_delay_and_its_hover_shows_once_the_delay_is_over() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            editor.Options.Hover.Delay.ShouldBe(TimeSpan.FromMilliseconds(300));

            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(149));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            await fake.WaitForAsync("hover ");
            // Answered, but the delay is not over: nothing shows yet.
            await Wait.UntilAsync(() => editor.HoverIsAnswered);
            window.Render();
            Popup(window).ShouldBeNull();

            Pass(window, time, TimeSpan.FromMilliseconds(150));

            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Texts(popup).ShouldContain($"{text} (echo)");
        });

    [Fact]
    public Task The_wait_starts_again_on_another_character_and_Loading_shows_while_the_server_is_late() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow");
            var editor = new CodeEditor
            {
                Document = document,
                Options = CodeEditorOptions.Default with
                {
                    Hover = new HoverOptions { Delay = TimeSpan.FromSeconds(1) },
                },
            };
            using var window = new EditorWindow(editor);
            await server.StartAsync();

            // Within a character, the wait goes on; on another one, it starts again.
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            window.MoveMouse(window.PointAt(1) + new Point(1, 0));
            Pass(window, time, TimeSpan.FromMilliseconds(100));
            window.MoveMouse(window.PointAt(2));
            Pass(window, time, TimeSpan.FromMilliseconds(499));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];

            // The server never answers for "slow": at three times the delay, the hover says so.
            Pass(window, time, TimeSpan.FromMilliseconds(2499));
            window.Render();
            Popup(window).ShouldBeNull();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            PopupFrame popup = await WaitForPopupAsync(window, "Loading…");
            Texts(popup).ShouldBe(["Loading…"]);

            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            await fake.WaitForAsync($"cancel {id}");
            fake.LinesStartingWith("hover ").Count.ShouldBe(1);
        });

    [Fact]
    public void The_hover_delay_is_between_0_and_10_seconds()
    {
        new HoverOptions().Delay.ShouldBe(TimeSpan.FromMilliseconds(300));
        new HoverOptions { Delay = TimeSpan.FromMilliseconds(-5) }.Delay.ShouldBe(TimeSpan.Zero);
        new HoverOptions { Delay = TimeSpan.FromMinutes(1) }.Delay.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task Escape_ends_a_hover_that_still_waits_for_its_server() =>
        _ui.Run(async () =>
        {
            // Without diagnostics, nothing shows until the server answers, and it never answers for "slow".
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];
            Popup(window).ShouldBeNull();

            window.Press(Key.Escape);

            // Its request is cancelled, so that a late answer opens nothing.
            await fake.WaitForAsync($"cancel {id}");
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task A_server_that_stops_serving_the_document_ends_its_hover() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "word");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            await HoverAsync(window, 1, "word is a word in", time);

            // The process crashes: its information and its diagnostics' messages are outdated.
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");

            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task New_colors_end_the_hover_whose_code_shows_the_old_ones() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            var editor = new CodeEditor { Document = document };
            var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = editor };
            using var window = new EditorWindow(editor, scope);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            await HoverAsync(window, 5, "is a word in", time);

            scope.RequestedThemeVariant = ThemeVariant.Dark;

            // Once the text area shows Dark+, no code in Light+'s colors shows beside it.
            await Wait.UntilAsync(() =>
            {
                window.Render();
                return EditorWindow.PartOf(editor).Background is ISolidColorBrush { Color: var color }
                    && color == Color.Parse("#1E1E1E");
            });
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task The_hover_grows_up_to_VS_Codes_limits_and_to_the_size_the_user_resized_a_hover_to() =>
        _ui.Run(async () =>
        {
            HoverPopup.LastResizedSize = default;
            try
            {
                var fake = new FakeServer("--no-diagnostics");
                var time = new ManualTime();
                await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
                using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
                using var window = new EditorWindow(new CodeEditor { Document = document }, width: 1600, height: 1200);
                await server.StartAsync();

                // 66% of the editor's width, and a quarter of its height, at least 750 by 250 pixels.
                PopupFrame popup = await HoverAsync(window, 1, "one is a word in", time);
                popup.MaxWidth.ShouldBe(1056);
                popup.MaxHeight.ShouldBe(300);

                // Below the first line: its right and bottom edges resize it.
                Rect bounds = BoundsInWindow(popup);
                var corner = new Point(bounds.Right - 2, bounds.Bottom - 2);
                var dragged = new Point(corner.X + 1_200, corner.Y + 400);
                window.MoveMouse(corner);
                window.Window.MouseDown(corner, MouseButton.Left);
                window.Window.MouseMove(dragged, RawInputModifiers.LeftMouseButton);
                window.Window.MouseUp(dragged, MouseButton.Left);
                window.Layout();
                Size resized = HoverPopup.LastResizedSize;
                resized.Width.ShouldBeGreaterThan(1056);
                resized.Height.ShouldBeGreaterThan(300);
                popup.Width.ShouldBe(resized.Width);

                // A later hover takes the size of its content, and may grow as large as the resized one.
                window.Press(Key.Escape);
                popup = await HoverAsync(window, 4, "two is a word in", time);
                popup.Width.ShouldBe(double.NaN);
                popup.MaxWidth.ShouldBe(resized.Width);
                popup.MaxHeight.ShouldBe(resized.Height);
            }
            finally
            {
                HoverPopup.LastResizedSize = default;
            }
        });

    [Fact]
    public Task A_long_hover_shows_all_of_its_content_and_scrolls() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            // The server's hover shows the hovered line as code: 20,000 characters.
            string line = string.Concat(Enumerable.Repeat("word ", 4_000)).TrimEnd();
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, line);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();

            PopupFrame popup = await HoverAsync(window, 1, "word is a word in", time);

            var content = (HoverContent)Content(popup);
            while (!content.IsComplete)
                content.AddNextPart();
            window.Layout();
            string.Concat(CodeTexts(popup, window.Editor.FontFamily)).ShouldBe(line);
            PopupScroller scroller = popup.GetVisualDescendants().OfType<PopupScroller>().Single();
            content.Bounds.Height.ShouldBeGreaterThan(scroller.Bounds.Height);
            BoundsInWindow(popup).Height.ShouldBeLessThanOrEqualTo(popup.MaxHeight);
        });

    [Fact]
    public Task Hover_content_builds_a_part_at_once_and_the_next_parts_in_the_frames_after_while_it_shows() =>
        _ui.Run(() =>
        {
            // 300 paragraphs of 100 characters: parts of as many paragraphs as a part has characters for.
            HoverBlock[] blocks =
            [
                .. Enumerable.Range(0, 300).Select(i => new HoverParagraph([new HoverSpan(new string('p', 100))])),
            ];
            int perPart = HoverParts.MaxCharacters / 100;
            HoverContent content = HoverView.Create(blocks, Look());
            content.Children.Count.ShouldBe(perPart);
            var host = new Border();
            using var window = new EditorWindow(new CodeEditor(), host);
            int grew = 0;
            host.AddHandler(HoverContent.GrewEvent, (_, _) => grew++);

            // Out of view by the next frame: nothing more is built.
            host.Child = content;
            host.Child = null;
            window.Render();
            content.Children.Count.ShouldBe(perPart);
            content.IsComplete.ShouldBeFalse();

            // In view, the next part comes with the next frame, and the parts after it with the frames after that.
            host.Child = content;
            window.Render();
            content.Children.Count.ShouldBeGreaterThan(perPart);
            while (!content.IsComplete)
                content.AddNextPart();
            content.Children.Count.ShouldBe(300);
            grew.ShouldBe(300 / perPart - 1);
            // The space between blocks, as between blocks built at once.
            window.Layout();
            (content.Children[^1].Bounds.Top - content.Children[^2].Bounds.Bottom).ShouldBe(HoverView.Spacing);
        });

    [Fact]
    public Task The_popup_stays_inside_a_window_that_narrows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            string text = new string('a', 36) + " word";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 700);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            PopupFrame popup = await HoverAsync(
                window,
                text.IndexOf("word", StringComparison.Ordinal),
                "word is a word in",
                time
            );
            double right = BoundsInWindow(popup).Right;

            // Narrower than the popup's right edge, while the hovered word stays in view.
            window.Window.Width = right - 40;
            window.Layout();
            window.Render();

            Popup(window).ShouldBeSameAs(popup);
            Rect bounds = BoundsInWindow(popup);
            new Rect(window.Window.ClientSize).Contains(bounds).ShouldBeTrue($"{bounds} in {window.Window.ClientSize}");
        });

    [Fact]
    public Task A_popup_whose_space_is_not_whole_pixels_stays_inside_the_window() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor();
            using var window = new EditorWindow(editor, width: 400, height: 300);
            // Taller than either side, so that its height is limited to the space below its place, 183.4 pixels.
            var popup = new EditorPopup(editor, new Border { Height = 1_000, Background = Brushes.Gray }, "Tall");

            popup.Show(editor, () => new Rect(10, 100.6, 8, 16));
            window.Layout();

            BoundsInWindow(popup.Root).Bottom.ShouldBeLessThanOrEqualTo(window.Window.ClientSize.Height);
            popup.Hide();
        });

    [Fact]
    public Task An_outdated_hover_request_is_cancelled() =>
        _ui.Run(async () =>
        {
            // Without diagnostics, nothing shows for "slow", so that leaving it ends its hover at once.
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "slow fast";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();

            // The server never answers for "slow".
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];
            (await fake.WaitForAsync("hover ")).ShouldEndWith(" slow");

            await HoverAsync(window, text.IndexOf("fast", StringComparison.Ordinal), "fast is a word in", time);
            await fake.WaitForAsync($"cancel {id}");
        });

    [Fact]
    public Task The_most_severe_diagnostics_are_found_among_however_many_there_are() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "word");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            TextSnapshot snapshot = document.CurrentSnapshot;
            // 10,000 on one word, their severities taking turns from hints to errors.
            document.SetDiagnostics([
                .. Enumerable
                    .Range(0, 10_000)
                    .Select(i => new Diagnostic(
                        snapshot,
                        new TextRange(0, 4),
                        (DiagnosticSeverity)(i % 4),
                        null,
                        $"{i}"
                    )),
            ]);
            window.Layout();
            using var squiggles = new SquiggleLayer(EditorWindow.PartOf(editor).TextArea, document);

            IReadOnlyList<Diagnostic> shown = [.. squiggles.At(1).Take(120)];

            // First the errors, which come every fourth, in the server's order.
            shown.Select(d => d.Message).ShouldBe(Enumerable.Range(0, 120).Select(i => $"{4 * i + 3}"));
        });

    [Fact]
    public Task A_hover_over_thousands_of_diagnostics_reads_them_a_part_at_a_time_the_most_severe_first() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a word here");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            TextSnapshot snapshot = document.CurrentSnapshot;
            // 10,000 on one word, their severities taking turns from hints to errors.
            const int count = 10_000;
            document.SetDiagnostics([
                .. Enumerable
                    .Range(0, count)
                    .Select(i => new Diagnostic(
                        snapshot,
                        new TextRange(2, 4),
                        (DiagnosticSeverity)(i % 4),
                        null,
                        $"{i}"
                    )),
            ]);
            window.Layout();
            using var squiggles = new SquiggleLayer(EditorWindow.PartOf(editor).TextArea, document);
            var read = new ReadCount(squiggles.At(3));
            read.Count.ShouldBe(count);

            HoverContent content = HoverView.Create(HoverBlocks.Of(read), Look());

            // As it opens, the first part, and the one that did not fit it.
            content.IsComplete.ShouldBeFalse();
            read.Read.ShouldBe(content.Children.Count + 1);
            TextsOf(content).Take(2).ShouldBe(["3", "7"]);
            // Each next part reads its own.
            content.AddNextPart();
            read.Read.ShouldBe(content.Children.Count + 1);
            while (!content.IsComplete)
                content.AddNextPart();
            read.Read.ShouldBe(count);
            TextsOf(content).Skip(count / 4).First().ShouldBe("2");
            TextsOf(content).Last().ShouldBe($"{count - 4}");
        });

    [Fact]
    public Task A_diagnostic_without_a_message_shows_its_code_and_a_related_place_longer_than_a_part_in_parts() =>
        _ui.Run(() =>
        {
            string related = string.Join(' ', Enumerable.Repeat("word", 1_020));
            var diagnostic = new Diagnostic(TextSnapshot.FromString(""), default, DiagnosticSeverity.Warning, "W1", "")
            {
                RelatedInformation = [new DiagnosticRelatedInformation(new Uri("file:///a.py"), new(2, 4), related)],
            };
            IReadOnlyCollection<HoverBlock> blocks = HoverBlocks.Of([diagnostic]);
            blocks.SelectMany(HoverParts.Split).ShouldAllBe(piece => HoverParts.Fits(HoverParts.Size(piece)));
            var host = new Border();
            using var window = new EditorWindow(new CodeEditor(), host);

            HoverContent content = HoverView.Create(blocks, Look());
            host.Child = content;
            while (!content.IsComplete)
                content.AddNextPart();
            window.Render();

            content.GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            string[] texts = [.. TextsOf(content)];
            texts[0].ShouldBe(" (W1)");
            string.Concat(texts.Skip(1)).ShouldBe("a.py(2, 4): " + related);
        });

    [Fact]
    public Task The_popup_stays_over_a_word_longer_than_the_hovered_word_reaches() =>
        _ui.Run(async () =>
        {
            string text = new string('x', 2_500) + " end";
            var time = new ManualTime();
            var document = new StandaloneDocument(CodeLanguage.PlainText, text, time);
            var editor = new CodeEditor { Document = document, WordWrap = true };
            using var window = new EditorWindow(editor, height: 800);
            document.SetDiagnostics([
                new Diagnostic(
                    document.CurrentSnapshot,
                    new TextRange(0, 2_500),
                    DiagnosticSeverity.Error,
                    null,
                    "long"
                ),
            ]);
            PopupFrame popup = await HoverAsync(window, 0, "long", time);

            // Past where the hovered word was cut, in the same word.
            window.MoveMouse(window.PointAt(HoverController.MaxWordReach + 1));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(window.PointAt(2_499));
            Popup(window).ShouldBeSameAs(popup);

            window.MoveMouse(window.PointAt(text.IndexOf("end", StringComparison.Ordinal)));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task The_pointer_over_a_long_lines_hidden_tail_is_over_no_text() =>
        _ui.Run(async () =>
        {
            // The editor shows the first 3,000 characters of the word, wrapped, and "..." for its other ten million.
            string text = new string('x', 10_000_000) + "\nend";
            var time = new ManualTime();
            var document = new StandaloneDocument(CodeLanguage.PlainText, text, time);
            using var window = new EditorWindow(new CodeEditor { Document = document, WordWrap = true }, height: 900);
            document.SetDiagnostics([
                new Diagnostic(document.CurrentSnapshot, new TextRange(0, 10), DiagnosticSeverity.Error, null, "long"),
            ]);
            await HoverAsync(window, 5, "long", time);

            // Over the "...", where the hidden tail starts.
            window.MoveMouse(window.PointAt(VisibleText.LineLimit));
            Pass(window, time, TimeSpan.FromMilliseconds(300));

            Popup(window).ShouldBeNull();

            // Straight on to the next line, past the hidden tail.
            await HoverAsync(window, 5, "long", time);
            window.MoveMouse(window.PointAt(text.Length - 2));
            Pass(window, time, TimeSpan.FromMilliseconds(300));

            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task The_hovered_word_reaches_a_bounded_distance_from_the_pointer() =>
        _ui.Run(() =>
        {
            // One word of a million characters on a line, after a short one.
            var buffer = new AvaloniaEdit.Document.TextDocument("ab " + new string('x', 1_000_000) + " cd");
            const int reach = HoverController.MaxWordReach;

            Hovered.WordAt(buffer, 1).ShouldBe(new TextRange(0, 2));
            Hovered.WordAt(buffer, 3).ShouldBe(new TextRange(3, reach));
            // Nor past the shown part of a long line.
            Hovered.WordAt(buffer, 2_500).ShouldBe(TextRange.FromBounds(2_500 - reach, VisibleText.LineLimit));

            // The pointer may move on along a cut word, in the shown part of its line only: neither into its hidden
            // tail nor past it, onto the next line.
            var tail = new AvaloniaEdit.Document.TextDocument(new string('x', 10_000_000) + "\nend");
            TextRange word = Hovered.WordAt(tail, 5);
            Hovered.AlongWord(tail, word).ShouldBe(new TextRange(0, VisibleText.LineLimit));
            Hovered.AlongWord(buffer, new TextRange(0, 2)).ShouldBe(new TextRange(0, 2));
            Hovered.AlongWord(buffer, new TextRange(2, 1)).ShouldBe(new TextRange(2, 1));
        });

    /// <summary>
    /// Moves the pointer away, then rests it on the character at <paramref name="offset"/> for the hover's delay on the
    /// clock of <paramref name="time"/>, and waits for the popup with <paramref name="text"/>.
    /// </summary>
    private static Task<PopupFrame> HoverAsync(EditorWindow window, int offset, string text, ManualTime time)
    {
        window.MoveMouse(window.PointAt(offset) + new Point(0, 200));
        window.MoveMouse(window.PointAt(offset));
        Pass(window, time, window.Editor.Options.Hover.Delay);
        return WaitForPopupAsync(window, text);
    }

    /// <summary>
    /// Moves the clock of <paramref name="time"/> on by <paramref name="span"/>, and runs the UI thread's work that its
    /// timers posted, so that what they do has happened.
    /// </summary>
    private static void Pass(EditorWindow window, ManualTime time, TimeSpan span)
    {
        time.Advance(span);
        window.Layout();
    }

    private static async Task<PopupFrame> WaitForPopupAsync(EditorWindow window, string text)
    {
        PopupFrame? popup = null;
        await Wait.UntilAsync(() =>
        {
            window.Render();
            popup = Popup(window);
            return popup is not null && Texts(popup).Any(t => t.Contains(text, StringComparison.Ordinal));
        });
        return popup ?? throw new InvalidOperationException();
    }

    /// <summary>Where the window shows the character at <paramref name="index"/> of a text block's text.</summary>
    private static Point TextPointAt(TextBlock text, int index) =>
        text.TranslatePoint(
            text.TextLayout.HitTestTextPosition(index).Center,
            TopLevel.GetTopLevel(text) ?? throw new InvalidOperationException("Not shown.")
        ) ?? throw new InvalidOperationException("Not shown.");

    private static IEnumerable<string> TextsOf(HoverContent content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    /// <summary>Diagnostics that count how many of them were read.</summary>
    private sealed class ReadCount(IReadOnlyCollection<Diagnostic> diagnostics) : IReadOnlyCollection<Diagnostic>
    {
        private readonly IReadOnlyCollection<Diagnostic> _diagnostics = diagnostics;

        public int Read { get; private set; }

        public int Count => _diagnostics.Count;

        public IEnumerator<Diagnostic> GetEnumerator()
        {
            foreach (Diagnostic diagnostic in _diagnostics)
            {
                Read++;
                yield return diagnostic;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A popup's look with the hover's colors, in a font that every test machine has.</summary>
    private static PopupLook Look() =>
        new(
            new FontFamily("DejaVu Sans Mono"),
            13,
            PopupColors.Default,
            FontFamily.Default,
            13,
            PopupColors.Default.HoverForeground
        );

    /// <summary>The paragraph of the server's hover, "… is a word in …".</summary>
    private static SelectableTextBlock Paragraph(PopupFrame popup) =>
        popup
            .GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .First(block => TextOf(block).Contains("is a word in", StringComparison.Ordinal));

    /// <summary>Whether <paramref name="content"/> still shows: a new hover shows new content in the same frame.</summary>
    private static bool IsShown(Visual content) => TopLevel.GetTopLevel(content) is not null;

    /// <summary>The hover's popup in the window's overlay layer; null while it is closed.</summary>
    private static PopupFrame? Popup(EditorWindow window) => PopupFrames.Find(window, "Hover");

    private static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    private static Control Content(PopupFrame popup) => PopupFrames.Content(popup);

    /// <summary>The texts of the hover's code blocks and their pieces, in the editor's <paramref name="font"/>.</summary>
    private static IEnumerable<string> CodeTexts(PopupFrame popup, FontFamily font) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Where(block => block.FontFamily == font).Select(TextOf);

    private static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    private static string TextOf(Control control) =>
        control is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : ((TextBlock)control).Text ?? "";
}
