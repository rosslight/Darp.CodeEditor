using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Commands;
using Darp.CodeEditor.Search;
using Keys = Avalonia.Input.Key;

namespace Darp.CodeEditor.Tests;

/// <summary>The keymap: VS Code's key syntax, its keybindings.json, and key sequences of two strokes.</summary>
[Collection(UiFixture.Collection)]
public sealed class KeymapTests(UiFixture ui)
{
    /// <summary>The key that commands hold in the default keys, in their syntax.</summary>
    private static readonly string s_command = OperatingSystem.IsMacOS() ? "cmd" : "ctrl";

    private readonly UiFixture _ui = ui;

    [Fact]
    public void Keys_are_read_as_modifiers_and_a_key_joined_by_plus_in_any_case()
    {
        Runs("ctrl+k ctrl+i", Key(Keys.K, KeyModifiers.Control), Key(Keys.I, KeyModifiers.Control));
        Runs("Shift+Cmd+Space", Key(Keys.Space, KeyModifiers.Shift | KeyModifiers.Meta));
        Runs("alt+f3", Key(Keys.F3, KeyModifiers.Alt));
        Runs("ctrl+/", Key(Keys.OemQuestion, KeyModifiers.Control));
        Runs("pagedown", Key(Keys.PageDown, KeyModifiers.None));
        // Its modifiers, and no others.
        Resolve(Keymap("ctrl+k"), Key(Keys.K, KeyModifiers.Control | KeyModifiers.Shift)).Commands.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("ctrl+")]
    [InlineData("hyper+a")]
    [InlineData("ctrl+nokey")]
    [InlineData("a b c")]
    [InlineData("")]
    public void Keys_outside_the_syntax_are_refused(string keys) =>
        Should.Throw<FormatException>(() => new CodeEditorKeyBinding(keys, CodeEditorCommands.Find));

    [Fact]
    public void A_keybindings_json_adds_to_the_defaults_and_a_minus_removes_a_binding()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.FromJson(
            $$"""
            // As a user's keybindings.json.
            [
              { "key": "{{s_command}}+/", "command": "-editor.action.commentLine" },
              { "key": "ctrl+q", "command": "editor.action.commentLine", "when": "editorTextFocus" },
              { "command": "-actions.find" },
              { "key": "ctrl+space", "command": "editor.action.triggerParameterHints" },
            ]
            """
        );

        // A removal with keys removes the command's binding of those keys only; without keys, all of them.
        Resolve(keymap, Key(Keys.Q, KeyModifiers.Control)).Commands.ShouldBe([CodeEditorCommands.CommentLine]);
        Resolve(keymap, Key(Keys.OemQuestion, EditorWindow.Command)).Commands.ShouldBeEmpty();
        Resolve(keymap, Key(Keys.F, EditorWindow.Command)).Commands.ShouldBeEmpty();
        // The defaults stay otherwise; the latest binding of a key comes first.
        Resolve(keymap, Key(Keys.Space, KeyModifiers.Control))
            .Commands.ShouldBe([CodeEditorCommands.TriggerParameterHints, CodeEditorCommands.TriggerSuggest]);
        keymap.Bindings.Count.ShouldBe(CodeEditorKeymap.Default.Bindings.Count + 4);
    }

    [Fact]
    public void A_punctuation_key_matches_where_the_layout_types_its_character()
    {
        CodeEditorKeymap keymap = Keymap("ctrl+/");

        // A German layout types "/" with Shift+7.
        Resolve(keymap, Key(Keys.D7, KeyModifiers.Control | KeyModifiers.Shift, "/")).Commands.ShouldBe(["command"]);
        Resolve(keymap, Key(Keys.Divide, KeyModifiers.Control)).Commands.ShouldBe(["command"]);
        Resolve(keymap, Key(Keys.D7, KeyModifiers.Control | KeyModifiers.Shift)).Commands.ShouldBeEmpty();
    }

    [Fact]
    public void A_punctuation_key_bound_with_Shift_needs_Shift()
    {
        CodeEditorKeymap keymap = Keymap("ctrl+shift+/");

        Resolve(keymap, Key(Keys.OemQuestion, KeyModifiers.Control, "/")).Commands.ShouldBeEmpty();
        Resolve(keymap, Key(Keys.OemQuestion, KeyModifiers.Control | KeyModifiers.Shift))
            .Commands.ShouldBe(["command"]);
    }

    [Fact]
    public void A_key_that_starts_several_sequences_waits_for_the_second_stroke_of_each()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Empty.With(
            new CodeEditorKeyBinding("ctrl+/ f", "slash"),
            new CodeEditorKeyBinding("ctrl+numpad_divide g", "divide")
        );
        PressedKey divide = Key(Keys.Divide, KeyModifiers.Control);

        KeyResolution.Of(keymap, null, divide).Waits.ShouldBeTrue();
        KeyResolution.Of(keymap, divide, Key(Keys.F, KeyModifiers.None)).Commands.ShouldBe(["slash"]);
        KeyResolution.Of(keymap, divide, Key(Keys.G, KeyModifiers.None)).Commands.ShouldBe(["divide"]);
    }

    /// <summary>Only an omitted or empty key removes every binding of the command.</summary>
    [Fact]
    public void A_key_that_is_no_string_is_refused() =>
        Should.Throw<System.Text.Json.JsonException>(() =>
            CodeEditorKeymap.FromJson("""[ { "key": 7, "command": "-actions.find" } ]""")
        );

    [Fact]
    public void On_macOS_Cmd_takes_Ctrls_place_and_Cmd_G_goes_to_the_next_match()
    {
        var keymap = new CodeEditorKeymap(CodeEditorKeymap.DefaultBindings(mac: true));
        KeyModifiers cmd = KeyModifiers.Meta;

        Resolve(keymap, Key(Keys.G, cmd)).Commands.ShouldBe([CodeEditorCommands.FindNext]);
        Resolve(keymap, Key(Keys.G, cmd | KeyModifiers.Shift)).Commands.ShouldBe([CodeEditorCommands.FindPrevious]);
        Resolve(keymap, Key(Keys.F3, KeyModifiers.None)).Commands.ShouldBe([CodeEditorCommands.FindNext]);
        Resolve(keymap, Key(Keys.F3, KeyModifiers.Shift)).Commands.ShouldBe([CodeEditorCommands.FindPrevious]);
        Resolve(keymap, Key(Keys.F, cmd)).Commands.ShouldBe([CodeEditorCommands.Find]);
        Resolve(keymap, Key(Keys.F, cmd | KeyModifiers.Alt)).Commands.ShouldBe([CodeEditorCommands.Replace]);
        // Cmd+Space is the system's search: completion keeps Ctrl+Space.
        Resolve(keymap, Key(Keys.Space, KeyModifiers.Control)).Commands.ShouldBe([CodeEditorCommands.TriggerSuggest]);
        Resolve(keymap, Key(Keys.Escape, KeyModifiers.Alt)).Commands.ShouldBe([CodeEditorCommands.TriggerSuggest]);
        Resolve(keymap, Key(Keys.I, cmd)).Commands.ShouldBe([CodeEditorCommands.TriggerSuggest]);
        Resolve(keymap, Key(Keys.Space, cmd | KeyModifiers.Shift))
            .Commands.ShouldBe([CodeEditorCommands.TriggerParameterHints]);
        Resolve(keymap, Key(Keys.OemQuestion, cmd)).Commands.ShouldBe([CodeEditorCommands.CommentLine]);
        Resolve(keymap, Key(Keys.Z, cmd)).Commands.ShouldBe([CodeEditorCommands.Undo]);
        Resolve(keymap, Key(Keys.Z, cmd | KeyModifiers.Shift)).Commands.ShouldBe([CodeEditorCommands.Redo]);
        Resolve(keymap, Key(Keys.Y, cmd)).Commands.ShouldBeEmpty();
        Resolve(keymap, Key(Keys.H, KeyModifiers.Control)).Commands.ShouldBeEmpty();
    }

    [Fact]
    public Task On_macOS_Cmd_G_and_Shift_Cmd_G_go_to_the_next_and_the_previous_match() =>
        _ui.Run(async () =>
        {
            // Three matches, so that the next and the previous one differ.
            using EditorWindow window = MarkedText.Show("one two «one» two one", CodeLanguage.PlainText);
            window.Editor.Keymap = new CodeEditorKeymap(CodeEditorKeymap.DefaultBindings(mac: true));

            window.Press(Keys.G, KeyModifiers.Meta);
            await SearchTests.SearchedAsync(BarOf(window));
            window.Editor.Selection.ShouldBe(new TextSelection(16, 19));

            window.Press(Keys.G, KeyModifiers.Meta | KeyModifiers.Shift);
            await SearchTests.SearchedAsync(BarOf(window));
            window.Editor.Selection.ShouldBe(new TextSelection(8, 11));
        });

    [Fact]
    public Task A_sequence_waits_for_its_second_key_and_says_so() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one two", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );

            window.Press(Keys.K, KeyModifiers.Control);
            window.Editor.PendingKeys.ShouldBe(new KeyGesture(Keys.K, KeyModifiers.Control));
            // A modifier alone keeps it waiting.
            window.Press(Keys.LeftShift, KeyModifiers.Shift);
            window.Editor.PendingKeys.ShouldNotBeNull();
            window.Press(Keys.F);

            window.Editor.PendingKeys.ShouldBeNull();
            BarOf(window).IsOpen.ShouldBeTrue();
        });

    [Fact]
    public Task A_second_key_that_completes_no_sequence_is_an_ordinary_key() =>
        _ui.Run(async () =>
        {
            using EditorWindow window = MarkedText.Show("«one» two one", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );

            window.Press(Keys.K, KeyModifiers.Control);
            window.Press(Keys.F3);
            await SearchTests.SearchedAsync(BarOf(window));

            window.Editor.PendingKeys.ShouldBeNull();
            // F3 ran as it does alone: the next match of the selection.
            window.Editor.Selection.ShouldBe(new TextSelection(8, 11));
        });

    [Fact]
    public Task A_later_binding_of_a_key_wins_over_an_earlier_sequence_that_starts_with_it_where_it_applies() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("o|ne\ntwo", CodeLanguage.PlainText);
            window.Editor.IsReadOnly = true;
            CodeEditorKeymap sequence = CodeEditorKeymap.Empty.With(
                new CodeEditorKeyBinding("ctrl+k ctrl+i", CodeEditorCommands.TriggerSuggest)
            );

            // A read-only editor comments nothing: the sequence goes on to wait.
            window.Editor.Keymap = sequence.With(new CodeEditorKeyBinding("ctrl+k", CodeEditorCommands.CommentLine));
            window.Press(Keys.K, KeyModifiers.Control);
            window.Editor.PendingKeys.ShouldBe(new KeyGesture(Keys.K, KeyModifiers.Control));

            window.Editor.Keymap = sequence.With(new CodeEditorKeyBinding("ctrl+k", CodeEditorCommands.Find));
            window.Press(Keys.K, KeyModifiers.Control);

            window.Editor.PendingKeys.ShouldBeNull();
            BarOf(window).IsOpen.ShouldBeTrue();
        });

    [Fact]
    public Task A_new_document_or_keymap_ends_the_wait() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );

            window.Press(Keys.K, KeyModifiers.Control);
            window.Editor.Document = CodeDocument.Create(CodeLanguage.PlainText, "two");
            window.Editor.PendingKeys.ShouldBeNull();

            window.Press(Keys.K, KeyModifiers.Control);
            window.Editor.Keymap = CodeEditorKeymap.Default;
            window.Editor.PendingKeys.ShouldBeNull();
        });

    [Theory]
    [InlineData(CodeEditorCommands.CommentLine)]
    [InlineData(CodeEditorCommands.Undo)]
    [InlineData(CodeEditorCommands.Redo)]
    [InlineData(CodeEditorCommands.TriggerSuggest)]
    [InlineData(CodeEditorCommands.TriggerParameterHints)]
    public Task A_command_that_a_read_only_editor_refuses_leaves_its_key_to_the_binding_before(string command) =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("1|\n2", CodeLanguage.Json);
            window.Editor.IsReadOnly = true;
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("f2", CodeEditorCommands.Find),
                new CodeEditorKeyBinding("f2", command)
            );

            window.Press(Keys.F2);

            BarOf(window).IsOpen.ShouldBeTrue();
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("1\n2");
        });

    /// <summary>Plain text, without a server, comments or anything to undo.</summary>
    [Theory]
    [InlineData(CodeEditorCommands.CommentLine)]
    [InlineData(CodeEditorCommands.Undo)]
    [InlineData(CodeEditorCommands.Redo)]
    [InlineData(CodeEditorCommands.TriggerSuggest)]
    [InlineData(CodeEditorCommands.TriggerParameterHints)]
    public Task A_command_with_nothing_to_act_on_leaves_its_key_to_the_binding_before(string command) =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("1|\n2", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("f2", CodeEditorCommands.Find),
                new CodeEditorKeyBinding("f2", command)
            );

            window.Press(Keys.F2);

            BarOf(window).IsOpen.ShouldBeTrue();
            window.Editor.Document.ShouldNotBeNull().Text.ShouldBe("1\n2");
        });

    /// <summary>
    /// The editor raises <see cref="CodeEditor.PendingKeys"/> once the switch is done, so that it shows one document
    /// with one set of features, also when a handler sets another document.
    /// </summary>
    [Fact]
    public Task A_document_set_while_a_switch_ends_the_wait_is_the_only_one_the_editor_runs_for() =>
        _ui.Run(async () =>
        {
            await CodeLanguage.Json.Loaded;
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.Json);
            CodeEditor editor = window.Editor;
            editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );
            CodeDocument third = CodeDocument.Create(CodeLanguage.Json);
            window.Press(Keys.K, KeyModifiers.Control);
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == CodeEditor.PendingKeysProperty && editor.Document != third)
                    editor.Document = third;
            };

            editor.Document = CodeDocument.Create(CodeLanguage.Json);
            window.Type("[");

            editor.Document.ShouldBe(third);
            third.Text.ShouldBe("[]");
        });

    [Fact]
    public Task A_click_ends_the_wait() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one two", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );

            window.Press(Keys.K, KeyModifiers.Control);
            window.Click(window.PointAt(1), MouseButton.Left);
            window.Editor.PendingKeys.ShouldBeNull();
            window.Press(Keys.F);

            window.Editor.GetVisualDescendants().OfType<SearchBar>().ShouldBeEmpty();
        });

    /// <summary>Also the keys of the editor's commands that AvaloniaEdit bound to its own: Ctrl+Z and Ctrl+I.</summary>
    [Fact]
    public Task An_empty_keymap_leaves_the_keys_to_the_app() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one\n  two", CodeLanguage.PlainText);
            window.Type("x");
            window.Editor.Keymap = CodeEditorKeymap.Empty;
            var seen = new List<Keys>();
            window.Window.KeyDown += (_, e) => seen.Add(e.Key);

            window.Press(Keys.F3);
            window.Press(Keys.Z, EditorWindow.Command);
            window.Press(Keys.I, KeyModifiers.Control);

            seen.ShouldBe([Keys.F3, Keys.Z, Keys.I]);
            window.Editor.GetVisualDescendants().OfType<SearchBar>().ShouldBeEmpty();
            MarkedText.Of(window).ShouldBe("x|one\n  two");
        });

    /// <summary>Whether the strokes run the command of a keymap with only <paramref name="keys"/>.</summary>
    private static void Runs(string keys, params PressedKey[] strokes)
    {
        CodeEditorKeymap keymap = Keymap(keys);
        PressedKey? pending = null;
        foreach (PressedKey stroke in strokes[..^1])
        {
            KeyResolution first = KeyResolution.Of(keymap, pending, stroke);
            first.Commands.ShouldBeEmpty();
            first.Waits.ShouldBeTrue();
            pending = stroke;
        }
        KeyResolution.Of(keymap, pending, strokes[^1]).Commands.ShouldBe(["command"]);
    }

    private static CodeEditorKeymap Keymap(string keys) =>
        CodeEditorKeymap.Empty.With(new CodeEditorKeyBinding(keys, "command"));

    private static KeyResolution Resolve(CodeEditorKeymap keymap, PressedKey key) =>
        KeyResolution.Of(keymap, pending: null, key);

    internal static PressedKey Key(Keys key, KeyModifiers modifiers, string? symbol = null) =>
        new(key, modifiers, symbol);

    private static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();
}
