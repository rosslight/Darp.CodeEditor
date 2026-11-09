using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Signature help: the signature of the call around the caret, with its active parameter in bold, asked for on the
/// server's characters and after a pause while typing, in the app's tooltip look above the line, beside completion.
/// The fake server's signatures are "call(first, second)" and "call(first, second, third)".
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SignatureHelpTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_trigger_character_opens_the_signature_with_the_active_parameter_in_bold_and_its_documentation() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await server.StartAsync();
            window.Editor.Selection = new TextSelection(4);

            window.Type("(");

            ToolTip popup = await WaitForSignatureAsync(window, "first", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ")).ShouldEndWith(" 2 ( False - 5");
            Texts(popup).ShouldContain("1/2");
            // The parameter's Markdown.
            popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Single(t => t.Inlines?.Text == "The first one.")
                .Inlines.ShouldNotBeNull()
                .OfType<Run>()
                .ShouldContain(run => run.Text == "first" && run.FontWeight == FontWeight.Bold);
            // The signature as code, in the editor's font.
            Signature(popup).FontFamily.ShouldBe(editor.FontFamily);
            Runs(popup).ShouldContain(run => run.Text == "first" && run.FontWeight == FontWeight.Bold);
        });

    [Fact]
    public Task A_retrigger_character_moves_the_active_parameter_at_once() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            window.Editor.Selection = new TextSelection(4);
            window.Type("(");
            await WaitForSignatureAsync(window, "first", "Calls with two.");

            window.Type(" ,");

            await WaitForSignatureAsync(window, "second", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 2 , True 0 7");
        });

    [Fact]
    public Task Typing_inside_the_call_asks_again_after_a_pause_that_each_edit_restarts() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            window.Editor.Selection = new TextSelection(4);
            window.Type("(");
            await WaitForSignatureAsync(window, "first", "Calls with two.");

            window.Type("+");
            time.Advance(TimeSpan.FromMilliseconds(100));
            window.Type("-");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("signatureHelp ").Count.ShouldBe(1);

            time.Advance(TimeSpan.FromMilliseconds(20));

            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 3 - True 0 7");
        });

    [Fact]
    public Task Up_and_Down_show_the_other_signatures_the_server_keeps_the_choice_and_an_open_list_takes_them() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await CompletionTests.SetCompletionAsync(
                server,
                fake,
                CompletionTests.Items("alpha", "beta")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);
            window.Type("(");
            await WaitForSignatureAsync(window, "first", "Calls with two.");

            window.Press(Key.Down);
            ToolTip popup = await WaitForSignatureAsync(window, "first", "Calls with three.");
            Texts(popup).ShouldContain("2/2");
            document.Text.ShouldBe("call(");

            // Asking again tells the server the signature that shows, and it keeps it.
            window.Type(" ,");
            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 2 , True 1 7");
            await WaitForSignatureAsync(window, "second", "Calls with three.");

            window.Press(Key.Space, KeyModifiers.Control);
            await CompletionTests.WaitForLabelsAsync(window);
            window.Press(Key.Down);
            CompletionTests.List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(1);
            Texts(Popup(window).ShouldNotBeNull()).ShouldContain("2/2");
        });

    [Fact]
    public Task Signature_help_shows_above_the_line_and_an_open_completion_list_below_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument answer = await CompletionTests.SetCompletionAsync(
                server,
                fake,
                CompletionTests.Items("alpha", "beta")
            );
            const string text = "\n\n\n\n\ncall";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(text.Length);
            window.Type("(");
            ToolTip popup = await WaitForSignatureAsync(window, "first", "Calls with two.");

            window.Press(Key.Space, KeyModifiers.Control);
            await CompletionTests.WaitForLabelsAsync(window);

            Point caret = window.PointAt(text.Length);
            CompletionTests.BoundsInWindow(popup).Bottom.ShouldBeLessThan(caret.Y);
            CompletionTests
                .BoundsInWindow(CompletionTests.Flyout(window).ShouldNotBeNull())
                .Top.ShouldBeGreaterThan(caret.Y);
            Popup(window).ShouldBeSameAs(popup);
        });

    [Fact]
    public Task Signature_help_ends_on_Escape_leaving_the_call_focus_loss_and_a_server_change() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });
            await server.StartAsync();

            await OpenAsync(window, document);
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();

            // The server has none once the caret is after the closing parenthesis.
            await OpenAsync(window, document);
            window.Type(")");
            await Wait.UntilAsync(() => Popup(window) is null);

            await OpenAsync(window, document);
            button.Focus();
            window.Layout();
            Popup(window).ShouldBeNull();

            editor.Focus();
            await OpenAsync(window, document);
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");
            await Wait.UntilAsync(() => Popup(window) is null);
        });

    /// <summary>Opens signature help on "call" anew: types "(" after it.</summary>
    private static async Task OpenAsync(EditorWindow window, CodeDocument document)
    {
        document.Text = "call";
        window.Editor.Selection = new TextSelection(4);
        window.Type("(");
        await WaitForSignatureAsync(window, "first", "Calls with two.");
    }

    /// <summary>
    /// Waits for the popup with <paramref name="documentation"/>, whose bold parameter is <paramref name="active"/>.
    /// </summary>
    private static async Task<ToolTip> WaitForSignatureAsync(EditorWindow window, string active, string documentation)
    {
        ToolTip? popup = null;
        await Wait.UntilAsync(() =>
        {
            window.Render();
            popup = Popup(window);
            return popup is not null
                && Texts(popup).Contains(documentation)
                && Runs(popup).Any(run => run.Text == active && run.FontWeight == FontWeight.Bold);
        });
        return popup ?? throw new InvalidOperationException();
    }

    /// <summary>The signature help's popup in the window's overlay layer; null while it is closed.</summary>
    private static ToolTip? Popup(EditorWindow window) =>
        OverlayLayer.GetOverlayLayer(window.Window)?.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault();

    private static TextBlock Signature(ToolTip popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines?.Text?.StartsWith("call(") == true);

    private static IEnumerable<Run> Runs(ToolTip popup) => Signature(popup).Inlines?.OfType<Run>() ?? [];

    private static IReadOnlyList<string> Texts(ToolTip popup) =>
        [.. popup.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text ?? "")];
}
