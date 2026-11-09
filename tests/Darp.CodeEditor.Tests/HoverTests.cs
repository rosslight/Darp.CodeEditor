using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

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

            ToolTip popup = await WaitForPopupAsync(window, "is a word.");
            Control[] parts = [.. ((Panel)Content(popup)).Children];
            // The diagnostic's message and code, a line, the server's paragraph, and its code block.
            TextOf(parts[0]).ShouldBe($"{text} (echo)");
            parts[1].ShouldBeOfType<Separator>();
            TextOf(parts[2]).ShouldBe("key is a word.");
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

            ToolTip popup = await WaitForPopupAsync(window, "is a word.");
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
                "three is a word."
            );
            window.MoveMouse(window.PointAt(text.IndexOf("three", StringComparison.Ordinal) + 3));
            Popup(window).ShouldBeSameAs(popup);
            window.MoveMouse(BoundsInWindow(popup).Center);
            Popup(window).ShouldBeSameAs(popup);

            // Out of the popup to the side of the editor.
            window.MoveMouse(outside.TranslatePoint(new Point(50, 50), window.Window)!.Value);
            Popup(window).ShouldBeNull();

            // Over another word, though under the same diagnostic.
            await HoverAsync(window, 0, "one is a word.");
            window.MoveMouse(window.PointAt(text.IndexOf("two", StringComparison.Ordinal)));
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task The_popup_closes_on_typing_Escape_scrolling_and_focus_loss() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string text = string.Concat(Enumerable.Range(0, 200).Select(i => $"word{i}\n"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            await HoverAsync(window, 1, "word0 is a word.");
            window.Type("x");
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word.");
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();

            await HoverAsync(window, 1, "xword0 is a word.");
            editor.ScrollTo(new TextRange(text.Length, 0));
            window.Layout();
            Popup(window).ShouldBeNull();

            editor.ScrollTo(new TextRange(0, 0));
            await HoverAsync(window, 1, "xword0 is a word.");
            button.Focus();
            window.Layout();
            Popup(window).ShouldBeNull();
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

            await HoverAsync(window, text.IndexOf("fast", StringComparison.Ordinal), "fast is a word.");
            await fake.WaitForAsync($"cancel {id}");
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
