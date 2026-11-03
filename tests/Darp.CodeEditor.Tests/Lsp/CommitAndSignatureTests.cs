using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaEdit.Document;
using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>Committing completion items whose resolve comes late, and signature help across edits, in an editor.</summary>
public sealed class CommitAndSignatureTests
{
    private static readonly CompletionItemDetails s_usingDirective = new(
        [],
        [new TextEdit(new TextRange(0, 0), "using X;\n")]
    );

    [AvaloniaFact]
    public async Task A_late_resolve_joins_the_commits_undo_step()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);

        Commit(editor);
        editor.Text.ShouldBe("x\nbeta");
        service.Details.SetResult(s_usingDirective);
        await Wait.UntilAsync(() => editor.Text.StartsWith("using", StringComparison.Ordinal));
        editor.Text.ShouldBe("using X;\nx\nbeta");
        editor.Document.UndoStack.Undo();

        editor.Text.ShouldBe("x\nb");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_resolve_that_arrives_after_undo_adds_nothing()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);

        Commit(editor);
        editor.Document.UndoStack.Undo();
        service.Details.SetResult(s_usingDirective);
        await Task.Delay(100);

        editor.Text.ShouldBe("x\nb");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_resolve_that_arrives_after_typing_on_adds_nothing()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);

        Commit(editor);
        editor.TextArea.PerformTextInput(";");
        service.Details.SetResult(s_usingDirective);
        await Task.Delay(100);

        editor.Text.ShouldBe("x\nbeta;");
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_resolve_that_arrives_after_another_undo_step_adds_nothing()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);

        Commit(editor);
        // An undo step of the app that changes no text, so that the document's version stays the commit's.
        editor.Document.UndoStack.Push(new TextlessOperation());
        service.Details.SetResult(s_usingDirective);
        await Task.Delay(100);

        editor.Text.ShouldBe("x\nbeta");
        // The app's step and the commit undo one at a time, each whole.
        editor.Document.UndoStack.Undo();
        editor.Text.ShouldBe("x\nbeta");
        editor.Document.UndoStack.Undo();
        editor.Text.ShouldBe("x\nb");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Commit_replaces_the_whole_range_of_the_item()
    {
        // The list replaces the typed "b"; the item replaces "bcd".
        var service = new ScriptedService { ItemRange = new TextRange(2, 3) };
        service.Details.SetResult(null);
        (CodeEditor editor, Window window) = await OpenWithListAsync(service, "x\ncd");

        Commit(editor);

        editor.Text.ShouldBe("x\nbeta");
        window.Close();
    }

    [AvaloniaFact]
    public async Task An_edit_cancels_signature_help_and_asks_again_once_typing_pauses()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);
        var shown = new List<string>();
        editor.Timing += (_, timing) =>
        {
            if (timing.Name == "signature-help")
                shown.Add(timing.Name);
        };

        editor.TextArea.PerformTextInput("(");
        await Wait.UntilAsync(() => service.SignatureRequests.Count == 1);
        ScriptedService.SignatureRequest first = service.SignatureRequests.Single();
        // It was sent after the ')' that auto-closing added in the same keystroke, so that edit did not cancel it.
        await Task.Delay(50);
        first.Token.IsCancellationRequested.ShouldBeFalse();
        editor.TextArea.PerformTextInput("1");
        first.Token.IsCancellationRequested.ShouldBeTrue();
        await Wait.UntilAsync(() => service.SignatureRequests.Count == 2);
        ScriptedService.SignatureRequest second = service.SignatureRequests[1];
        // The first answer is for an older version and comes too late; the second is shown.
        first.Answer.SetResult(Help());
        second.Answer.SetResult(Help());
        await Wait.UntilAsync(() => shown.Count > 0);
        await Task.Delay(100);

        (first.Trigger.Kind, first.Trigger.Character).ShouldBe((SignatureHelpTriggerKind.TriggerCharacter, '('));
        second.Trigger.Kind.ShouldBe(SignatureHelpTriggerKind.ContentChange);
        first.Position.ShouldBe(4);
        (second.Document.Version, second.Position).ShouldBe((first.Document.Version + 1, editor.CaretOffset));
        shown.Count.ShouldBe(1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Every_edit_restarts_the_pause_before_signature_help_is_asked_again()
    {
        var service = new ScriptedService();
        (CodeEditor editor, Window window) = await OpenWithListAsync(service);
        editor.TextArea.PerformTextInput("(");
        await Wait.UntilAsync(() => service.SignatureRequests.Count == 1);
        int asked = service.SignatureRequests[0].Document.Version;

        // The first edit cancels the request; the second comes before the pause ends.
        editor.TextArea.PerformTextInput("1");
        await Task.Delay(60);
        editor.TextArea.PerformTextInput("2");
        long typed = Stopwatch.GetTimestamp();
        await Wait.UntilAsync(() => service.SignatureRequests[^1].Document.Version == asked + 2);

        // The pause of 100 ms counts from the last edit.
        Stopwatch
            .GetElapsedTime(typed, service.SignatureRequests[^1].SentAt)
            .ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90));
        window.Close();
    }

    /// <summary>Opens an editor on the service, types "b" at offset 2 and waits for the completion list.</summary>
    private static async Task<(CodeEditor Editor, Window Window)> OpenWithListAsync(
        ScriptedService service,
        string text = "x\n"
    )
    {
        var editor = new CodeEditor { DocumentId = "scripted", Text = text };
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
        bool listed = false;
        editor.Timing += (_, timing) => listed |= timing.Name == "completion";
        editor.TextArea.Focus();
        editor.CaretOffset = 2;
        editor.TextArea.PerformTextInput("b");
        await Wait.UntilAsync(() => listed);
        return (editor, window);
    }

    private static void Commit(CodeEditor editor) =>
        editor.TextArea.RaiseEvent(
            new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Enter,
                Source = editor.TextArea,
            }
        );

    private static SignatureHelp Help() =>
        new(
            [new SignatureInfo("f(int a)", null, [new ParameterInfo("int a", null, new TextRange(2, 5))])],
            0,
            0,
            new TextRange(4, 0)
        );

    /// <summary>An undo step that changes no text.</summary>
    private sealed class TextlessOperation : IUndoableOperation
    {
        public void Undo() { }

        public void Redo() { }
    }
}
