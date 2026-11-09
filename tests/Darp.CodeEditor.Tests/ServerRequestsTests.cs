using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Darp.CodeEditor.LanguageClient;

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

            // The fake server's request 1 asks for its own section when it gets all settings at the start.
            (await fake.WaitForAsync("response 2 ")).ShouldBe("""response 2 result [{"validate":true}]""");
            (await fake.WaitForAsync("response 3 ")).ShouldBe("response 3 result [null]");
            // Requests run side by side, in any order.
            requests.ShouldContain((new LanguageServerSettingsRequest("yaml", path), false));
            requests.ShouldContain((new LanguageServerSettingsRequest("other", null), false));
        });

    [Fact]
    public Task All_settings_reach_the_server_when_it_starts_and_after_a_restart() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            var requests = new ConcurrentQueue<(LanguageServerSettingsRequest Request, bool OnUiThread)>();
            using var server = new LanguageServer(
                fake.Options with
                {
                    TimeProvider = time,
                    Settings = request =>
                    {
                        requests.Enqueue((request, Dispatcher.UIThread.CheckAccess()));
                        return request.Section is null
                            ? new JsonObject { ["json"] = new JsonObject { ["validate"] = true } }
                            : null;
                    },
                }
            );
            const string pushed = """configuration changed {"settings":{"json":{"validate":true}}}""";

            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "text");

            (await fake.WaitForAsync("configuration changed")).ShouldBe(pushed);
            string opened = await fake.WaitForAsync("open ");
            // After initialized, and before the documents, which the server may check against its settings.
            List<string> lines = [.. fake.Lines];
            lines.IndexOf("initialized").ShouldBeLessThan(lines.IndexOf(pushed));
            lines.IndexOf(pushed).ShouldBeLessThan(lines.IndexOf(opened));

            CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 1");
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            crash.Dispose();
            time.Advance(TimeSpan.FromSeconds(0.5));

            (await fake.WaitForAsync("configuration changed", 2)).ShouldBe(pushed);
            requests.Count(r => r.Request == new LanguageServerSettingsRequest(null, null)).ShouldBe(2);
            requests.ShouldAllBe(r => !r.OnUiThread);
        });

    [Fact]
    public Task NotifySettingsChanged_sends_all_settings_again_and_the_server_asks_again() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            int version = 0;
            await using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request => new JsonObject { ["section"] = request.Section, ["version"] = version },
                }
            );
            await Wait.ForAsync(server.StartAsync());
            (await fake.WaitForAsync("configuration changed")).ShouldBe(
                """configuration changed {"settings":{"section":null,"version":0}}"""
            );
            await fake.WaitForAsync("response 1 ");

            version = 1;
            server.NotifySettingsChanged();

            (await fake.WaitForAsync("configuration changed", 2)).ShouldBe(
                """configuration changed {"settings":{"section":null,"version":1}}"""
            );
            (await fake.WaitForAsync("response 2 ")).ShouldBe("""response 2 result [{"section":"fake","version":1}]""");
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Without_settings_the_server_gets_an_empty_object_not_null(bool settingsAnswerNull) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(
                settingsAnswerNull ? fake.Options with { Settings = _ => null } : fake.Options
            );

            await Wait.ForAsync(server.StartAsync());

            (await fake.WaitForAsync("configuration changed")).ShouldBe("""configuration changed {"settings":{}}""");
        });

    [Fact]
    public Task DisposeAsync_completes_while_the_apps_settings_block_the_start() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var entered = new TaskCompletionSource();
            var release = new ManualResetEventSlim();
            var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is null)
                        {
                            entered.TrySetResult();
                            release.Wait();
                        }
                        return null;
                    },
                }
            );
            try
            {
                await Wait.ForAsync(server.StartAsync());
                await Wait.ForAsync(entered.Task);

                await Wait.ForAsync(server.DisposeAsync().AsTask());

                server.State.ShouldBe(LanguageServerState.Stopped);
                FakeServer.HasEnded(fake.ProcessIds[0]).ShouldBeTrue();
            }
            finally
            {
                release.Set();
            }
        });

    [Fact]
    public Task StartAsync_completes_while_the_starts_settings_block_and_documents_wait_for_them() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var release = new ManualResetEventSlim();
            using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is null)
                            release.Wait();
                        return null;
                    },
                }
            );
            string path = Path.Combine(AppContext.BaseDirectory, "waiting.txt");
            try
            {
                using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "before");

                await Wait.ForAsync(server.StartAsync());

                server.State.ShouldBe(LanguageServerState.Running);
                await fake.WaitForAsync("initialized");
                // While the settings are computed, no edit and no new path reaches the server: the didOpen that
                // follows the settings takes them.
                document.Replace(new TextRange(6, 0), " and after");
                document.FilePath = path;
                fake.LinesStartingWith("open ").ShouldBeEmpty();
                release.Set();
                await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "before and after" }]);
                List<string> lines = [.. fake.Lines];
                string opened = $"open {new Uri(path).AbsoluteUri} plaintext {document.CurrentSnapshot.Version}";
                lines.Where(line => line.StartsWith("open ")).ShouldBe([opened]);
                lines.IndexOf("configuration changed {\"settings\":{}}").ShouldBeLessThan(lines.IndexOf(opened));
                lines.ShouldNotContain(line => line.StartsWith("change ") || line.StartsWith("close "));
            }
            finally
            {
                release.Set();
            }
        });

    [Fact]
    public Task Editors_ask_a_server_only_once_the_document_is_open_in_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var release = new ManualResetEventSlim();
            using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is null)
                            release.Wait();
                        return null;
                    },
                }
            );
            try
            {
                using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "some words");
                int changes = 0;
                document.LanguageFeaturesChanged += (_, _) => changes++;
                await Wait.ForAsync(server.StartAsync());
                await fake.WaitForAsync("initialized");

                // Running, but the document is not open in the process until the start's settings went out: editors
                // get nothing, the server hears nothing of it, and nothing tells editors that it serves the document.
                (await Hover(document)).ShouldBeNull();
                (await Tokens(document)).ShouldBeNull();
                Dispatcher.UIThread.RunJobs();
                changes.ShouldBe(0);
                fake.Lines.ShouldNotContain(line => line.StartsWith("hover ") || line.StartsWith("semanticTokens"));

                release.Set();

                await Wait.UntilAsync(() => changes == 1);
                (await Hover(document)).ShouldNotBeNull();
                (await Tokens(document)).ShouldNotBeNull();
                List<string> lines = [.. fake.Lines];
                lines
                    .FindIndex(line => line.StartsWith("hover "))
                    .ShouldBeGreaterThan(lines.FindIndex(line => line.StartsWith("open ")));
                changes.ShouldBe(1);
                fake.ProcessIds.ShouldHaveSingleItem();
            }
            finally
            {
                release.Set();
            }

            static Task<HoverInfo?> Hover(CodeDocument document) =>
                document.HoverAsync(document.CurrentSnapshot, new TextPosition(1, 1), CancellationToken.None);

            static Task<SemanticTokenLines?> Tokens(CodeDocument document) =>
                document.SemanticTokensAsync(
                    new TextPosition(1, 1),
                    new TextPosition(1, 11),
                    1,
                    CancellationToken.None
                );
        });

    [Fact]
    public Task Settings_that_block_hold_back_no_text_changes() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var entered = new TaskCompletionSource();
            var release = new ManualResetEventSlim();
            bool block = false;
            using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is null && Volatile.Read(ref block))
                        {
                            entered.TrySetResult();
                            release.Wait();
                        }
                        return null;
                    },
                }
            );
            try
            {
                using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "before");
                await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "before" }]);
                Volatile.Write(ref block, true);
                server.NotifySettingsChanged();
                await Wait.ForAsync(entered.Task);

                document.Replace(new TextRange(6, 0), " and after");

                await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "before and after" }]);
            }
            finally
            {
                release.Set();
            }
        });

    [Fact]
    public Task Changes_while_the_settings_are_computed_make_one_more_computation() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var release = new ManualResetEventSlim();
            int calls = 0;
            using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is not null)
                            return null;
                        int call = Interlocked.Increment(ref calls);
                        // The start's answer comes at once; later ones wait for the test.
                        if (call > 1)
                            release.Wait();
                        return new JsonObject { ["call"] = call };
                    },
                }
            );
            try
            {
                await Wait.ForAsync(server.StartAsync());
                await fake.WaitForAsync("configuration changed");
                server.NotifySettingsChanged();
                await Wait.UntilAsync(() => Volatile.Read(ref calls) == 2);

                for (int i = 0; i < 10; i++)
                    server.NotifySettingsChanged();
                release.Set();

                (await fake.WaitForAsync("configuration changed", 3)).ShouldBe(
                    """configuration changed {"settings":{"call":3}}"""
                );
                Volatile.Read(ref calls).ShouldBe(3);
            }
            finally
            {
                release.Set();
            }
        });

    [Fact]
    public Task Settings_that_throw_drop_only_their_own_push() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            int calls = 0;
            using var server = new LanguageServer(
                fake.Options with
                {
                    Settings = request =>
                    {
                        if (request.Section is not null)
                            return null;
                        int call = Interlocked.Increment(ref calls);
                        return call == 2
                            ? throw new InvalidOperationException("The app's settings failed.")
                            : new JsonObject { ["call"] = call };
                    },
                }
            );
            await Wait.ForAsync(server.StartAsync());
            await fake.WaitForAsync("configuration changed");

            server.NotifySettingsChanged();
            await Wait.UntilAsync(() => Volatile.Read(ref calls) == 2);
            server.NotifySettingsChanged();

            (await fake.WaitForAsync("configuration changed", 2)).ShouldBe(
                """configuration changed {"settings":{"call":3}}"""
            );
            server.State.ShouldBe(LanguageServerState.Running);
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

            // The fake server's request 1 asks for its own section when it gets all settings at the start.
            (await fake.WaitForAsync("response 2 ")).ShouldBe("response 2 result null");
            (await fake.WaitForAsync("response 3 ")).ShouldBe("response 3 result null");
            (await fake.WaitForAsync("response 4 ")).ShouldBe("response 4 error -32601");
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
