using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// The editor's keys as an app meets them: ordinary <see cref="KeyBinding"/>s that the app can beat and remove, which go
/// on to the app where their command does not apply, and <see cref="CodeEditor.RunCommand"/> from its buttons and menus.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class KeyBindingTests(UiFixture ui)
{
    private static readonly KeyModifiers s_command = EditorWindow.Command;

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task An_apps_binding_from_its_XAML_runs_instead_of_the_default() =>
        _ui.Run(() =>
        {
            var app = new CountingCommand();
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            // As XAML adds it: before the editor is attached.
            editor.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.A, s_command), Command = app });
            using var window = new EditorWindow(editor);

            window.Press(Key.A, s_command);

            app.Runs.ShouldBe(1);
            editor.Selection.ShouldBe(new TextSelection(0));
        });

    [Fact]
    public Task An_apps_binding_added_later_goes_first() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|text", CodeLanguage.PlainText);
            var app = new CountingCommand();

            window.Editor.KeyBindings.Insert(
                0,
                new KeyBinding { Gesture = new KeyGesture(Key.A, s_command), Command = app }
            );
            window.Press(Key.A, s_command);

            app.Runs.ShouldBe(1);
            window.Editor.Selection.ShouldBe(new TextSelection(0));
        });

    /// <summary>Also for the keys that AvaloniaEdit's input handlers had: its shortcuts must not take them back.</summary>
    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.C)]
    [InlineData(Key.V)]
    [InlineData(Key.X)]
    [InlineData(Key.Z)]
    [InlineData(Key.I)]
    [InlineData(Key.F)]
    public Task A_removed_default_frees_its_key_for_the_app(Key key) =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«ab»c", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            document.Replace(new TextRange(3, 0), "d");
            await Clipboard(window).SetTextAsync("pasted");
            var gesture = new KeyGesture(key, s_command);
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = app });
            TextSelection selection = window.Editor.Selection;

            window.Editor.KeyBindings.RemoveAll(binding => binding.Gesture == gesture);
            window.Press(key, s_command);
            await EditorWindow.RunPostedJobsAsync();

            app.Runs.ShouldBe(1);
            document.Text.ShouldBe("abcd");
            window.Editor.Selection.ShouldBe(selection);
            (await Clipboard(window).TryGetTextAsync()).ShouldBe("pasted");
        });

    [Fact]
    public Task A_removed_default_stays_removed_when_the_editor_comes_back() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            using var window = new EditorWindow(editor);
            var gesture = new KeyGesture(Key.A, s_command);
            editor.KeyBindings.RemoveAll(binding => binding.Gesture == gesture);

            window.Window.Content = null;
            window.Layout();
            window.Window.Content = editor;
            window.Layout();

            editor.KeyBindings.ShouldNotContain(binding => binding.Gesture == gesture);
        });

    [Fact]
    public Task The_comment_key_goes_on_in_a_language_without_comments() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("text|", CodeLanguage.PlainText);
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(
                new KeyBinding { Gesture = new KeyGesture(Key.Oem2, s_command), Command = app }
            );

            window.Press(Key.Oem2, s_command);

            app.Runs.ShouldBe(1);
        });

    [Fact]
    public Task A_default_removed_before_the_editor_is_attached_stays_removed() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            var gesture = new KeyGesture(Key.A, s_command);
            editor.KeyBindings.RemoveAll(binding => binding.Gesture == gesture);
            var app = new CountingCommand();
            using var window = new EditorWindow(editor);
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = app });

            window.Press(Key.A, s_command);

            app.Runs.ShouldBe(1);
            editor.Selection.Range.Length.ShouldBe(0);
        });

    /// <summary>AvaloniaEdit's Ctrl+D deletes the line, and its Ctrl+I indents it; neither is a default here.</summary>
    [Theory]
    [InlineData(Key.D)]
    [InlineData(Key.I)]
    public Task AvaloniaEdits_line_shortcuts_leave_the_text_alone_and_go_on(Key key) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|b\nc", CodeLanguage.PlainText);
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, s_command), Command = app });

            window.Press(key, s_command);

            app.Runs.ShouldBe(1);
            MarkedText.Of(window).ShouldBe("a|b\nc");
        });

    public static TheoryData<Key, KeyModifiers, bool> KeysThatDoNotApply =>
        new()
        {
            // Nothing to undo or redo.
            { Key.Z, s_command, false },
            { Key.Z, s_command | KeyModifiers.Shift, false },
            // No server that completes or gives signatures.
            { Key.Space, KeyModifiers.Control, false },
            { Key.Space, s_command | KeyModifiers.Shift, false },
            // Read-only text.
            { Key.X, s_command, true },
            { Key.V, s_command, true },
            { Key.Oem2, s_command, true },
        };

    [Theory]
    [MemberData(nameof(KeysThatDoNotApply))]
    public Task A_key_whose_command_does_not_apply_goes_on_to_the_app(Key key, KeyModifiers modifiers, bool readOnly) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|b", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = readOnly;
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(key, modifiers), Command = app });

            window.Press(key, modifiers);

            app.Runs.ShouldBe(1);
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("ab");
        });

    [Fact]
    public Task A_focused_editor_beats_the_windows_binding_for_a_key_it_uses() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|text", CodeLanguage.PlainText);
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.A, s_command), Command = app });

            window.Press(Key.A, s_command);

            app.Runs.ShouldBe(0);
            window.Editor.Selection.ShouldBe(new TextSelection(0, 4));
        });

    [Fact]
    public Task Without_a_document_Ctrl_F_reaches_the_app() =>
        _ui.Run(() =>
        {
            using var window = new EditorWindow(new CodeEditor());
            var app = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F, s_command), Command = app });

            window.Press(Key.F, s_command);

            app.Runs.ShouldBe(1);
        });

    [Fact]
    public Task The_search_bars_text_box_keeps_its_clipboard_and_selection_keys() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("|one two", CodeLanguage.PlainText);
            window.Press(Key.F, s_command);
            SearchBar bar = BarOf(window);
            window.Type("two");

            // The headless platform gives text boxes Ctrl's keys on every system, also on macOS.
            window.Press(Key.A, KeyModifiers.Control);
            window.Press(Key.C, KeyModifiers.Control);

            bar.FindBox.SelectedText.ShouldBe("two");
            window.Editor.Selection.ShouldNotBe(new TextSelection(0, 7));
            (await Clipboard(window).TryGetTextAsync()).ShouldBe("two");
        });

    [Fact]
    public Task The_find_keys_also_work_from_the_search_bar() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("| a b a", CodeLanguage.PlainText);
            window.Press(Key.F, s_command);
            SearchBar bar = BarOf(window);
            window.Type("a");
            await Wait.UntilAsync(() => bar.Count == "1 of 2");

            window.Press(Key.F3);

            window.Editor.Selection.ShouldBe(new TextSelection(5, 6));
            bar.FindBox.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task RunCommand_from_a_button_runs_and_gives_the_text_the_keyboard() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            var button = new Button { Content = "Select all" };
            using var window = new EditorWindow(editor, new StackPanel { Children = { button, editor } });
            button.Click += (_, _) => editor.RunCommand(CodeEditorCommands.SelectAll).ShouldBeTrue();
            button.Focus();

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            editor.Selection.ShouldBe(new TextSelection(0, 4));
            TextAreaOf(window).IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task RunCommand_from_a_context_menu_runs_on_the_text() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            var item = new MenuItem { Header = "Comment" };
            var menu = new ContextMenu { Items = { item } };
            var button = new Button { Content = "Menu", ContextMenu = menu };
            using var window = new EditorWindow(editor, new StackPanel { Children = { button, editor } });
            item.Click += (_, _) => editor.RunCommand(CodeEditorCommands.SelectAll).ShouldBeTrue();
            button.Focus();
            menu.Open(button);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.Close();
            window.Layout();

            editor.Selection.ShouldBe(new TextSelection(0, 4));
            TextAreaOf(window).IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task RunCommand_for_Find_leaves_the_keyboard_in_the_query() =>
        _ui.Run(() =>
        {
            var editor = new CodeEditor { Document = CodeDocument.Create(CodeLanguage.PlainText, "text") };
            var button = new Button { Content = "Find" };
            using var window = new EditorWindow(editor, new StackPanel { Children = { button, editor } });
            button.Focus();

            editor.RunCommand(CodeEditorCommands.Find).ShouldBeTrue();

            BarOf(window).FindBox.IsFocused.ShouldBeTrue();
        });

    [Fact]
    public Task RunCommand_answers_false_where_the_command_does_not_apply() =>
        _ui.Run(() =>
        {
            using (var empty = new EditorWindow(new CodeEditor()))
                empty.Editor.RunCommand(CodeEditorCommands.Copy).ShouldBeFalse();
            using EditorWindow window = MarkedText.Show("a|b", CodeLanguage.PlainText);

            window.Editor.RunCommand("no.such.command").ShouldBeFalse();
            window.Editor.RunCommand(CodeEditorCommands.Undo).ShouldBeFalse();
            window.Editor.IsReadOnly = true;
            window.Editor.RunCommand(CodeEditorCommands.Paste).ShouldBeFalse();
            window.Editor.RunCommand(CodeEditorCommands.Copy).ShouldBeTrue();
        });

    /// <summary>An app's <c>KeyBinding</c> to the command consumes its key even where the command does not apply.</summary>
    [Fact]
    public Task An_apps_binding_to_RunCommand_consumes_its_key() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("a|b", CodeLanguage.PlainText);
            var gesture = new KeyGesture(Key.J, s_command);
            var outer = new CountingCommand();
            window.Window.KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = outer });
            CodeEditor editor = window.Editor;
            window.Editor.KeyBindings.Insert(
                0,
                new KeyBinding { Gesture = gesture, Command = new RunCommandCommand(editor, CodeEditorCommands.Undo) }
            );

            window.Press(Key.J, s_command);

            outer.Runs.ShouldBe(0);
        });

    [Fact]
    public void On_macOS_Cmd_takes_Ctrls_place_but_for_Ctrl_Space()
    {
        (string Id, KeyGesture Gesture)[] keys = [.. DefaultKeys.Table(mac: true)];

        keys.ShouldContain((CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.Space, KeyModifiers.Control)));
        keys.ShouldContain((CodeEditorCommands.TriggerSuggest, new KeyGesture(Key.Escape, KeyModifiers.Alt)));
        keys.ShouldContain((CodeEditorCommands.Replace, new KeyGesture(Key.F, KeyModifiers.Alt | KeyModifiers.Meta)));
        keys.ShouldContain((CodeEditorCommands.FindNext, new KeyGesture(Key.G, KeyModifiers.Meta)));
        keys.ShouldContain((CodeEditorCommands.Redo, new KeyGesture(Key.Y, KeyModifiers.Meta)));
    }

    [Fact]
    public Task Ctrl_I_opens_the_servers_list_and_leaves_the_lines_as_they_are() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Math", "Console"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x = \ny");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);

            window.Press(Key.I, s_command);

            (await WaitForLabelsAsync(window)).ShouldBe(["Console", "Math"]);
            document.Text.ShouldBe("x = \ny");
        });

    [Fact]
    public Task A_command_that_moves_the_selection_ends_completion() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await SetCompletionAsync(server, fake, Items("Math", "Console"));
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "x = ");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);
            window.Press(Key.Space, KeyModifiers.Control);
            await WaitForLabelsAsync(window);

            window.Press(Key.A, s_command);

            List(window).ShouldBeNull();
        });

    [Fact]
    public Task Ctrl_Shift_Z_and_Ctrl_Y_redo() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("a");

            window.Press(Key.Z, s_command);
            document.Text.ShouldBe("");
            window.Press(Key.Z, s_command | KeyModifiers.Shift);
            document.Text.ShouldBe("a");

            window.Press(Key.Z, s_command);
            window.Press(Key.Y, s_command);
            document.Text.ShouldBe("a");
        });

    [Fact]
    public Task Undo_and_redo_change_nothing_in_a_read_only_editor() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("a");
            window.Editor.IsReadOnly = true;

            window.Press(Key.Z, s_command);
            document.Text.ShouldBe("a");

            document.Undo();
            window.Press(Key.Z, s_command | KeyModifiers.Shift);
            document.Text.ShouldBe("");
        });

    private static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();

    private static TextArea TextAreaOf(EditorWindow window) => EditorWindow.PartOf(window.Editor).TextArea;

    private static IClipboard Clipboard(EditorWindow window) =>
        window.Window.Clipboard ?? throw new InvalidOperationException("The window has no clipboard.");

    /// <summary>An app's command, which counts its runs.</summary>
    private sealed class CountingCommand : ICommand
    {
        public int Runs { get; private set; }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Runs++;
    }

    /// <summary>What an app's XAML binding to <see cref="CodeEditor.RunCommand"/> makes.</summary>
    private sealed class RunCommandCommand(CodeEditor editor, string id) : ICommand
    {
        private readonly CodeEditor _editor = editor;
        private readonly string _id = id;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _editor.RunCommand(_id);
    }
}
