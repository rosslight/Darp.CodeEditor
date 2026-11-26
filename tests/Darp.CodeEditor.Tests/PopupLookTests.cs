using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Darp.CodeEditor.Completion;
using Darp.CodeEditor.Popups;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>The editor's own popups: their colors, their scroll bar, and that an app's styles don't reach them.</summary>
[Collection(UiFixture.Collection)]
public sealed class PopupLookTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_long_list_scrolls_by_wheel_thumb_and_track_and_the_keys_still_move_its_row_from_the_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, ManyItems());
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 0);
            window.Render();
            CompletionListBox list = List(window).ShouldNotBeNull();
            ScrollBar bar = list.GetVisualDescendants().OfType<PopupScrollBar>().Single();
            Thumb thumb = bar.GetVisualDescendants().OfType<PopupThumb>().Single();

            window.Window.MouseWheel(BoundsInWindow(list).Center, new Vector(0, -1));
            window.Render();
            int wheeled = list.FirstRow;
            wheeled.ShouldBeGreaterThan(0);

            Point grip = BoundsInWindow(thumb).Center;
            window.MoveMouse(grip);
            window.Window.MouseDown(grip, MouseButton.Left);
            window.MoveMouse(grip + new Point(0, 30));
            window.Window.MouseUp(grip + new Point(0, 30), MouseButton.Left);
            window.Render();
            int dragged = list.FirstRow;
            dragged.ShouldBeGreaterThan(wheeled);

            Rect track = BoundsInWindow(bar);
            // Above the frame's bottom edge, which resizes the list.
            var below = new Point(track.Center.X, track.Bottom - 8);
            window.MoveMouse(below);
            window.Click(below, MouseButton.Left);
            window.Render();
            list.FirstRow.ShouldBeGreaterThan(dragged);

            // None of it took the keyboard or ended completion: the keys still move the selected row from the text.
            EditorWindow.PartOf(window.Editor).TextArea.IsFocused.ShouldBeTrue();
            int selected = list.SelectedIndex;
            window.Press(Key.Down);
            list.SelectedIndex.ShouldBe(selected + 1);
            window.Press(Key.PageDown);
            list.SelectedIndex.ShouldBeGreaterThan(selected + 1);
        });

    [Fact]
    public Task The_bar_takes_the_slider_colors_and_a_gutter_of_its_own_beside_the_rows() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, ManyItems());
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 0);
            window.Render();
            CompletionListBox list = List(window).ShouldNotBeNull();
            ScrollBar bar = list.GetVisualDescendants().OfType<PopupScrollBar>().Single();
            Thumb thumb = bar.GetVisualDescendants().OfType<PopupThumb>().Single();
            TextBlock label = list.RowOf(0).ShouldNotBeNull().Label;

            bar.Bounds.Width.ShouldBe(10);
            BoundsInWindow(label).Right.ShouldBeLessThanOrEqualTo(BoundsInWindow(bar).Left);
            // Light+ leaves them out: VS Code's defaults.
            ColorOf(thumb).ShouldBe(Color.Parse("#66646464"));
            Point grip = BoundsInWindow(thumb).Center;
            window.MoveMouse(grip);
            ColorOf(thumb).ShouldBe(Color.Parse("#B3646464"));
            window.Window.MouseDown(grip, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            ColorOf(thumb).ShouldBe(Color.Parse("#99000000"));
            window.Window.MouseUp(grip, MouseButton.Left);
        });

    [Fact]
    public Task The_selected_rows_icon_takes_the_color_themes_color_and_the_others_keep_their_own() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse("""{ "items": [ { "label": "alpha", "kind": 2 }, { "label": "beta", "kind": 7 } ] }""")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, 0);
            CompletionListBox list = List(window).ShouldNotBeNull();
            int White(int index)
            {
                window.Render();
                CompletionIcon icon = list.RowOf(index)
                    .ShouldNotBeNull()
                    .GetVisualDescendants()
                    .OfType<CompletionIcon>()
                    .Single();
                return window.RenderAndCountPixels(Colors.White, BoundsInWindow(icon));
            }

            // Two rows fit: no bar.
            list.GetVisualDescendants().OfType<PopupScrollBar>().Single().IsVisible.ShouldBeFalse();
            // Light+ sets list.activeSelectionIconForeground.
            await Wait.UntilAsync(() => White(0) > 10);
            White(1).ShouldBe(0);

            window.Press(Key.Down);

            White(0).ShouldBe(0);
            White(1).ShouldBeGreaterThan(10);
        });

    [Fact]
    public Task App_styles_for_flyouts_lists_scroll_bars_text_and_panels_reach_neither_the_list_nor_its_details() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """
                    { "items": [ { "label": "Console", "detail": "class System.Console",
                      "documentation": { "kind": "markdown", "value": "Writes `text` to the output." } } ] }
                    """
                )
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "Co");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            AddHostileStyles(window.Window);

            await OpenAsync(window, 2);
            await Wait.UntilAsync(() =>
                PopupFrames
                    .Find(window, "CompletionDetails")
                    ?.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Any(text => text.Inlines?.Text == "Writes text to the output.") == true
            );
            window.Render();

            PopupFrame frame = Flyout(window).ShouldNotBeNull();
            PopupFrame details = PopupFrames.Find(window, "CompletionDetails").ShouldNotBeNull();
            frame.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color.ShouldBe(Color.Parse("#F3F3F3"));
            details.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color.ShouldBe(Color.Parse("#F3F3F3"));
            CompletionListBox.Row row = List(window).ShouldNotBeNull().RowOf(0).ShouldNotBeNull();
            row.Bounds.Height.ShouldBe(EditorWindow.PartOf(window.Editor).TextArea.TextView.DefaultLineHeight, 0.5);
            foreach (TextBlock text in details.GetVisualDescendants().OfType<TextBlock>())
            {
                text.FontWeight.ShouldBe(FontWeight.Normal);
                text.Margin.ShouldBe(default);
                text.MaxLines.ShouldBe(0);
            }
            details.GetVisualDescendants().OfType<StackPanel>().ShouldAllBe(panel => panel.Spacing == 6);
            window.RenderAndCountPixels(Colors.Red, BoundsInWindow(frame)).ShouldBe(0);
            window.RenderAndCountPixels(Colors.Red, BoundsInWindow(details)).ShouldBe(0);
        });

    [Fact]
    public Task Completions_details_and_signature_helps_card_look_as_the_hover_code_on_the_popup_a_line_and_docs() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(
                server,
                fake,
                Parse(
                    """{ "items": [ { "label": "key", "data": { "detail": "\"key\": 1", "documentation": "The key." } } ] }"""
                )
            );
            var signatures = new JsonArray(
                new JsonObject
                {
                    ["label"] = "call(\"first\", 2)",
                    ["documentation"] = "Calls.",
                    ["parameters"] = new JsonArray(new JsonObject { ["label"] = new JsonArray(5, 12) }),
                }
            );
            using CodeDocument command = server.CreateDocument(
                FakeServer.Commands,
                $"signatures {signatures.ToJsonString()}"
            );
            await fake.WaitForAsync("set signatures");
            // JSON colors strings and numbers: the code's tokens have colors of their own.
            using CodeDocument document = server.CreateDocument(CodeLanguage.Json, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await window.TokensKnownAsync();

            await OpenAsync(window, 0);
            PopupFrame? details = null;
            await Wait.UntilAsync(() =>
            {
                window.Render();
                details = PopupFrames.Find(window, "CompletionDetails");
                return details is not null && Hovers.Texts(details).Contains("The key.");
            });
            TextBlock detail = ShouldShowCodeThenDocs(window, details!, "\"key\": 1", "The key.");
            Run[] detailRuns = [.. detail.Inlines.ShouldNotBeNull().OfType<Run>()];
            detailRuns.ShouldContain(run => run.Text == "\"key\"" && ColorOf(run) == s_string);
            window.Press(Key.Escape);

            window.Editor.Selection = new TextSelection(4);
            window.Type("(");
            PopupFrame? card = null;
            await Wait.UntilAsync(() =>
            {
                window.Render();
                card = PopupFrames.Find(window, "SignatureHelp");
                return card is not null && Hovers.Texts(card).Contains("Calls.");
            });
            TextBlock signature = ShouldShowCodeThenDocs(window, card!, "call(\"first\", 2)", "Calls.");
            // The active parameter is bold in editorHoverWidget.highlightForeground, over its token's color; the rest
            // keeps its tokens' colors.
            Run[] runs = [.. signature.Inlines.ShouldNotBeNull().OfType<Run>()];
            Run parameter = runs.Single(run => run.Text == "\"first\"");
            parameter.FontWeight.ShouldBe(FontWeight.Bold);
            ColorOf(parameter).ShouldBe(Color.Parse("#0066BF"));
            Run number = runs.Single(run => run.Text == "2");
            number.FontWeight.ShouldBe(FontWeight.Normal);
            ColorOf(number).ShouldBe(s_number);
        });

    [Fact]
    public Task App_styles_for_tooltips_scroll_bars_and_text_do_not_reach_a_long_popup_of_text() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor();
            using var window = new EditorWindow(editor, width: 500, height: 300);
            AddHostileStyles(window.Window);
            var text = new PopupText
            {
                Text = string.Join(' ', Enumerable.Repeat("word", 900)),
                TextWrapping = TextWrapping.Wrap,
            };
            var frame = new PopupFrame
            {
                Name = "Hover",
                MaxWidth = PopupFrame.MaxTextWidth(editor.Bounds.Width),
                Child = new PopupScrollViewer
                {
                    Content = text,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            };
            PopupColors colors = PopupColors.Default;
            frame.Paint(colors.HoverBackground, colors.HoverBorder, colors.HoverForeground, colors);
            var popup = new EditorPopup(editor, frame);

            popup.Show(editor, () => new Rect(10, 150, 8, 16));
            window.Layout();
            window.Render();

            ScrollBar bar = frame.GetVisualDescendants().OfType<PopupScrollBar>().Single();
            bar.IsEffectivelyVisible.ShouldBeTrue();
            bar.Bounds.Width.ShouldBe(10);
            BoundsInWindow(text).Right.ShouldBeLessThanOrEqualTo(BoundsInWindow(bar).Left);
            text.FontWeight.ShouldBe(FontWeight.Normal);
            text.MaxLines.ShouldBe(0);
            window.RenderAndCountPixels(Colors.Red, BoundsInWindow(frame)).ShouldBe(0);
            popup.Hide();
        });

    /// <summary>Light+'s colors of JSON's strings and numbers out of an object.</summary>
    private static readonly Color s_string = Color.Parse("#A31515");

    private static readonly Color s_number = Color.Parse("#098658");

    /// <summary>
    /// Checks the look that every popup of code and docs shares, the hover's (its own tests check it): on Light+'s hover
    /// background, <paramref name="code"/> in the editor's font with no background of its own, a line in the popup's
    /// border below it, then <paramref name="docs"/> as text in the hover's muted foreground. Returns the code.
    /// </summary>
    private static TextBlock ShouldShowCodeThenDocs(EditorWindow window, PopupFrame frame, string code, string docs)
    {
        Control[] parts = [.. ((Panel)PopupFrames.Content(frame)).Children];
        parts.Length.ShouldBe(3);
        TextBlock codeBlock = parts[0].ShouldBeAssignableTo<TextBlock>().ShouldNotBeNull();
        Hovers.TextOf(codeBlock).ShouldBe(code);
        codeBlock.FontFamily.ShouldBe(window.Editor.FontFamily);
        parts[1].ShouldBeOfType<PopupLine>().Background.ShouldBeSameAs(frame.BorderBrush);
        TextBlock docsBlock = parts[2].ShouldBeAssignableTo<TextBlock>().ShouldNotBeNull();
        Hovers.TextOf(docsBlock).ShouldBe(docs);
        docsBlock.Foreground.ShouldBeAssignableTo<ISolidColorBrush>()!.Color.ShouldBe(Color.Parse("#616161"));
        frame.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color.ShouldBe(Color.Parse("#F3F3F3"));
        // None of the text area's white behind the code, in the pixels.
        window.RenderAndCountPixels(Colors.White, BoundsInWindow(codeBlock)).ShouldBe(0);
        return codeBlock;
    }

    private static Color? ColorOf(Run run) => (run.Foreground as ISolidColorBrush)?.Color;

    private static JsonObject ManyItems() => Items([.. Enumerable.Range(0, 100).Select(i => $"item{i:D3}")]);

    private static Color ColorOf(Thumb thumb) => thumb.Background.ShouldBeAssignableTo<ISolidColorBrush>()!.Color;

    /// <summary>Styles of an app on the stock controls that popups must not take.</summary>
    private static void AddHostileStyles(Window window)
    {
        Add<FlyoutPresenter>(new Setter(TemplatedControl.BackgroundProperty, Brushes.Red));
        Add<ToolTip>(new Setter(TemplatedControl.BackgroundProperty, Brushes.Red));
        Add<ListBox>(new Setter(TemplatedControl.BackgroundProperty, Brushes.Red));
        Add<ListBoxItem>(
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Red),
            new Setter(Layoutable.MinHeightProperty, 60.0)
        );
        Add<ScrollViewer>(
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Red),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(30))
        );
        Add<ScrollBar>(
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Red),
            new Setter(Layoutable.WidthProperty, 40.0)
        );
        Add<Thumb>(new Setter(TemplatedControl.BackgroundProperty, Brushes.Red));
        Add<RepeatButton>(new Setter(TemplatedControl.BackgroundProperty, Brushes.Red));
        Add<Border>(new Setter(Border.BackgroundProperty, Brushes.Red));
        Add<TextBlock>(
            new Setter(TextBlock.FontWeightProperty, FontWeight.Black),
            new Setter(TextBlock.ForegroundProperty, Brushes.Red),
            new Setter(Layoutable.MarginProperty, new Thickness(20)),
            new Setter(TextBlock.MaxLinesProperty, 1)
        );
        Add<StackPanel>(new Setter(StackPanel.SpacingProperty, 40.0));

        void Add<T>(params Setter[] setters)
            where T : StyledElement
        {
            var style = new Style(x => x.OfType<T>());
            foreach (Setter setter in setters)
                style.Setters.Add(setter);
            window.Styles.Add(style);
        }
    }
}
