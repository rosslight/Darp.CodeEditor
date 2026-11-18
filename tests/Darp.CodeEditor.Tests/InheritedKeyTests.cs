using Avalonia.Input;
using static Darp.CodeEditor.Tests.CompletionTests;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Keys that AvaloniaEdit bound to other commands than VS Code does: the keymap binds them as VS Code does instead.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class InheritedKeyTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public void By_default_Ctrl_I_asks_for_completion_and_toggles_its_details_as_Ctrl_Space_does()
    {
        CodeEditorKeymap
            .Default.CommandsFor(Key(Avalonia.Input.Key.I, EditorWindow.Command), null)
            .ShouldBe([CodeEditorCommands.ToggleSuggestionDetails, CodeEditorCommands.TriggerSuggest]);
    }

    [Fact]
    public void By_default_undo_and_redo_have_VS_Codes_keys()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Default;
        KeyModifiers command = EditorWindow.Command;

        keymap.CommandsFor(Key(Avalonia.Input.Key.Z, command), null).ShouldBe([CodeEditorCommands.Undo]);
        keymap
            .CommandsFor(Key(Avalonia.Input.Key.Z, command | KeyModifiers.Shift), null)
            .ShouldBe([CodeEditorCommands.Redo]);
        // Ctrl+Y on Windows and Linux; on macOS, Cmd+Shift+Z only.
        keymap
            .CommandsFor(Key(Avalonia.Input.Key.Y, command), null)
            .ShouldBe(OperatingSystem.IsMacOS() ? [] : [CodeEditorCommands.Redo]);
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

            window.Press(Avalonia.Input.Key.I, EditorWindow.Command);

            (await WaitForLabelsAsync(window)).ShouldBe(["Console", "Math"]);
            document.Text.ShouldBe("x = \ny");
        });

    [Fact]
    public Task Ctrl_Shift_Z_redoes_and_Ctrl_Y_too_outside_macOS() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("abc");

            window.Press(Avalonia.Input.Key.Z, EditorWindow.Command);
            document.Text.ShouldBe("");
            window.Press(Avalonia.Input.Key.Z, EditorWindow.Command | KeyModifiers.Shift);
            document.Text.ShouldBe("abc");

            window.Press(Avalonia.Input.Key.Z, EditorWindow.Command);
            window.Press(Avalonia.Input.Key.Y, EditorWindow.Command);
            document.Text.ShouldBe(OperatingSystem.IsMacOS() ? "" : "abc");
        });

    [Fact]
    public Task Undo_and_redo_change_nothing_in_a_read_only_editor() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("abc");
            window.Editor.IsReadOnly = true;

            window.Press(Avalonia.Input.Key.Z, EditorWindow.Command);
            document.Text.ShouldBe("abc");

            document.Undo();
            window.Press(Avalonia.Input.Key.Z, EditorWindow.Command | KeyModifiers.Shift);
            document.Text.ShouldBe("");
        });

    private static KeyEventArgs Key(Key key, KeyModifiers modifiers) => new() { Key = key, KeyModifiers = modifiers };
}
