using Avalonia.Input;
using Darp.CodeEditor.Commands;
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
    public void By_default_Ctrl_I_asks_for_completion_and_toggles_its_details_as_Ctrl_Space_does() =>
        KeyResolution
            .Of(CodeEditorKeymap.Default, null, KeymapTests.Key(Key.I, EditorWindow.Command))
            .Commands.ShouldBe([CodeEditorCommands.ToggleSuggestionDetails, CodeEditorCommands.TriggerSuggest]);

    [Fact]
    public void By_default_Ctrl_Z_undoes_and_Ctrl_Shift_Z_redoes_and_outside_macOS_Ctrl_Y_too()
    {
        CodeEditorKeymap keymap = CodeEditorKeymap.Default;
        KeyModifiers command = EditorWindow.Command;

        KeyResolution.Of(keymap, null, KeymapTests.Key(Key.Z, command)).Commands.ShouldBe([CodeEditorCommands.Undo]);
        KeyResolution
            .Of(keymap, null, KeymapTests.Key(Key.Z, command | KeyModifiers.Shift))
            .Commands.ShouldBe([CodeEditorCommands.Redo]);
        KeyResolution
            .Of(keymap, null, KeymapTests.Key(Key.Y, command))
            .Commands.ShouldBe(OperatingSystem.IsMacOS() ? [] : [CodeEditorCommands.Redo]);
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

            window.Press(Key.I, EditorWindow.Command);

            (await WaitForLabelsAsync(window)).ShouldBe(["Console", "Math"]);
            document.Text.ShouldBe("x = \ny");
        });

    [Fact]
    public Task Ctrl_Shift_Z_redoes_and_Ctrl_Y_too_outside_macOS() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("a");

            window.Press(Key.Z, EditorWindow.Command);
            document.Text.ShouldBe("");
            window.Press(Key.Z, EditorWindow.Command | KeyModifiers.Shift);
            document.Text.ShouldBe("a");

            window.Press(Key.Z, EditorWindow.Command);
            window.Press(Key.Y, EditorWindow.Command);
            document.Text.ShouldBe(OperatingSystem.IsMacOS() ? "" : "a");
        });

    [Fact]
    public Task Undo_and_redo_change_nothing_in_a_read_only_editor() =>
        _ui.Run(() =>
        {
            using EditorWindow window = MarkedText.Show("|", CodeLanguage.PlainText);
            CodeDocument document = window.Editor.Document.ShouldNotBeNull();
            window.Type("a");
            window.Editor.IsReadOnly = true;

            window.Press(Key.Z, EditorWindow.Command);
            document.Text.ShouldBe("a");

            document.Undo();
            window.Press(Key.Z, EditorWindow.Command | KeyModifiers.Shift);
            document.Text.ShouldBe("");
        });
}
