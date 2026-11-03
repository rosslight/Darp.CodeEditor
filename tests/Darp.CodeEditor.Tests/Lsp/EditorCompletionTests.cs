using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>Completion in an editor, against the toy server.</summary>
public sealed class EditorCompletionTests
{
    [AvaloniaFact]
    public async Task Typing_on_asks_an_incomplete_list_again_and_commit_applies_the_items_additional_edit()
    {
        var servers = new ToyServers();
        await using LspLanguageService service = servers.CreateService();
        await service.PrewarmAsync(Wait.Timeout);
        var editor = new CodeEditor { DocumentId = "a", Text = "x\n" };
        var window = new Window
        {
            Content = editor,
            Width = 800,
            Height = 600,
        };
        window.Show();
        // The list opens at the caret, which needs a layout.
        window.UpdateLayout();
        editor.LanguageService = service;
        var timings = new List<string>();
        editor.Timing += (_, timing) => timings.Add(timing.Name);
        // The list closes when the editor loses the focus.
        editor.TextArea.Focus();
        editor.CaretOffset = 2;

        editor.TextArea.PerformTextInput("b");
        await Wait.UntilAsync(() => timings.Contains("completion"));
        editor.TextArea.PerformTextInput("e");
        await Wait.UntilAsync(() => timings.Contains("completion-refined"));
        editor.TextArea.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                Source = editor.TextArea,
            }
        );
        // The edit that goes with beta comes with the list or its resolve, whichever the commit has.
        await Wait.UntilAsync(() => editor.Text.StartsWith("-- uses beta", StringComparison.Ordinal));

        servers
            .SentOf("textDocument/completion")
            .Select(m => (int?)m["params"]?["context"]?["triggerKind"])
            .ShouldBe([1, 3]);
        editor.Text.ShouldBe("-- uses beta\nx\nbeta()");
        window.Close();
    }
}
