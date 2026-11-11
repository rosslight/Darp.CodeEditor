using Avalonia.Input;
using Avalonia.VisualTree;
using Darp.CodeEditor.Search;
using Keys = Avalonia.Input.Key;

namespace Darp.CodeEditor.Tests;

/// <summary>The keymap: VS Code's key syntax, its keybindings.json, and key sequences of two strokes.</summary>
[Collection(UiFixture.Collection)]
public sealed class KeymapTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public void Keys_are_read_in_VS_Codes_syntax()
    {
        Gestures("ctrl+k ctrl+i")
            .ShouldBe([new KeyGesture(Keys.K, KeyModifiers.Control), new KeyGesture(Keys.I, KeyModifiers.Control)]);
        Gestures("Shift+Cmd+Space").ShouldBe([new KeyGesture(Keys.Space, KeyModifiers.Shift | KeyModifiers.Meta)]);
        Gestures("alt+f3").ShouldBe([new KeyGesture(Keys.F3, KeyModifiers.Alt)]);
        Gestures("ctrl+/").ShouldBe([new KeyGesture(Keys.OemQuestion, KeyModifiers.Control)]);
        Gestures("pagedown").ShouldBe([new KeyGesture(Keys.PageDown)]);
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
            """
            // As a user's keybindings.json.
            [
              { "key": "ctrl+k ctrl+i", "command": "-editor.action.showHover" },
              { "key": "ctrl+q", "command": "editor.action.showHover", "when": "editorTextFocus" },
              { "key": "ctrl+f", "command": "-actions.find" },
            ]
            """
        );

        keymap.CommandsFor(Key(Keys.Q, KeyModifiers.Control), null).ShouldBe([CodeEditorCommands.ShowHover]);
        keymap.ChordStartedBy(Key(Keys.K, KeyModifiers.Control)).ShouldBeNull();
        keymap.CommandsFor(Key(Keys.F, KeyModifiers.Control), null).ShouldBeEmpty();
        // The defaults stay otherwise; the latest binding of a key comes first.
        keymap
            .CommandsFor(Key(Keys.Space, KeyModifiers.Control), null)
            .ShouldBe([CodeEditorCommands.ToggleSuggestionDetails, CodeEditorCommands.TriggerSuggest]);
    }

    [Fact]
    public void By_default_Enter_accepts_completions_item_inserting_and_Tab_and_Shift_Enter_replacing()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Default;

        keymap
            .CommandsFor(Key(Keys.Enter, KeyModifiers.None), null)
            .ShouldBe([CodeEditorCommands.AcceptSelectedSuggestion]);
        keymap
            .CommandsFor(Key(Keys.Tab, KeyModifiers.None), null)
            .ShouldBe([CodeEditorCommands.AcceptAlternativeSelectedSuggestion]);
        keymap
            .CommandsFor(Key(Keys.Enter, KeyModifiers.Shift), null)
            .ShouldBe([CodeEditorCommands.AcceptAlternativeSelectedSuggestion]);
    }

    [Fact]
    public void By_default_F8_and_Alt_F8_go_to_the_next_diagnostic_and_with_Shift_to_the_previous_one()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Default;

        keymap.CommandsFor(Key(Keys.F8, KeyModifiers.None), null).ShouldBe([CodeEditorCommands.NextMarkerInFiles]);
        keymap.CommandsFor(Key(Keys.F8, KeyModifiers.Shift), null).ShouldBe([CodeEditorCommands.PreviousMarkerInFiles]);
        keymap.CommandsFor(Key(Keys.F8, KeyModifiers.Alt), null).ShouldBe([CodeEditorCommands.NextMarker]);
        keymap
            .CommandsFor(Key(Keys.F8, KeyModifiers.Alt | KeyModifiers.Shift), null)
            .ShouldBe([CodeEditorCommands.PreviousMarker]);
    }

    [Fact]
    public void By_default_Ctrl_D_selects_the_word_as_VS_Codes_addSelectionToNextFindMatch_starts()
    {
        CodeEditorKeymap
            .Default.CommandsFor(Key(Keys.D, EditorWindow.Command), null)
            .ShouldBe([CodeEditorCommands.AddSelectionToNextFindMatch]);
    }

    [Fact]
    public void By_default_Ctrl_Left_and_Ctrl_Right_move_by_words_as_VS_Code_binds_them()
    {
        KeyModifiers words = OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control;
        CodeEditorKeymap keymap = CodeEditorKeymap.Default;

        keymap.CommandsFor(Key(Keys.Left, words), null).ShouldBe([CodeEditorCommands.CursorWordLeft]);
        keymap
            .CommandsFor(Key(Keys.Left, words | KeyModifiers.Shift), null)
            .ShouldBe([CodeEditorCommands.CursorWordLeftSelect]);
        keymap.CommandsFor(Key(Keys.Right, words), null).ShouldBe([CodeEditorCommands.CursorWordEndRight]);
        keymap
            .CommandsFor(Key(Keys.Right, words | KeyModifiers.Shift), null)
            .ShouldBe([CodeEditorCommands.CursorWordEndRightSelect]);
    }

    [Fact]
    public void A_punctuation_key_matches_where_the_layout_types_its_character()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Empty.With(new CodeEditorKeyBinding("ctrl+/", "comment"));

        // A German layout types "/" with Shift+7.
        keymap.CommandsFor(Key(Keys.D7, KeyModifiers.Control | KeyModifiers.Shift, "/"), null).ShouldBe(["comment"]);
        keymap.CommandsFor(Key(Keys.Divide, KeyModifiers.Control), null).ShouldBe(["comment"]);
        keymap.CommandsFor(Key(Keys.D7, KeyModifiers.Control | KeyModifiers.Shift), null).ShouldBeEmpty();
    }

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
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("«one» two one", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Default.With(
                new CodeEditorKeyBinding("ctrl+k f", CodeEditorCommands.Find)
            );

            window.Press(Keys.K, KeyModifiers.Control);
            window.Press(Keys.F3);

            window.Editor.PendingKeys.ShouldBeNull();
            // F3 ran as it does alone: the next match of the selection.
            window.Editor.Selection.ShouldBe(new TextSelection(8, 11));
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

    [Fact]
    public Task An_empty_keymap_leaves_the_keys_to_the_app() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|one", CodeLanguage.PlainText);
            window.Editor.Keymap = CodeEditorKeymap.Empty;
            var seen = new List<Keys>();
            window.Window.KeyDown += (_, e) => seen.Add(e.Key);

            window.Press(Keys.F3);

            seen.ShouldBe([Keys.F3]);
            window.Editor.GetVisualDescendants().OfType<SearchBar>().ShouldBeEmpty();
        });

    private static IReadOnlyList<KeyGesture> Gestures(string keys) =>
        new CodeEditorKeyBinding(keys, CodeEditorCommands.Find).Gestures;

    private static KeyEventArgs Key(Keys key, KeyModifiers modifiers, string? symbol = null) =>
        new()
        {
            Key = key,
            KeyModifiers = modifiers,
            KeySymbol = symbol,
        };

    private static SearchBar BarOf(EditorWindow window) =>
        window.Editor.GetVisualDescendants().OfType<SearchBar>().Single();
}
