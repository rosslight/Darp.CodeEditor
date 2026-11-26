using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Popups;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Signature help: every signature of the call around the caret, the active one marked and its active parameter in bold,
/// asked for on the server's characters and after a pause while typing, in the editor's own popup above the line,
/// beside completion. The fake server's signatures are "call(first, second)" and "call(first, second, third)". The
/// pauses run on a clock that the test moves.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class SignatureHelpTests(UiFixture ui)
{
    private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(120);

    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_long_signature_and_documentation_show_only_as_much_as_a_hover() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            var signatures = new JsonArray(
                new JsonObject
                {
                    ["label"] = "call(first, " + new string('x', 5_000) + ")",
                    ["documentation"] = new JsonObject
                    {
                        ["kind"] = "markdown",
                        ["value"] = string.Concat(Enumerable.Repeat("**word** ", 2_000)),
                    },
                    ["parameters"] = new JsonArray(new JsonObject { ["label"] = "first" }),
                }
            );
            using CodeDocument command = server.CreateDocument(
                FakeServer.Commands,
                $"signatures {signatures.ToJsonString()}"
            );
            await fake.WaitForAsync("set signatures");
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(4);

            window.Type("(");

            PopupFrame? popup = null;
            await Wait.UntilAsync(() =>
            {
                window.Render();
                popup = Popup(window);
                return popup is not null
                    && Runs(popup).Any(run => run.Text == "first" && run.FontWeight == FontWeight.Bold);
            });
            // The signature uses up the budget: the documentation does not show.
            string label = Signature(popup!).Inlines?.Text ?? "";
            label.Length.ShouldBe(HoverBudget.MaxCharacters + 1);
            label.ShouldEndWith("…");
            Texts(popup!).Sum(text => text.Length).ShouldBe(label.Length);
        });

    [Fact]
    public Task A_long_signature_shows_on_one_line_within_the_window_and_its_documentation_wraps_as_a_hovers() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document }, width: 1200);
            double hover = PopupFrame.MaxTextWidth(window.Editor.Bounds.Width);
            // Wider than a hover, narrower than the window.
            var words = new List<string> { "first" };
            while (words.Sum(word => word.Length + 2) * window.TextView.WideSpaceWidth < hover + 100)
                words.Add("parameter" + words.Count);
            string label = $"call({string.Join(", ", words)})";
            string documentation = string.Join(' ', Enumerable.Repeat("word", 300));
            var signatures = new JsonArray(
                new JsonObject
                {
                    ["label"] = label,
                    ["documentation"] = documentation,
                    ["parameters"] = new JsonArray(new JsonObject { ["label"] = "first" }),
                }
            );
            using CodeDocument command = server.CreateDocument(
                FakeServer.Commands,
                $"signatures {signatures.ToJsonString()}"
            );
            await fake.WaitForAsync("set signatures");
            window.Editor.Selection = new TextSelection(4);

            window.Type("(");

            PopupFrame popup = await WaitForSignatureAsync(window, "first", documentation);
            Signature(popup).Inlines?.Text.ShouldBe(label);
            Signature(popup).Bounds.Height.ShouldBeLessThan(2 * window.TextView.DefaultLineHeight);
            Text(popup, documentation).Bounds.Width.ShouldBeLessThanOrEqualTo(hover);
        });

    [Fact]
    public Task A_retrigger_character_moves_the_active_parameter_at_once() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.Selection = new TextSelection(4);
            window.Type("(");
            await WaitForSignatureAsync(window, "first", "Calls with two.");

            window.Type(" ,");

            await WaitForSignatureAsync(window, "second", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 2 , True 0 7");
        });

    [Fact]
    public Task A_trigger_character_opens_every_signature_with_the_active_one_marked_its_parameter_in_bold_and_its_documentation() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.Selection = new TextSelection(4);

            window.Type("(");
            time.Advance(s_pause);

            PopupFrame popup = await WaitForSignatureAsync(window, "first", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ")).ShouldEndWith(" 2 ( False - 5");
            Texts(popup).ShouldContain("call(first, second, third)");
            ActiveSignature(popup).ShouldBe("call(first, second)");
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
    public Task Ctrl_Shift_Space_asks_for_the_signature_on_demand() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call()");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.Selection = new TextSelection(5);

            window.Press(Key.Space, EditorWindow.Command | KeyModifiers.Shift);

            await WaitForSignatureAsync(window, "first", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ")).ShouldContain(" 1 ");
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
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);

            window.Type("+");
            time.Advance(TimeSpan.FromMilliseconds(100));
            window.Type("-");
            time.Advance(TimeSpan.FromMilliseconds(100));
            window.Layout();
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("signatureHelp ").Count.ShouldBe(1);

            time.Advance(TimeSpan.FromMilliseconds(20));

            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 3 - True 0 7");
        });

    [Fact]
    public Task An_edit_that_leaves_the_caret_where_it_was_asks_again_after_the_pause() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);

            // As another editor of the document renames the call: same length, so the caret stays.
            document.Replace(new TextRange(0, 4), "ring");
            window.Editor.Selection.Caret.ShouldBe(5);
            time.Advance(s_pause);

            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 3 - True 0 5");
        });

    [Fact]
    public Task Up_and_Down_choose_the_active_signature_the_server_keeps_the_choice_and_an_open_list_takes_them() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument answer = await CompletionTests.SetCompletionAsync(
                server,
                fake,
                CompletionTests.Items("alpha", "beta")
            );
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await OpenAsync(window, document, time);

            window.Press(Key.Down);
            PopupFrame popup = await WaitForSignatureAsync(window, "first", "Calls with three.");
            ActiveSignature(popup).ShouldBe("call(first, second, third)");
            document.Text.ShouldBe("call(");

            // Asking again tells the server the signature that shows, and it keeps it.
            window.Type(" ,");
            time.Advance(s_pause);
            (await fake.WaitForAsync("signatureHelp ", 2)).ShouldEndWith(" 2 , True 1 7");
            await WaitForSignatureAsync(window, "second", "Calls with three.");

            window.Press(Key.Space, KeyModifiers.Control);
            await CompletionTests.WaitForLabelsAsync(window);
            window.Press(Key.Down);
            CompletionTests.List(window).ShouldNotBeNull().SelectedIndex.ShouldBe(1);
            ActiveSignature(Popup(window).ShouldNotBeNull()).ShouldBe("call(first, second, third)");
        });

    [Fact]
    public Task Several_signatures_show_which_one_is_active_between_buttons_that_choose_the_one_before_and_after() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);
            PopupFrame popup = Popup(window).ShouldNotBeNull();
            Texts(popup).ShouldContain("1/2");

            window.Click(CompletionTests.BoundsInWindow(Buttons(popup)[1]).Center, MouseButton.Left);

            popup = await WaitForSignatureAsync(window, "first", "Calls with three.");
            ActiveSignature(popup).ShouldBe("call(first, second, third)");
            Texts(popup).ShouldContain("2/2");
            // The editor keeps the keyboard and the text.
            window.Editor.IsKeyboardFocusWithin.ShouldBeTrue();
            document.Text.ShouldBe("call(");

            window.Click(CompletionTests.BoundsInWindow(Buttons(popup)[0]).Center, MouseButton.Left);

            popup = await WaitForSignatureAsync(window, "first", "Calls with two.");
            Texts(popup).ShouldContain("1/2");

            // One signature: nothing to choose among.
            var one = new JsonArray(
                new JsonObject
                {
                    ["label"] = "call(first)",
                    ["documentation"] = "Calls with one.",
                    ["parameters"] = new JsonArray(new JsonObject { ["label"] = "first" }),
                }
            );
            using CodeDocument command = server.CreateDocument(FakeServer.Commands, $"signatures {one.ToJsonString()}");
            await fake.WaitForAsync("set signatures");
            window.Press(Key.Space, EditorWindow.Command | KeyModifiers.Shift);
            popup = await WaitForSignatureAsync(window, "first", "Calls with one.");
            Buttons(popup).ShouldBeEmpty();
            Texts(popup).ShouldNotContain("1/1");
        });

    [Fact]
    public Task The_documentation_shows_right_below_the_active_signature() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);
            PopupFrame popup = Popup(window).ShouldNotBeNull();
            Rect two = CompletionTests.BoundsInWindow(Text(popup, "call(first, second)"));
            Rect three = CompletionTests.BoundsInWindow(Text(popup, "call(first, second, third)"));
            Rect documentation = CompletionTests.BoundsInWindow(Text(popup, "Calls with two."));
            documentation.Top.ShouldBeGreaterThan(two.Bottom);
            documentation.Bottom.ShouldBeLessThan(three.Top);

            window.Press(Key.Down);

            popup = await WaitForSignatureAsync(window, "first", "Calls with three.");
            three = CompletionTests.BoundsInWindow(Text(popup, "call(first, second, third)"));
            CompletionTests.BoundsInWindow(Text(popup, "Calls with three.")).Top.ShouldBeGreaterThan(three.Bottom);
        });

    [Fact]
    public Task Up_and_Down_go_around_the_signatures() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);

            // Two signatures: past the last, Down goes to the first, and before the first, Up to the last.
            window.Press(Key.Down);
            await WaitForSignatureAsync(window, "first", "Calls with three.");
            window.Press(Key.Down);
            PopupFrame popup = await WaitForSignatureAsync(window, "first", "Calls with two.");
            ActiveSignature(popup).ShouldBe("call(first, second)");
            window.Press(Key.Up);
            popup = await WaitForSignatureAsync(window, "first", "Calls with three.");
            ActiveSignature(popup).ShouldBe("call(first, second, third)");
            document.Text.ShouldBe("call(");
            window.Editor.Selection.ShouldBe(new TextSelection(5));
        });

    [Fact]
    public Task Alt_Up_and_Alt_Down_choose_the_signature_while_several_show_and_otherwise_move_lines() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "first\ncall");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);

            // Closed: the lines move.
            window.Editor.Selection = new TextSelection(10);
            window.Press(Key.Up, KeyModifiers.Alt);
            document.Text.ShouldBe("call\nfirst");
            window.Press(Key.Down, KeyModifiers.Alt);
            document.Text.ShouldBe("first\ncall");

            // Two signatures: around them, and the lines stay.
            window.Type("(");
            time.Advance(s_pause);
            await WaitForSignatureAsync(window, "first", "Calls with two.");
            window.Press(Key.Down, KeyModifiers.Alt);
            await WaitForSignatureAsync(window, "first", "Calls with three.");
            window.Press(Key.Down, KeyModifiers.Alt);
            await WaitForSignatureAsync(window, "first", "Calls with two.");
            window.Press(Key.Up, KeyModifiers.Alt);
            await WaitForSignatureAsync(window, "first", "Calls with three.");
            document.Text.ShouldBe("first\ncall(");

            // One signature: the lines move.
            var one = new JsonArray(
                new JsonObject
                {
                    ["label"] = "call(first)",
                    ["documentation"] = "Calls with one.",
                    ["parameters"] = new JsonArray(new JsonObject { ["label"] = "first" }),
                }
            );
            using CodeDocument command = server.CreateDocument(FakeServer.Commands, $"signatures {one.ToJsonString()}");
            await fake.WaitForAsync("set signatures");
            window.Press(Key.Space, EditorWindow.Command | KeyModifiers.Shift);
            await WaitForSignatureAsync(window, "first", "Calls with one.");
            window.Press(Key.Up, KeyModifiers.Alt);
            document.Text.ShouldBe("call(\nfirst");
        });

    [Fact]
    public Task Signature_help_shows_above_the_line_and_an_open_completion_list_below_it() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument answer = await CompletionTests.SetCompletionAsync(
                server,
                fake,
                CompletionTests.Items("alpha", "beta")
            );
            // Room for the signature above the line with any platform's fonts, and for the list below it.
            const string text = "\n\n\n\n\n\n\n\n\n\n\n\ncall";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            window.Editor.Selection = new TextSelection(text.Length);
            window.Type("(");
            time.Advance(s_pause);
            PopupFrame popup = await WaitForSignatureAsync(window, "first", "Calls with two.");

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
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            var button = new Button { Content = "Elsewhere" };
            DockPanel.SetDock(button, Dock.Top);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor, new DockPanel { Children = { button, editor } });
            await CompletionTests.StartServingAsync(server, document);

            await OpenAsync(window, document, time);
            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();

            // The server has none once the caret is after the closing parenthesis.
            await OpenAsync(window, document, time);
            window.Type(")");
            time.Advance(s_pause);
            await Wait.UntilAsync(() => Popup(window) is null);

            await OpenAsync(window, document, time);
            button.Focus();
            window.Layout();
            Popup(window).ShouldBeNull();

            editor.Focus();
            await OpenAsync(window, document, time);
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");
            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task A_click_that_moves_the_caret_ends_signature_help_and_a_key_that_moves_it_asks_again() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call(xy");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await CompletionTests.StartServingAsync(server, document);
            window.Editor.Selection = new TextSelection(7);
            window.Press(Key.Space, EditorWindow.Command | KeyModifiers.Shift);
            await WaitForSignatureAsync(window, "first", "Calls with two.");
            (await fake.WaitForAsync("signatureHelp ")).ShouldEndWith(" 1 - False - 7");

            // A key that moves the caret asks again after the pause, a click ends it.
            window.Press(Key.Left);
            time.Advance(s_pause);
            await fake.WaitForAsync("signatureHelp ", 2);
            Popup(window).ShouldNotBeNull();
            // Between "(" and "x": a quarter of a character right of their boundary.
            window.Click(window.PointAt(5) - new Point(window.TextView.WideSpaceWidth / 4, 0), MouseButton.Left);

            window.Editor.Selection.ShouldBe(new TextSelection(5));
            Popup(window).ShouldBeNull();
            time.Advance(s_pause);
            window.Layout();
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("signatureHelp ").Count.ShouldBe(2);
        });

    [Fact]
    public Task A_read_only_editor_ends_signature_help() =>
        _ui.Run(async () =>
        {
            var time = new ManualTime();
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "call");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await CompletionTests.StartServingAsync(server, document);
            await OpenAsync(window, document, time);

            editor.IsReadOnly = true;

            Popup(window).ShouldBeNull();
        });

    /// <summary>Opens signature help on "call" anew: types "(" after it, and lets the pause pass.</summary>
    private static async Task OpenAsync(EditorWindow window, CodeDocument document, ManualTime time)
    {
        document.Text = "call";
        window.Editor.Selection = new TextSelection(4);
        window.Type("(");
        time.Advance(s_pause);
        await WaitForSignatureAsync(window, "first", "Calls with two.");
    }

    /// <summary>
    /// Waits for the popup with <paramref name="documentation"/>, whose first signature's bold parameter is
    /// <paramref name="active"/>.
    /// </summary>
    private static async Task<PopupFrame> WaitForSignatureAsync(
        EditorWindow window,
        string active,
        string documentation
    )
    {
        PopupFrame? popup = null;
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
    private static PopupFrame? Popup(EditorWindow window) => PopupFrames.Find(window, "SignatureHelp");

    /// <summary>The first signature.</summary>
    private static TextBlock Signature(PopupFrame popup) =>
        popup.GetVisualDescendants().OfType<TextBlock>().First(t => t.Inlines?.Text?.StartsWith("call(") == true);

    private static IEnumerable<Run> Runs(PopupFrame popup) => Signature(popup).Inlines?.OfType<Run>() ?? [];

    /// <summary>The text block that shows <paramref name="text"/>.</summary>
    private static TextBlock Text(PopupFrame popup, string text) =>
        popup.GetVisualDescendants().OfType<TextBlock>().First(t => (t.Inlines?.Text ?? t.Text) == text);

    /// <summary>The buttons that show: the one that chooses the signature before, then the one after.</summary>
    private static IReadOnlyList<PopupButton> Buttons(PopupFrame popup) =>
        [.. popup.GetVisualDescendants().OfType<PopupButton>().Where(button => button.IsEffectivelyVisible)];

    /// <summary>The texts that show.</summary>
    private static IReadOnlyList<string> Texts(PopupFrame popup) =>
        [
            .. popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Inlines?.Text ?? t.Text ?? ""),
        ];

    /// <summary>The label of the signature marked active, on a background of its own; null when none is marked.</summary>
    private static string? ActiveSignature(PopupFrame popup) =>
        popup
            .GetVisualDescendants()
            .OfType<Border>()
            .Where(border => border.Background is not null)
            .Select(border => border.Child)
            .OfType<TextBlock>()
            .Select(text => text.Inlines?.Text)
            .SingleOrDefault();
}
