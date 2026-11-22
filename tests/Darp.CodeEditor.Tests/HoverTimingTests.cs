using Avalonia.Controls;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>When the hover asks and shows, on the document's clock: all of it once the pointer has rested for the delay.</summary>
[Collection(UiFixture.Collection)]
public sealed class HoverTimingTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task The_server_is_asked_and_the_hover_shows_when_the_delay_ends() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            window.MoveMouse(window.PointAt(1));
            Pass(window, time, Delay - TimeSpan.FromMilliseconds(1));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();
            Popup(window).ShouldBeNull();

            Pass(window, time, TimeSpan.FromMilliseconds(1));

            PopupFrame popup = await WaitForPopupAsync(window, "one is a word in");
            Texts(popup).ShouldContain($"{text} (echo)");
            fake.LinesStartingWith("hover ").ShouldHaveSingleItem();
        });

    [Fact]
    public Task The_next_words_hover_shows_after_one_delay() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();
            await HoverAsync(window, 0, "one is a word in", time);

            window.MoveMouse(window.PointAt(4));
            Pass(window, time, Delay);

            await WaitForPopupAsync(window, "two is a word in");
        });

    [Fact]
    public Task The_wait_starts_again_on_another_character() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--no-diagnostics");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await server.StartAsync();

            window.MoveMouse(window.PointAt(1));
            Pass(window, time, TimeSpan.FromMilliseconds(200));
            window.MoveMouse(window.PointAt(2));
            Pass(window, time, TimeSpan.FromMilliseconds(200));
            await CompletionTests.BarrierAsync(server, fake);
            fake.LinesStartingWith("hover ").ShouldBeEmpty();

            Pass(window, time, TimeSpan.FromMilliseconds(100));

            await WaitForPopupAsync(window, "one is a word in");
        });

    [Fact]
    public Task The_servers_answer_below_the_diagnostics_keeps_their_selection() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using CodeDocument commands = server.CreateDocument(FakeServer.Commands, "hold-hovers");
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await fake.WaitForAsync("holding hovers");
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            PopupFrame popup = await HoverAsync(window, 1, $"{text} (echo)", time);
            await fake.WaitForAsync("hover ");
            SelectableTextBlock message = popup.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            message.SelectAll();

            commands.Text = "release-hovers";

            await WaitForPopupAsync(window, "one is a word in");
            message.SelectedText.ShouldBe($"{text} (echo)");
            TopLevel.GetTopLevel(message).ShouldNotBeNull();
        });
}
