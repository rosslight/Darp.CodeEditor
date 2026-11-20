using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// When the hover asks and shows, on the editor's clock: the server halfway through the delay, what is known once it is
/// over, "Loading…" at three times it, and a request that is no longer wanted cancelled.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class HoverTimingTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task The_server_is_asked_halfway_through_the_hover_delay_and_its_hover_shows_once_the_delay_is_over() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            editor.Options.Hover.Delay.ShouldBe(TimeSpan.FromMilliseconds(300));

            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(149));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            await fake.WaitForAsync("hover ");
            // Answered, but the delay is not over: nothing shows yet.
            await Wait.UntilAsync(() => editor.HoverIsAnswered);
            window.Render();
            Popup(window).ShouldBeNull();

            Pass(window, time, TimeSpan.FromMilliseconds(150));

            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Texts(popup).ShouldContain($"{text} (echo)");
        });

    [Fact]
    public Task The_wait_starts_again_on_another_character_and_Loading_shows_while_the_server_is_late() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow");
            var editor = new CodeEditor
            {
                Document = document,
                Options = CodeEditorOptions.Default with
                {
                    Hover = new HoverOptions { Delay = TimeSpan.FromSeconds(1) },
                },
            };
            using var window = new EditorWindow(editor);
            await server.StartAsync();

            // Within a character, the wait goes on; on another one, it starts again.
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            window.MoveMouse(window.PointAt(1) + new Point(1, 0));
            Pass(window, time, TimeSpan.FromMilliseconds(100));
            window.MoveMouse(window.PointAt(2));
            Pass(window, time, TimeSpan.FromMilliseconds(499));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];

            // The server never answers for "slow": at three times the delay, the hover says so.
            Pass(window, time, TimeSpan.FromMilliseconds(2499));
            window.Render();
            Popup(window).ShouldBeNull();
            Pass(window, time, TimeSpan.FromMilliseconds(1));
            PopupFrame popup = await WaitForPopupAsync(window, "Loading…");
            Texts(popup).ShouldBe(["Loading…"]);

            window.Press(Key.Escape);
            Popup(window).ShouldBeNull();
            await fake.WaitForAsync($"cancel {id}");
            fake.LinesStartingWith("hover ").Count.ShouldBe(1);
        });

    [Fact]
    public void The_hover_delay_is_between_0_and_10_seconds()
    {
        new HoverOptions().Delay.ShouldBe(TimeSpan.FromMilliseconds(300));
        new HoverOptions { Delay = TimeSpan.FromMilliseconds(-5) }.Delay.ShouldBe(TimeSpan.Zero);
        new HoverOptions { Delay = TimeSpan.FromMinutes(1) }.Delay.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public Task Escape_ends_a_hover_that_still_waits_for_its_server() =>
        _ui.Run(async () =>
        {
            // Without diagnostics, nothing shows until the server answers, and it never answers for "slow".
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "slow");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];
            Popup(window).ShouldBeNull();

            window.Press(Key.Escape);

            // Its request is cancelled, so that a late answer opens nothing.
            await fake.WaitForAsync($"cancel {id}");
            Popup(window).ShouldBeNull();
        });

    [Fact]
    public Task A_server_that_stops_serving_the_document_ends_its_hover() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "word");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            await HoverAsync(window, 1, "word is a word in", time);

            // The process crashes: its information and its diagnostics' messages are outdated.
            using CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");

            await Wait.UntilAsync(() => Popup(window) is null);
        });

    [Fact]
    public Task An_outdated_hover_request_is_cancelled() =>
        _ui.Run(async () =>
        {
            // Without diagnostics, nothing shows for "slow", so that leaving it ends its hover at once.
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "slow fast";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();

            // The server never answers for "slow".
            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(300));
            string id = (await fake.WaitForAsync("hover "))["hover ".Length..].Split(' ')[0];
            (await fake.WaitForAsync("hover ")).ShouldEndWith(" slow");

            await HoverAsync(window, text.IndexOf("fast", StringComparison.Ordinal), "fast is a word in", time);
            await fake.WaitForAsync($"cancel {id}");
        });
}
