using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Diagnostics;
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

            ToolTip popup = await WaitForPopupAsync(window, "is a word in");
            Control[] parts = [.. ((Panel)Content(popup)).Children];
            // The diagnostic's message and code, a line, the server's paragraph, and its code block.
            TextOf(parts[0]).ShouldBe($"{text} (echo)");
            parts[1].ShouldBeOfType<Separator>();
            TextOf(parts[2]).ShouldBe("key is a word in json.");
            ((TextBlock)parts[2]).Inlines.ShouldNotBeNull().OfType<Run>().First().FontWeight.ShouldBe(FontWeight.Bold);
            var code = (TextBlock)parts[3].GetVisualDescendants().Prepend(parts[3]).OfType<Border>().First().Child!;
            TextOf(code).ShouldBe("  \"key\": 1");
            code.FontFamily.ShouldBe(editor.FontFamily);
            ((Border)parts[3])
                .Background.ShouldBeOfType<Avalonia.Media.Immutable.ImmutableSolidColorBrush>()
                .Color.ShouldBe(Colors.White);
            // A code block starts from the start of the grammar: the key is a string out of an object.
            Run[] runs = [.. code.Inlines.ShouldNotBeNull().OfType<Run>()];
            runs.ShouldContain(run => run.Text == "\"key\"" && (run.Foreground as ISolidColorBrush)!.Color == s_string);
            runs.ShouldContain(run => run.Text == "1" && (run.Foreground as ISolidColorBrush)!.Color == s_number);
            Rect bounds = BoundsInWindow(popup);
            new Rect(window.Window.ClientSize).Contains(bounds).ShouldBeTrue(bounds.ToString());
        });

    [Fact]
    public Task Inline_code_shows_in_the_editors_font_colors_and_background() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{\n  \"key\": 1\n}");
            // A dark text area in a light app: code in the light tooltip keeps the text area's look.
            var editor = new CodeEditor { Document = document, ColorTheme = CodeEditorColorTheme.DarkPlus };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            ToolTip popup = await HoverAsync(window, 5, "key is a word in");

            var paragraph = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(t => TextOf(t) == "key is a word in json.");
            Run code = paragraph.Inlines.ShouldNotBeNull().OfType<Run>().Single(run => run.Text == "json");
            code.FontFamily.ShouldBe(editor.FontFamily);
            code.Foreground.ShouldBeAssignableTo<ISolidColorBrush>()
                .ShouldNotBeNull()
                .Color.ShouldBe(Color.Parse("#D4D4D4"));
            code.Background.ShouldBeAssignableTo<ISolidColorBrush>()
                .ShouldNotBeNull()
                .Color.ShouldBe(Color.Parse("#1E1E1E"));
            // At the paragraph's size, so that it fits its line.
            code.IsSet(TextElement.FontSizeProperty).ShouldBeFalse();
            window.RenderAndCountPixels(Color.Parse("#1E1E1E"), BoundsInWindow(paragraph)).ShouldBeGreaterThan(20);
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

            ToolTip popup = await HoverAsync(window, 1, "some is a word in");

            // In Light+'s black text, on its white background.
            TextBlock code = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(text => text.GetVisualParent() is Border);
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

            ToolTip popup = await WaitForPopupAsync(window, "is a word in");
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
            ToolTip popup = await HoverAsync(
                window,
                text.IndexOf("three", StringComparison.Ordinal),
                "three is a word in"
            );
            window.MoveMouse(window.PointAt(text.IndexOf("three", StringComparison.Ordinal) + 3));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            Popup(window).ShouldBeSameAs(popup);

            // Out of the popup to the side of the editor.
            window.MoveMouse(outside.TranslatePoint(new Point(50, 50), window.Window)!.Value);
            Popup(window).ShouldBeNull();

            // Over another word, though under the same diagnostic.
            await HoverAsync(window, 0, "one is a word in");
            window.MoveMouse(window.PointAt(text.IndexOf("two", StringComparison.Ordinal)));
            Popup(window).ShouldBeNull();
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
            ToolTip popup = await HoverAsync(
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
            ToolTip popup = await HoverAsync(window, 0, "long");

            // Past where the hovered word was cut, in the same word.
            window.MoveMouse(window.PointAt(HoverController.MaxWordReach + 1));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(window.PointAt(2_499));
            Popup(window).ShouldBeSameAs(popup);

            window.MoveMouse(window.PointAt(text.IndexOf("end", StringComparison.Ordinal)));
            Popup(window).ShouldBeNull();
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
            HoverController.WordAt(buffer, 500_000).ShouldBe(TextRange.FromBounds(500_000 - reach, 500_000 + reach));
        });

    /// <summary>
    /// Moves the pointer away, then rests it on the character at <paramref name="offset"/>, and waits for the popup
    /// with <paramref name="text"/>.
    /// </summary>
    private static Task<ToolTip> HoverAsync(EditorWindow window, int offset, string text)
    {
        window.MoveMouse(window.PointAt(offset) + new Point(0, 200));
        window.MoveMouse(window.PointAt(offset));
        return WaitForPopupAsync(window, text);
    }

    private static async Task<ToolTip> WaitForPopupAsync(EditorWindow window, string text)
    {
        ToolTip? popup = null;
        await Wait.UntilAsync(() =>
        {
            window.Render();
            popup = Popup(window);
            return popup is not null && Texts(popup).Any(t => t.Contains(text, StringComparison.Ordinal));
        });
        return popup ?? throw new InvalidOperationException();
    }

    /// <summary>The hover's popup in the window's overlay layer; null while it is closed.</summary>
    private static ToolTip? Popup(EditorWindow window) =>
        OverlayLayer
            .GetOverlayLayer(window.Window)
            ?.Children.OfType<ThemeVariantScope>()
            .Select(scope => scope.Child)
            .OfType<ToolTip>()
            .FirstOrDefault();

    private static Rect BoundsInWindow(Visual visual) =>
        visual.GetTransformedBounds() is { } bounds
            ? bounds.Bounds.TransformToAABB(bounds.Transform)
            : throw new InvalidOperationException("Not shown.");

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    private static Control Content(ToolTip popup) => (Control)((ScrollViewer)popup.Content!).Content!;

    private static IEnumerable<string> Texts(ToolTip popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().Select(TextOf);

    private static string TextOf(Control control) =>
        control is TextBlock { Inlines: { } inlines } ? inlines.Text ?? "" : ((TextBlock)control).Text ?? "";
}
