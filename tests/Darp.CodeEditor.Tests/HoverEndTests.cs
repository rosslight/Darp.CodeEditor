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
/// When the hover stays and when it ends: over its word and itself, held once clicked into or given the keyboard by
/// Ctrl+K Ctrl+I, and ended by keys, clicks, typing, scrolling, Escape and focus loss.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class HoverEndTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

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
    public Task Text_typed_with_the_keyboard_in_the_hover_goes_into_no_document_that_the_text_shows_as_it_takes_it_back() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            using CodeDocument other = server.CreateDocument(CodeLanguage.PlainText, "other");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            await HoverAsync(window, 4, "two is a word in", time);
            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);
            Popup(window).ShouldNotBeNull().IsKeyboardFocusWithin.ShouldBeTrue();
            // An app that shows another document as the text gets the keyboard.
            EditorWindow.PartOf(editor).TextArea.GotFocus += (_, _) => editor.Document = other;

            window.Type("x");

            Popup(window).ShouldBeNull();
            document.Text.ShouldBe("one two");
            other.Text.ShouldBe("other");
        });

    [Fact]
    public Task A_held_hover_whose_line_leaves_the_window_ends_and_gives_the_keyboard_back() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();

            // Held by a click beside its text, which leaves the keyboard in the text; then out of the window and back.
            Rect bounds = BoundsInWindow(await HoverAsync(window, 0, "one is a word in", time));
            window.Click(new Point(bounds.Left + 4, bounds.Center.Y), MouseButton.Left);
            MoveOutAndBack();

            // Nothing holds it: resting on other text shows its hover.
            Popup(window).ShouldBeNull();
            await HoverAsync(window, 4, "two is a word in", time);

            // Held with the keyboard in it, which goes back to the text before the popup leaves.
            window.Press(Key.K, EditorWindow.Command);
            window.Press(Key.I, EditorWindow.Command);
            Popup(window).ShouldNotBeNull().IsKeyboardFocusWithin.ShouldBeTrue();
            MoveOutAndBack();

            Popup(window).ShouldBeNull();
            EditorWindow.PartOf(editor).TextArea.IsFocused.ShouldBeTrue();

            void MoveOutAndBack()
            {
                editor.Margin = new Thickness(0, 2_000, 0, 0);
                window.Layout();
                editor.Margin = default;
                window.Layout();
            }
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
}
