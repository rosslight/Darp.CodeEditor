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

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Hover: the diagnostics' messages above the server's Markdown, in a popup in the app's tooltip look that stays inside
/// the window, takes the editor's theme variant, and closes when it is no longer wanted.
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
            await using var server = new LanguageServer(fake.Options);
            const string text = "{\n  \"key\": 1\n}";
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, text);
            var editor = new CodeEditor { Document = document };
            // Near the window's right edge, so that the popup has to move left to stay inside.
            using var window = new EditorWindow(editor, width: 300, height: 200);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1 && window.ColorAt(5) == s_propertyName);

            window.MoveMouse(window.PointAt(text.IndexOf("key", StringComparison.Ordinal)));

            PopupFrame popup = await WaitForPopupAsync(window, "is a word in");
            Control[] parts = [.. ((Panel)Content(popup)).Children];
            // The diagnostic's message and code, a line, the server's paragraph, and its code block.
            TextOf(parts[0]).ShouldBe($"{text} (echo)");
            parts[1].ShouldBeOfType<Border>().Height.ShouldBe(1);
            TextOf(parts[2]).ShouldBe("key is a word in json.");
            ((TextBlock)parts[2]).Inlines.ShouldNotBeNull().OfType<Run>().First().FontWeight.ShouldBe(FontWeight.Bold);
            // Code sits on the popup's background, in the editor's font: no box of the editor's background.
            var code = parts[3].ShouldBeAssignableTo<SelectableTextBlock>().ShouldNotBeNull();
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
    public Task Inline_code_shows_in_the_editors_font_on_the_themes_code_background() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            // A dark color theme in a light app: popups take the color theme's colors.
            var editor = new CodeEditor { Document = document, ColorTheme = CodeEditorColorTheme.DarkPlus };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            PopupFrame popup = await HoverAsync(window, 5, "key is a word in");

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
            await using var server = new LanguageServer(fake.Options);
            // Plain text has no grammar: no token of its code block has a color of its own.
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "some plain words");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            PopupFrame popup = await HoverAsync(window, 1, "some is a word in");

            // In Light+'s black text, on its white background.
            TextBlock code = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(text => text.FontFamily == window.Editor.FontFamily);
            code.Inlines.ShouldNotBeNull().OfType<Run>().Single().IsSet(TextElement.ForegroundProperty).ShouldBeFalse();
            window.RenderAndCountPixels(Colors.Black, BoundsInWindow(code)).ShouldBeGreaterThan(0);
        });

    [Fact]
    public Task An_empty_diagnostic_at_a_lines_end_shows_where_its_squiggle_is() =>
        _ui.Run(async () =>
        {
            // As a missing semicolon: no server, only the diagnostic.
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "let a = 1\nlet b = 2");
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

            window.MoveMouse(window.PointAt(9));

            await WaitForPopupAsync(window, "';' expected");
        });

    [Fact]
    public Task The_popup_takes_the_editors_theme_variant() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            var editor = new CodeEditor { Document = document };
            // A dark editor in a light window.
            using var window = new EditorWindow(
                editor,
                new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = editor }
            );
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            window.MoveMouse(window.PointAt(5));

            PopupFrame popup = await WaitForPopupAsync(window, "is a word in");
            window.Window.ActualThemeVariant.ShouldBe(ThemeVariant.Light);
            popup.ActualThemeVariant.ShouldBe(ThemeVariant.Dark);
            // The app's dark tooltip, and the code in Dark+'s background, in the frame's pixels.
            Color background = popup.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color;
            Luminance(background).ShouldBeLessThan(0.5);
            Rect bounds = BoundsInWindow(popup);
            window.RenderAndCountPixels(background, bounds).ShouldBeGreaterThan(100);
            window.RenderAndCountPixels(Color.Parse("#1E1E1E"), bounds).ShouldBeGreaterThan(100);
        });

    [Fact]
    public Task The_popup_stays_over_its_word_and_itself_and_closes_when_the_pointer_leaves_both() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            const string text = "one two\nthree";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var outside = new Border { Width = 100, Background = Brushes.Gray };
            DockPanel.SetDock(outside, Dock.Right);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { outside, editor } });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            // Over another character of the word, and over the popup, it stays.
            PopupFrame popup = await HoverAsync(
                window,
                text.IndexOf("three", StringComparison.Ordinal),
                "three is a word in"
            );
            window.MoveMouse(window.PointAt(text.IndexOf("three", StringComparison.Ordinal) + 3));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            Popup(window).ShouldBeSameAs(popup);

            // Out of the popup to the side of the editor: it closes after a moment.
            window.MoveMouse(outside.TranslatePoint(new Point(50, 50), window.Window)!.Value);
            await Wait.UntilAsync(() => Popup(window) is null);

            // Over another word, though under the same diagnostic: it stays a moment, so that the pointer can cross
            // other text on its way into the popup, and keeps it then.
            popup = await HoverAsync(window, 0, "one is a word in");
            window.MoveMouse(window.PointAt(text.IndexOf("two", StringComparison.Ordinal)));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(window.PointAt(text.IndexOf("two", StringComparison.Ordinal)));
            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task Clicked_into_to_select_its_text_the_popup_stays_wherever_the_pointer_goes_until_a_click_outside_or_typing() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            const string text = "one two\nthree";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            int three = text.IndexOf("three", StringComparison.Ordinal);
            SelectableTextBlock paragraph = Paragraph(await HoverAsync(window, three, "three is a word in"));
            Rect box = BoundsInWindow(paragraph);
            var from = new Point(box.Left + 1, box.Center.Y);
            var to = new Point(box.Left + 40, box.Center.Y);
            window.MoveMouse(window.PointAt(three) + new Point(0, 5));
            window.MoveMouse(from);

            // A drag that selects its text, out over its edge and back: the popup stays.
            window.Window.MouseDown(from, MouseButton.Left);
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseMove(window.PointAt(0), RawInputModifiers.LeftMouseButton);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(to, MouseButton.Left);
            window.Layout();
            IsShown(paragraph).ShouldBeTrue();
            paragraph.SelectedText.ShouldNotBeEmpty();

            // Clicked into, it stays also over other text, which shows no hover of its own, as in VS Code.
            window.MoveMouse(window.PointAt(0));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            window.Layout();
            IsShown(paragraph).ShouldBeTrue();
            paragraph.SelectedText.ShouldNotBeEmpty();

            // A click outside it ends it.
            window.Click(window.PointAt(0), MouseButton.Left);
            await Wait.UntilAsync(() => Popup(window) is null);
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();

            // So does typing, which goes into the text.
            paragraph = Paragraph(await HoverAsync(window, three, "three is a word in"));
            box = BoundsInWindow(paragraph);
            window.MoveMouse(box.Center);
            window.Click(box.Center, MouseButton.Left);
            window.MoveMouse(window.PointAt(0));
            await Task.Delay(TimeSpan.FromMilliseconds(500));
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
            await using var server = new LanguageServer(fake.Options);
            const string text = "one two three";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            int two = text.IndexOf("two", StringComparison.Ordinal);

            // A click, also on the hovered word; until the pointer moves, it shows no other.
            await HoverAsync(window, two, "two is a word in");
            int asked = fake.LinesStartingWith("hover ").Count;
            window.Click(window.PointAt(two), MouseButton.Left);
            Popup(window).ShouldBeNull();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            window.Layout();
            fake.LinesStartingWith("hover ").Count.ShouldBe(asked);

            // An arrow key, which moves no caret of a hover the mouse opened.
            await HoverAsync(window, two, "two is a word in");
            window.Press(Key.Right);
            Popup(window).ShouldBeNull();

            // Not the first key of a sequence, which may show the hover.
            await HoverAsync(window, two, "two is a word in");
            window.Press(Key.K, EditorWindow.Command);
            Popup(window).ShouldNotBeNull();
        });

    [Fact]
    public Task No_hover_starts_while_the_mouse_drags_a_selection_nor_where_it_rests_after() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options);
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
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            window.Window.MouseUp(window.PointAt(three), MouseButton.Left);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            window.Layout();
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            editor.Selection.IsEmpty.ShouldBeFalse();

            // Once the pointer moves on, resting it starts one again.
            await HoverAsync(window, three, "three is a word in");
        });

    [Fact]
    public Task Ctrl_K_Ctrl_I_shows_the_hover_at_the_caret_and_again_puts_the_keyboard_into_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            // Right after the word, as typing leaves the caret.
            editor.Selection = new TextSelection(3);

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
    public Task The_popup_closes_on_typing_Escape_scrolling_and_focus_loss() =>
        _ui.Run(async () =>
        {
            // Without diagnostics: the echoed whole text would take all that a hover shows.
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options);
            string text = string.Concat(Enumerable.Range(0, 200).Select(i => $"word{i}\n"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });
            await server.StartAsync();

            await HoverAsync(window, 1, "word0 is a word in");
            window.Type("x");
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word in");
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word in");
            editor.ScrollTo(new TextRange(text.Length, 0));
            window.Layout();
            Popup(window).ShouldBeNull();

            editor.ScrollTo(new TextRange(0, 0));
            await HoverAsync(window, 1, "xword0 is a word in");
            button.Focus();
            window.Layout();
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task Escape_ends_a_hover_that_still_waits_for_its_server() =>
        _ui.Run(async () =>
        {
            // Without diagnostics, nothing shows until the server answers, and it never answers for "slow". The clock
            // stands still, so that no hang limit cancels the request instead.
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            window.MoveMouse(window.PointAt(1));
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
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "word");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            await HoverAsync(window, 1, "word is a word in");

            // The process crashes: its information and its diagnostics' messages are outdated.
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");

            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task New_colors_end_the_hover_whose_code_shows_the_old_ones() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            var editor = new CodeEditor { Document = document };
            var scope = new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Light, Child = editor };
            using var window = new EditorWindow(editor, scope);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            await HoverAsync(window, 5, "is a word in");

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
    public Task The_popup_stays_inside_a_window_that_narrows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string text = new string('a', 36) + " word";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 700);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            PopupFrame popup = await HoverAsync(
                window,
                text.IndexOf("word", StringComparison.Ordinal),
                "word is a word in"
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
            var popup = new EditorPopup(editor, new Border { Height = 1_000, Background = Brushes.Gray });

            popup.Show(editor, () => new Rect(10, 100.6, 8, 16));
            window.Layout();

            BoundsInWindow(popup.Root).Bottom.ShouldBeLessThanOrEqualTo(window.Window.ClientSize.Height);
            popup.Hide();
        });

    [Fact]
    public Task An_outdated_hover_request_is_cancelled() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            const string text = "slow fast";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });

            // The server never answers for "slow".
            window.MoveMouse(window.PointAt(1));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];
            (await fake.WaitForAsync("hover ")).ShouldEndWith(" slow");

            await HoverAsync(window, text.IndexOf("fast", StringComparison.Ordinal), "fast is a word in");
            await fake.WaitForAsync($"cancel {id}");
        });

    [Fact]
    public Task A_hover_takes_the_few_most_severe_diagnostics_of_however_many_there_are() =>
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

            IReadOnlyList<Diagnostic> shown = squiggles.At(1, HoverBudget.MaxLines);

            // The errors, which come every fourth, in the server's order; no more than a hover has lines.
            shown
                .Select(d => d.Message)
                .ShouldBe(Enumerable.Range(0, HoverBudget.MaxLines).Select(i => $"{4 * i + 3}"));
        });

    [Fact]
    public Task The_popup_stays_over_a_word_longer_than_the_hovered_word_reaches() =>
        _ui.Run(async () =>
        {
            string text = new string('x', 2_500) + " end";
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
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
            PopupFrame popup = await HoverAsync(window, 0, "long");

            // Past where the hovered word was cut, in the same word.
            window.MoveMouse(window.PointAt(HoverController.MaxWordReach + 1));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(window.PointAt(2_499));
            Popup(window).ShouldBeSameAs(popup);

            window.MoveMouse(window.PointAt(text.IndexOf("end", StringComparison.Ordinal)));
            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task The_pointer_over_a_long_lines_hidden_tail_is_over_no_text() =>
        _ui.Run(async () =>
        {
            // The editor shows the first 3,000 characters of the word, wrapped, and "..." for its other ten million.
            string text = new string('x', 10_000_000) + "\nend";
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document, WordWrap = true }, height: 900);
            document.SetDiagnostics([
                new Diagnostic(document.CurrentSnapshot, new TextRange(0, 10), DiagnosticSeverity.Error, null, "long"),
            ]);
            await HoverAsync(window, 5, "long");

            // Over the "...", where the hidden tail starts.
            window.MoveMouse(window.PointAt(VisibleText.LineLimit));

            await Wait.UntilAsync(() => Popup(window) is null);

            // Straight on to the next line, past the hidden tail.
            await HoverAsync(window, 5, "long");
            window.MoveMouse(window.PointAt(text.Length - 2));

            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task The_hovered_word_reaches_a_bounded_distance_from_the_pointer() =>
        _ui.Run(() =>
        {
            // One word of a million characters on a line, after a short one.
            var buffer = new AvaloniaEdit.Document.TextDocument("ab " + new string('x', 1_000_000) + " cd");
            const int reach = HoverController.MaxWordReach;

            HoverController.WordAt(buffer, 1).ShouldBe(new TextRange(0, 2));
            HoverController.WordAt(buffer, 3).ShouldBe(new TextRange(3, reach));
            // Nor past the shown part of a long line.
            HoverController.WordAt(buffer, 2_500).ShouldBe(TextRange.FromBounds(2_500 - reach, VisibleText.LineLimit));

            // The pointer may move on along a cut word, in the shown part of its line only: neither into its hidden
            // tail nor past it, onto the next line.
            var tail = new AvaloniaEdit.Document.TextDocument(new string('x', 10_000_000) + "\nend");
            TextRange word = HoverController.WordAt(tail, 5);
            HoverController.AlongWord(tail, word).ShouldBe(new TextRange(0, VisibleText.LineLimit));
            HoverController.AlongWord(buffer, new TextRange(0, 2)).ShouldBe(new TextRange(0, 2));
            HoverController.AlongWord(buffer, new TextRange(2, 1)).ShouldBe(new TextRange(2, 1));
        });

    /// <summary>
    /// Moves the pointer away, then rests it on the character at <paramref name="offset"/>, and waits for the popup
    /// with <paramref name="text"/>.
    /// </summary>
    private static Task<PopupFrame> HoverAsync(EditorWindow window, int offset, string text)
    {
        window.MoveMouse(window.PointAt(offset) + new Point(0, 200));
        window.MoveMouse(window.PointAt(offset));
        return WaitForPopupAsync(window, text);
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

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    private static Control Content(PopupFrame popup) => PopupFrames.Content(popup);

    private static IEnumerable<string> Texts(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    private static string TextOf(Control control) =>
        control is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : ((TextBlock)control).Text ?? "";
}
