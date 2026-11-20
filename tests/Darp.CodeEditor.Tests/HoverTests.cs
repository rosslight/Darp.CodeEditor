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
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Hover: the diagnostics above the server's Markdown, in the editor's own popup in its color theme's hover colors,
/// inside the window, in the editor's theme variant, as large as VS Code's limits or a resize, and all of its content
/// built a part a frame.
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

            Control[] parts = Blocks(popup);
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

            HoverContent content = Sections(popup).Single();
            while (!content.IsComplete)
                content.AddNextPart();
            window.Layout();
            string.Concat(CodeTexts(popup, window.Editor.FontFamily)).ShouldBe(line);
            PopupScroller scroller = popup.GetVisualDescendants().OfType<PopupScroller>().Single();
            Content(popup).Bounds.Height.ShouldBeGreaterThan(scroller.Bounds.Height);
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
}
