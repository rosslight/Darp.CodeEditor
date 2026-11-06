using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace Darp.CodeEditor.Tests;

/// <summary>The editor's contracts: its document, selection, scrolling, view state, clipboard, keys and lifetime.</summary>
[Collection(UiFixture.Collection)]
public sealed class CodeEditorTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public async Task An_editor_created_on_another_thread_fails_at_once()
    {
        Exception? failure = null;
        // A thread of its own: creating an Avalonia object gives the thread a dispatcher.
        var thread = new Thread(() =>
        {
            try
            {
                _ = new CodeEditor();
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.Start();
        await Task.Run(thread.Join, TestContext.Current.CancellationToken);

        failure.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("main UI thread", Case.Insensitive);
    }

    [Fact]
    public Task A_backward_selection_keeps_its_direction() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor("hello world"));
            CodeEditor editor = window.Editor;

            editor.Selection = new TextSelection(8, 2);
            editor.Selection.ShouldBe(new TextSelection(8, 2));
            editor.CaretPosition.ShouldBe(new TextPosition(1, 3));

            // Selecting with the keyboard: the anchor stays where the caret was.
            editor.Selection = new TextSelection(5);
            window.Press(Key.Left, KeyModifiers.Shift);
            window.Press(Key.Left, KeyModifiers.Shift);
            editor.Selection.ShouldBe(new TextSelection(5, 3));
        });

    [Fact]
    public Task Selection_is_clamped_to_the_text() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor("hello"));

            window.Editor.Selection = new TextSelection(-3, 99);

            window.Editor.Selection.ShouldBe(new TextSelection(0, 5));
            window.Editor.CaretPosition.ShouldBe(new TextPosition(1, 6));
        });

    [Fact]
    public Task Selection_and_CaretPosition_follow_the_caret_with_change_notifications() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor("first\nsecond"));
            CodeEditor editor = window.Editor;
            editor.Selection = new TextSelection(6);
            var changed = new List<string>();
            editor.PropertyChanged += (_, e) => changed.Add(e.Property.Name);

            window.Type("x");

            editor.Selection.ShouldBe(new TextSelection(7));
            editor.CaretPosition.ShouldBe(new TextPosition(2, 2));
            changed.ShouldContain(nameof(CodeEditor.Selection));
            changed.ShouldContain(nameof(CodeEditor.CaretPosition));
        });

    [Fact]
    public Task Setting_Document_shows_the_other_document_from_its_start() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor(Texts.Json(200)));
            CodeEditor editor = window.Editor;
            window.Layout();
            editor.Selection = new TextSelection(3000);
            editor.ScrollTo(new TextRange(3000, 0));
            CodeDocument other = CodeDocument.Create(CodeLanguage.Json, Texts.Json(200));

            editor.Document = other;
            window.Layout();

            editor.Selection.ShouldBe(new TextSelection(0));
            editor.CaretPosition.ShouldBe(new TextPosition(1, 1));
            TopLine(window).ShouldBe(1);
            window.TextView.Document.ShouldBeSameAs(other.Buffer);
        });

    [Fact]
    public Task Without_a_document_the_editor_is_empty_and_read_only() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(new CodeEditor());
            window.Layout();

            window.Type("text");

            window.TextView.Document.ShouldBeNull();
            window.Editor.Selection.ShouldBe(new TextSelection(0));
            Should.Throw<InvalidOperationException>(() => window.Editor.SaveViewState());
        });

    [Fact]
    public Task ScrollTo_leaves_a_visible_range_where_it_is() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor(Texts.Json(200)));
            window.Layout();

            window.Editor.ScrollTo(LineRange(window.Editor, 5));
            window.Layout();

            TopLine(window).ShouldBe(1);
        });

    [Fact]
    public Task ScrollTo_centers_a_range_out_of_view() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor(Texts.Json(200)));
            window.Layout();

            window.Editor.ScrollTo(LineRange(window.Editor, 100));
            window.Layout();

            CenterLine(window).ShouldBeInRange(99, 101);
        });

    [Fact]
    public Task ScrollTo_shows_a_range_taller_than_the_view_from_its_start() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor(Texts.Json(200)));
            window.Layout();
            TextSnapshot text = DocumentOf(window.Editor).CurrentSnapshot;

            window.Editor.ScrollTo(
                TextRange.FromBounds(text.GetOffset(new TextPosition(50, 1)), text.GetOffset(new TextPosition(150, 1)))
            );
            window.Layout();

            TopLine(window).ShouldBe(50);
        });

    [Fact]
    public Task ScrollTo_centers_a_range_whose_end_is_out_of_view_to_the_right() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(
                new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, new string('x', 500)) },
                width: 400
            );
            window.Layout();
            TextView textView = window.TextView;
            // From the middle of the view to past its right edge: narrower than the view.
            int columns = (int)(textView.Bounds.Width / textView.WideSpaceWidth);
            var range = TextRange.FromBounds(columns / 2, columns + 5);
            X(textView, range.Start).ShouldBeLessThan(textView.Bounds.Width);
            X(textView, range.End).ShouldBeGreaterThan(textView.Bounds.Width);

            window.Editor.ScrollTo(range);
            window.Layout();

            ShouldShowAcross(textView, range.Start);
            ShouldShowAcross(textView, range.End);
        });

    [Fact]
    public Task ScrollTo_shows_from_its_start_a_range_over_lines_wider_than_the_view() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, new string('x', 300) + "\nabc");
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 400);
            window.Layout();

            // From column 101 of the long line to the start of the next one: its ends alone would scroll left.
            window.Editor.ScrollTo(TextRange.FromBounds(100, 302));
            window.Layout();

            ShouldShowAcross(window.TextView, 100);
        });

    [Fact]
    public Task ScrollTo_brings_a_range_inside_a_wrapped_line_into_view() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, new string('x', 2_000) + "\nend");
            using var window = new EditorWindow(new CodeEditor { Document = document, WordWrap = true }, width: 400);
            window.Layout();
            TextView textView = window.TextView;

            window.Editor.ScrollTo(new TextRange(1_900, 5));
            window.Layout();

            textView.ScrollOffset.X.ShouldBe(0);
            Y(textView, 1_900)
                .ShouldBeInRange(textView.ScrollOffset.Y, textView.ScrollOffset.Y + textView.Bounds.Height);
        });

    [Fact]
    public Task ScrollTo_finds_a_range_after_tabs_where_they_draw_it() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, new string('\t', 30) + "value\n");
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 400);
            window.Layout();
            // Thirty tabs of four columns: as characters, "value" would be inside the view.
            X(window.TextView, 30).ShouldBeGreaterThan(window.TextView.Bounds.Width);

            window.Editor.ScrollTo(new TextRange(30, 5));
            window.Layout();

            ShouldShowAcross(window.TextView, 30);
            ShouldShowAcross(window.TextView, 35);
        });

    [Fact]
    public Task ScrollTo_before_the_first_layout_scrolls_once_laid_out() =>
        _ui.Run(() =>
        {
            CodeEditor editor = Editor(Texts.Json(200));
            editor.ScrollTo(LineRange(editor, 100));

            using var window = new EditorWindow(editor);
            window.Layout();

            CenterLine(window).ShouldBeInRange(99, 101);
        });

    [Fact]
    public Task A_view_state_follows_the_edits_made_since() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(300));
            using var window = new EditorWindow(new CodeEditor { Document = document });
            CodeEditor editor = window.Editor;
            window.Layout();
            editor.ScrollTo(LineRange(editor, 150));
            window.Layout();
            int top = TopLine(window);
            editor.Selection = new TextSelection(
                document.CurrentSnapshot.GetOffset(new TextPosition(150, 9)),
                document.CurrentSnapshot.GetOffset(new TextPosition(150, 3))
            );
            CodeEditorViewState state = editor.SaveViewState();

            editor.Document = CodeDocument.Create(CodeLanguage.Json, "{}");
            document.Replace(new TextRange(2, 0), "  \"added\": 1,\n  \"added\": 2,\n");
            editor.Document = document;
            editor.RestoreViewState(state);
            window.Layout();

            TopLine(window).ShouldBe(top + 2);
            TextSnapshot text = document.CurrentSnapshot;
            editor.Selection.ShouldBe(
                new TextSelection(text.GetOffset(new TextPosition(152, 9)), text.GetOffset(new TextPosition(152, 3)))
            );
            editor.CaretPosition.ShouldBe(new TextPosition(152, 3));
        });

    [Fact]
    public Task A_view_state_restored_before_the_first_layout_scrolls_once_laid_out() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(300));
            CodeEditorViewState state;
            using (var first = new EditorWindow(new CodeEditor { Document = document }))
            {
                first.Layout();
                first.Editor.ScrollTo(LineRange(first.Editor, 200));
                first.Layout();
                state = first.Editor.SaveViewState();
            }
            var editor = new CodeEditor { Document = document };

            editor.RestoreViewState(state);
            using var window = new EditorWindow(editor);
            window.Layout();

            CenterLine(window).ShouldBeInRange(199, 201);
        });

    [Fact]
    public Task A_view_state_of_another_document_is_rejected() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(Editor("text"));
            CodeEditorViewState state = window.Editor.SaveViewState();
            window.Editor.Document = CodeDocument.Create(CodeLanguage.PlainText, "other");

            Should.Throw<ArgumentException>(() => window.Editor.RestoreViewState(state));
        });

    [Fact]
    public Task A_read_only_editor_ignores_typing_cut_and_paste() =>
        _ui.Run(async () =>
        {
            using var window = new EditorWindow(Editor("text"));
            CodeEditor editor = window.Editor;
            editor.IsReadOnly = true;
            IClipboard clipboard = Clipboard(window);
            await clipboard.SetTextAsync("pasted");
            editor.Selection = new TextSelection(0, 4);

            window.Type("x");
            editor.Cut();
            editor.Paste();
            window.Layout();

            DocumentOf(editor).Text.ShouldBe("text");
            (await clipboard.TryGetTextAsync()).ShouldBe("pasted");
        });

    [Fact]
    public Task Cut_copy_paste_and_select_all_go_through_the_apps_clipboard() =>
        _ui.Run(async () =>
        {
            using var window = new EditorWindow(Editor("one two"));
            CodeEditor editor = window.Editor;
            IClipboard clipboard = Clipboard(window);

            editor.Selection = new TextSelection(4, 7);
            editor.Copy();
            (await clipboard.TryGetTextAsync()).ShouldBe("two");

            editor.Selection = new TextSelection(0, 4);
            editor.Cut();
            DocumentOf(editor).Text.ShouldBe("two");
            (await clipboard.TryGetTextAsync()).ShouldBe("one ");

            editor.Selection = new TextSelection(3);
            editor.Paste();
            await Wait.UntilAsync(() => DocumentOf(editor).Text == "twoone ");

            editor.SelectAll();
            editor.Selection.ShouldBe(new TextSelection(0, 7));
        });

    [Fact]
    public Task The_context_menu_offers_cut_copy_paste_and_select_all() =>
        _ui.Run(async () =>
        {
            using var window = new EditorWindow(Editor("one two"));
            CodeEditor editor = window.Editor;
            ContextMenu menu = editor.ContextMenu.ShouldBeOfType<ContextMenu>();
            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            items.Select(item => item.Header as string).ShouldBe(["Cut", "Copy", "Paste", "Select All"]);

            editor.Selection = new TextSelection(4, 7);
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            (await Clipboard(window).TryGetTextAsync()).ShouldBe("two");
            items[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            editor.Selection.ShouldBe(new TextSelection(0, 7));

            editor.IsReadOnly = true;
            window.Click(new Point(200, 10), MouseButton.Right);
            menu.IsOpen.ShouldBeTrue();
            items.Select(item => item.IsEnabled).ShouldBe([false, true, false, true]);
            menu.Close();
        });

    [Fact]
    public Task Keys_the_editor_does_not_handle_reach_the_apps_key_bindings() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, "text");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            var pressed = new List<string>();
            foreach (string gesture in (string[])["Ctrl+S", "F5"])
            {
                window.Window.KeyBindings.Add(
                    new KeyBinding
                    {
                        Gesture = KeyGesture.Parse(gesture),
                        Command = new Command(() => pressed.Add(gesture)),
                    }
                );
            }

            // Select All is the editor's own: Ctrl+A, or Cmd+A on macOS, as AvaloniaEdit decides by the operating system.
            // The headless platform reports Ctrl on every system, so its hotkey settings can't tell.
            KeyModifiers command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

            window.Press(Key.S, KeyModifiers.Control);
            window.Press(Key.F5);
            window.Press(Key.A, command);
            window.Type("x");

            pressed.ShouldBe(["Ctrl+S", "F5"]);
            // Select All selected the text that typing replaced.
            document.Text.ShouldBe("x");
        });

    [Fact]
    public Task An_editor_that_shows_up_leaves_the_keyboard_where_it_is() =>
        _ui.Run(() =>
        {
            var box = new TextBox();
            var panel = new StackPanel { Children = { box } };
            using var window = new EditorWindow(new CodeEditor(), panel);
            box.Focus();

            panel.Children.Add(new CodeEditor { Document = CodeDocument.Create(CodeLanguage.Json, "{}") });
            window.Render();

            box.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task Disposing_a_document_on_any_thread_empties_the_editors_that_show_it() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, "{}");
            var first = new CodeEditor { Document = document };
            var second = new CodeEditor { Document = document };
            using var window = new EditorWindow(first, new StackPanel { Children = { first, second } });
            window.Layout();

            await Task.Run(document.Dispose);
            await Wait.UntilAsync(() => EditorWindow.TextViewOf(first).Document is null);
            window.Type("x");

            EditorWindow.TextViewOf(second).Document.ShouldBeNull();
            first.Document.ShouldBeSameAs(document);
            document.Text.ShouldBe("{}");
        });

    [Fact]
    public Task A_document_disposed_while_its_editor_starts_showing_it_is_not_shown() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, "{}");
            using var window = new EditorWindow(new CodeEditor());
            TextEditor part = EditorWindow.PartOf(window.Editor);
            // Disposed as soon as the part has the buffer, in the middle of showing it.
            part.DocumentChanged += (_, _) => document.Dispose();

            window.Editor.Document = document;
            window.Layout();
            window.Type("x");

            part.Document.ShouldBeNull();
            part.IsReadOnly.ShouldBeTrue();
            document.Text.ShouldBe("{}");
        });

    [Fact]
    public Task Two_editors_of_one_document_have_their_own_carets() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "hello world");
            var first = new CodeEditor { Document = document };
            var second = new CodeEditor { Document = document };
            using var window = new EditorWindow(first, new StackPanel { Children = { first, second } });
            first.Selection = new TextSelection(0);
            second.Selection = new TextSelection(6, 11);

            window.Type("> ");

            document.Text.ShouldBe("> hello world");
            first.Selection.ShouldBe(new TextSelection(2));
            second.Selection.ShouldBe(new TextSelection(8, 13));
            EditorWindow.TextViewOf(second).Document.Text.ShouldBe("> hello world");
        });

    [Fact]
    public Task An_editor_out_of_the_visual_tree_is_collected_while_its_document_lives() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(500));
            using var window = new EditorWindow(new CodeEditor(), new ContentControl());

            WeakReference editor = ShowAndRemove(window, document, edit: false);

            await WaitUntilCollected(editor);
            document.Text.ShouldBe(Texts.Json(500));
        });

    [Fact]
    public Task An_editor_that_showed_edits_is_collected_while_its_document_lives() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(500));
            using var window = new EditorWindow(new CodeEditor(), new ContentControl());

            WeakReference editor = ShowAndRemove(window, document, edit: true);

            await WaitUntilCollected(editor);
        });

    [Fact]
    public Task An_editor_that_comes_back_to_the_visual_tree_is_where_it_was() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.Json, Texts.Json(300));
            var editor = new CodeEditor { Document = document };
            var host = new ContentControl { Content = editor };
            using var window = new EditorWindow(editor, host);
            window.Layout();
            editor.ScrollTo(LineRange(editor, 150));
            window.Layout();
            int top = TopLine(window);
            editor.Selection = new TextSelection(document.CurrentSnapshot.GetOffset(new TextPosition(150, 3)));

            host.Content = null;
            window.Layout();
            document.Replace(new TextRange(2, 0), "  \"added\": 1,\n");
            host.Content = editor;
            window.Layout();

            editor.Document.ShouldBeSameAs(document);
            TopLine(window).ShouldBe(top + 1);
            editor.CaretPosition.ShouldBe(new TextPosition(151, 3));
        });

    [Fact]
    public Task A_selection_set_out_of_the_visual_tree_after_edits_is_the_one_shown_on_return() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "one two three");
            var editor = new CodeEditor { Document = document };
            var host = new ContentControl { Content = editor };
            using var window = new EditorWindow(editor, host);
            editor.Selection = new TextSelection(4, 7);
            host.Content = null;
            window.Layout();

            document.Replace(new TextRange(0, 0), "zero ");
            // "three", in the text as it is now: the edit before must not move it again.
            editor.Selection = new TextSelection(13, 18);
            host.Content = editor;
            window.Layout();

            editor.Selection.ShouldBe(new TextSelection(13, 18));
            EditorWindow.PartOf(editor).TextArea.Caret.Offset.ShouldBe(18);
        });

    [Fact]
    public Task A_new_template_shows_the_document_where_the_editor_was() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "one two three");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            CodeEditor editor = window.Editor;
            editor.Selection = new TextSelection(4, 7);
            TextEditor before = EditorWindow.PartOf(editor);

            editor.Template = new FuncControlTemplate<CodeEditor>(
                (_, scope) => new TextEditor { Name = "PART_TextEditor" }.RegisterInNameScope(scope)
            );
            window.Layout();
            editor.Focus();
            window.Type("2");

            TextEditor after = EditorWindow.PartOf(editor);
            after.ShouldNotBeSameAs(before);
            before.Document.ShouldBeNull();
            after.Document.ShouldBeSameAs(document.Buffer);
            document.Text.ShouldBe("one 2 three");
            editor.Selection.ShouldBe(new TextSelection(5));
        });

    /// <summary>
    /// Shows an editor of <paramref name="document"/> with the focus, selects, scrolls, edits if asked to, renders it,
    /// and takes it out of the window again.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ShowAndRemove(EditorWindow window, CodeDocument document, bool edit)
    {
        var editor = new CodeEditor { Document = document };
        var host = window.Window.Content as ContentControl ?? throw new InvalidOperationException("No host.");
        host.Content = editor;
        window.Render();
        editor.Focus();
        editor.Selection = new TextSelection(10, 20);
        editor.ScrollTo(LineRange(editor, 300));
        if (edit)
            window.Type("x");
        window.Render();
        host.Content = null;
        window.Render();
        return new WeakReference(editor);
    }

    /// <summary>Waits until <paramref name="reference"/> is collected: background work that was under way ends soon.</summary>
    private static Task WaitUntilCollected(WeakReference reference) =>
        Wait.UntilAsync(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return !reference.IsAlive;
        });

    private static CodeEditor Editor(string json) => new() { Document = CodeDocument.Create(CodeLanguage.Json, json) };

    /// <summary>The text that <paramref name="editor"/> shows.</summary>
    private static CodeDocument DocumentOf(CodeEditor editor) =>
        editor.Document ?? throw new InvalidOperationException("The editor has no document.");

    private static TextRange LineRange(CodeEditor editor, int line)
    {
        TextSnapshot text = DocumentOf(editor).CurrentSnapshot;
        int start = text.GetOffset(new TextPosition(line, 1));
        return TextRange.FromBounds(start, text.GetOffset(new TextPosition(line, int.MaxValue)));
    }

    /// <summary>
    /// The line at the top of the view. Scroll offsets are rounded to pixels, so the line above may show by a fraction
    /// of a pixel.
    /// </summary>
    private static int TopLine(EditorWindow window) =>
        window.TextView.GetDocumentLineByVisualTop(window.TextView.ScrollOffset.Y + 1).LineNumber;

    /// <summary>The line in the middle of the view.</summary>
    private static int CenterLine(EditorWindow window)
    {
        TextView textView = window.TextView;
        return textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y + textView.Bounds.Height / 2).LineNumber;
    }

    /// <summary>Where <paramref name="textView"/> draws <paramref name="offset"/>, across its document.</summary>
    private static double X(TextView textView, int offset) =>
        textView
            .GetVisualPosition(new TextViewPosition(textView.Document.GetLocation(offset)), VisualYPosition.LineTop)
            .X;

    /// <summary>Where <paramref name="textView"/> draws the top of <paramref name="offset"/>'s row, down its document.</summary>
    private static double Y(TextView textView, int offset) =>
        textView
            .GetVisualPosition(new TextViewPosition(textView.Document.GetLocation(offset)), VisualYPosition.LineTop)
            .Y;

    private static void ShouldShowAcross(TextView textView, int offset) =>
        X(textView, offset).ShouldBeInRange(textView.ScrollOffset.X, textView.ScrollOffset.X + textView.Bounds.Width);

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    private sealed class Command(Action execute) : System.Windows.Input.ICommand
    {
        private readonly Action _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute();
    }
}
