using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// How a hover is held and left: a click into it holds it, its text can be selected and copied, the hover key puts the
/// keyboard into it, and it ends on Escape, a click outside it and the keys of the text.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class HoverEndTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Clicked_into_the_hover_stays_wherever_the_pointer_goes_and_copies_its_selection_until_a_click_outside() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two\nthree";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await CompletionTests.StartServingAsync(server, document);
            int three = text.IndexOf("three", StringComparison.Ordinal);
            SelectableTextBlock paragraph = Paragraph(await HoverAsync(window, three, "three is a word in", time));
            Rect box = BoundsInWindow(paragraph);
            var from = new Point(box.Left + 1, box.Center.Y);
            var to = new Point(box.Left + 40, box.Center.Y);
            window.MoveMouse(from);

            // A drag that selects its text, out over the editor's text and back: the popup stays.
            window.Window.MouseDown(from, MouseButton.Left);
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseMove(window.PointAt(0), RawInputModifiers.LeftMouseButton);
            Pass(window, time, Delay);
            window.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(to, MouseButton.Left);
            window.Layout();
            string selected = paragraph.SelectedText;
            selected.ShouldNotBeEmpty();
            paragraph.IsKeyboardFocusWithin.ShouldBeTrue();

            // Its own Ctrl+C copies the selection, not the editor's line.
            KeyGesture copy = Application
                .Current.ShouldNotBeNull()
                .PlatformSettings.ShouldNotBeNull()
                .HotkeyConfiguration.Copy[0];
            window.Press(copy.Key, copy.KeyModifiers);
            (await window.Window.Clipboard.ShouldNotBeNull().TryGetTextAsync()).ShouldBe(selected);

            // Held, it stays over other text, which shows no hover of its own.
            window.MoveMouse(window.PointAt(0));
            Pass(window, time, Delay);
            Popup(window).ShouldNotBeNull();
            TopLevel.GetTopLevel(paragraph).ShouldNotBeNull();
            paragraph.SelectedText.ShouldBe(selected);

            // A click outside it ends it, and the text has the keyboard again.
            window.Click(window.PointAt(0), MouseButton.Left);
            Popup(window).ShouldBeNull();
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task A_click_in_the_text_and_a_key_end_the_hover_and_no_other_starts_until_the_pointer_moves() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two three";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            int two = text.IndexOf("two", StringComparison.Ordinal);

            await HoverAsync(window, two, "two is a word in", time);
            int asked = fake.LinesStartingWith("hover ").Count;
            window.Click(window.PointAt(two), MouseButton.Left);
            Popup(window).ShouldBeNull();
            Pass(window, time, Delay);
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").Count.ShouldBe(asked);

            await HoverAsync(window, two, "two is a word in", time);
            window.Press(Key.A, EditorWindow.Command);
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task No_hover_starts_while_the_mouse_drags_a_selection() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two three");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);

            window.Window.MouseDown(window.PointAt(1), MouseButton.Left);
            window.Window.MouseMove(window.PointAt(5), RawInputModifiers.LeftMouseButton);
            Pass(window, time, Delay);
            await CompletionTests.BarrierAsync(server, fake);

            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            window.Window.MouseUp(window.PointAt(5), MouseButton.Left);
        });

    [Fact]
    public Task The_hover_key_shows_the_hover_at_the_caret_at_once_and_again_puts_the_keyboard_into_it() =>
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
            PressHoverKey(window);

            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Texts(popup).ShouldContain($"{text} (echo)");
            popup.BorderBrush.ShouldBe(PopupColors.Default.HoverBorder);
            PressHoverKey(window);
            popup.IsKeyboardFocusWithin.ShouldBeTrue();
            popup.BorderBrush.ShouldBe(PopupColors.Default.FocusBorder);

            // A focused hover keeps its own Escape, which gives the keyboard back.
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task A_hover_shown_by_key_closes_when_the_caret_moves() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            await using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await CompletionTests.StartServingAsync(server, document);
            editor.Selection = new TextSelection(1);
            PressHoverKey(window);
            await WaitForPopupAsync(window, "one is a word in");

            window.Press(Key.Right);

            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task With_the_keyboard_in_it_a_long_hover_scrolls_by_the_arrows_page_keys_Home_and_End() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            // The server's hover shows the hovered line as code, as much of it as a hover shows.
            string line = string.Concat(Enumerable.Repeat("word ", 4_000)).TrimEnd();
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, line);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            PopupFrame popup = await HoverAsync(window, 1, "word is a word in", time);
            PressHoverKey(window);
            window.Layout();
            PopupScrollViewer scroller = popup.GetVisualDescendants().OfType<PopupScrollViewer>().Single();
            double most = scroller.Extent.Height - scroller.Viewport.Height;
            most.ShouldBeGreaterThan(0);

            double Offset(Key key)
            {
                window.Press(key);
                window.Layout();
                return scroller.Offset.Y;
            }

            double line1 = Offset(Key.Down);
            line1.ShouldBeGreaterThan(0);
            Offset(Key.Up).ShouldBe(0);
            Offset(Key.PageDown).ShouldBeGreaterThan(line1);
            Offset(Key.PageUp).ShouldBe(0);
            Offset(Key.End).ShouldBe(most, 1);
            Offset(Key.Home).ShouldBe(0);
            Popup(window).ShouldNotBeNull();
        });

    [Fact]
    public Task The_hover_resizes_by_its_edges_and_narrows_from_the_edge_that_is_dragged() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            // Near the right edge, with a diagnostic as wide as the line: the window's edge pushes the popup left.
            string text = new string('a', 60) + " word";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 600);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            PopupFrame popup = await HoverAsync(window, text.Length - 2, "word is a word in", time);
            Rect bounds = BoundsInWindow(popup);
            bounds.Right.ShouldBe(window.Window.ClientSize.Width, 1);
            var grip = new Point(bounds.Right - 2, bounds.Center.Y);

            window.MoveMouse(grip);
            window.Window.MouseDown(grip, MouseButton.Left);
            window.Window.MouseMove(grip - new Point(20, 0), RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(grip - new Point(20, 0), MouseButton.Left);
            window.Layout();
            window.Render();

            Rect narrowed = BoundsInWindow(popup);
            narrowed.Left.ShouldBe(bounds.Left, 0.5);
            narrowed.Right.ShouldBe(bounds.Right - 20, 0.5);

            // The bottom edge, away from the line above it.
            var bottom = new Point(narrowed.Center.X, narrowed.Bottom - 1);
            window.MoveMouse(bottom);
            window.Window.MouseDown(bottom, MouseButton.Left);
            window.Window.MouseMove(bottom + new Point(0, 30), RawInputModifiers.LeftMouseButton);
            window.Window.MouseUp(bottom + new Point(0, 30), MouseButton.Left);
            window.Layout();
            window.Render();

            BoundsInWindow(popup).Height.ShouldBe(narrowed.Height + 30, 0.5);
        });
}
