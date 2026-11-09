using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace Darp.CodeEditor.Tests;

/// <summary>What a server asks of the client, and messages that the client cannot read.</summary>
[Collection(UiFixture.Collection)]
public sealed class ServerRequestsTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Settings_answer_configuration_requests_off_the_UI_thread() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var requests = new ConcurrentQueue<(LanguageServerSettingsRequest Request, bool OnUiThread)>();
            await using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        requests.Enqueue((request, Dispatcher.UIThread.CheckAccess()));
                        return request.Section == "yaml" ? new JsonObject { ["validate"] = true } : null;
                    },
                }
            );
            string path = Path.Combine(AppContext.BaseDirectory, "settings.yaml");

            using CodeDocument command = server.CreateDocument(
                FakeServer.Commands,
                $"configuration yaml {new Uri(path).AbsoluteUri}\nconfiguration other"
            );

            (await fake.WaitForAsync("response 1 ")).ShouldBe("""response 1 result [{"validate":true}]""");
            (await fake.WaitForAsync("response 2 ")).ShouldBe("response 2 result [null]");
            // Requests run side by side, in any order.
            requests.ShouldBe(
                [
                    (new LanguageServerSettingsRequest("yaml", path), false),
                    (new LanguageServerSettingsRequest("other", null), false),
                ],
                ignoreOrder: true
            );
        });

    [Fact]
    public Task NotifySettingsChanged_makes_the_server_ask_again() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request => new JsonObject { ["section"] = request.Section },
                }
            );
            await server.StartAsync();

            server.NotifySettingsChanged();

            (await fake.WaitForAsync("configuration changed")).ShouldBe("""configuration changed {"settings":null}""");
            (await fake.WaitForAsync("response 1 ")).ShouldBe("""response 1 result [{"section":"fake"}]""");
        });

    [Fact]
    public Task Registrations_are_acknowledged_and_unknown_requests_refused() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);

            using CodeDocument command = server.CreateDocument(
                FakeServer.Commands,
                "register\nunregister\nunknown-request"
            );

            (await fake.WaitForAsync("response 1 ")).ShouldBe("response 1 result null");
            (await fake.WaitForAsync("response 2 ")).ShouldBe("response 2 result null");
            (await fake.WaitForAsync("response 3 ")).ShouldBe("response 3 error -32601");
        });

    [Fact]
    public Task Malformed_JSON_from_the_server_is_answered_with_an_error_and_the_server_runs_on() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);

            using CodeDocument command = server.CreateDocument(FakeServer.Commands, "malformed");

            (await fake.WaitForAsync("response null ")).ShouldBe("response null error -32700");
            server.State.ShouldBe(LanguageServerState.Running);
        });

    [Fact]
    public Task A_message_over_the_size_limit_ends_the_process_as_a_crash() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            await server.StartAsync();

            using CodeDocument command = server.CreateDocument(FakeServer.Commands, "oversized");

            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            FakeServer.HasEnded(fake.ProcessIds[0]).ShouldBeTrue();
        });
}
