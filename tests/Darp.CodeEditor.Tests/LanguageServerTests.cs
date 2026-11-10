using System.Text.Json.Nodes;
using Avalonia.Threading;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.Tests;

/// <summary>A language server's life: its start, restarts after crashes and hangs, failure, and its end.</summary>
[Collection(UiFixture.Collection)]
public sealed class LanguageServerTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task State_goes_through_start_crash_restart_and_dispose_and_is_raised_on_the_UI_thread() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            var server = new LanguageServer(fake.Options with { TimeProvider = time });
            var raised = new List<bool>();
            server.PropertyChanged += (_, e) =>
            {
                e.PropertyName.ShouldBe(nameof(LanguageServer.State));
                raised.Add(Dispatcher.UIThread.CheckAccess());
            };
            server.State.ShouldBe(LanguageServerState.NotStarted);

            Task start = server.StartAsync();
            server.State.ShouldBe(LanguageServerState.Starting);
            await start;
            server.State.ShouldBe(LanguageServerState.Running);

            CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 3");
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            crash.Dispose();
            time.Advance(TimeSpan.FromSeconds(0.5));
            server.State.ShouldBe(LanguageServerState.Starting);
            await Wait.UntilAsync(() => server.State == LanguageServerState.Running);

            await server.DisposeAsync();
            server.State.ShouldBe(LanguageServerState.Stopped);

            // Starting, Running, Restarting, Starting, Running, Stopped: one event each.
            await Wait.UntilAsync(() => raised.Count == 6);
            raised.ShouldAllBe(onUiThread => onUiThread);
        });

    [Fact]
    public Task The_first_document_starts_the_server() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);

            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "text");

            server.State.ShouldBe(LanguageServerState.Starting);
            await Wait.UntilAsync(() => server.State == LanguageServerState.Running);
            await server.StartAsync();
            fake.ProcessIds.Count.ShouldBe(1);
        });

    [Fact]
    public Task StartAsync_throws_why_the_process_ended_with_the_end_of_its_standard_error() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--stderr", "Cannot read the configuration.", "--exit-at-start", "3");
            using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                server.StartAsync()
            );

            exception.Message.ShouldContain("exited with code 3 before it answered 'initialize'");
            exception.Message.ShouldContain("Cannot read the configuration.");
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
        });

    [Fact]
    public Task StartAsync_throws_when_the_executable_does_not_start() =>
        _ui.Run(async () =>
        {
            using var server = new LanguageServer(
                new LanguageServerOptions
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "no-such-server"),
                    TimeProvider = new ManualTime(),
                }
            );

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
                server.StartAsync()
            );

            exception.Message.ShouldContain("did not start");
        });

    [Fact]
    public Task A_crashing_server_restarts_after_growing_delays_and_fails_after_five_tries() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--exit-at-start", "1");
            var time = new ManualTime();
            using var server = new LanguageServer(fake.Options with { TimeProvider = time });

            await FailFiveTimesAsync(server, fake, time);

            // Documents start no process now.
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "text");
            server.State.ShouldBe(LanguageServerState.Failed);
            time.Advance(TimeSpan.FromMinutes(1));
            server.State.ShouldBe(LanguageServerState.Failed);
            fake.ProcessIds.Count.ShouldBe(5);
        });

    [Fact]
    public Task StartAsync_after_Failed_starts_a_new_process() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--exit-at-start", "1");
            var time = new ManualTime();
            using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            await FailFiveTimesAsync(server, fake, time);

            await Should.ThrowAsync<InvalidOperationException>(server.StartAsync());

            fake.ProcessIds.Count.ShouldBe(6);
            // A new series of tries, from the first delay.
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            time.Advance(TimeSpan.FromSeconds(0.5));
            server.State.ShouldBe(LanguageServerState.Starting);
        });

    [Fact]
    public Task A_server_that_does_not_answer_initialize_restarts_with_longer_limits_up_to_two_minutes() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--initialize", "never");
            var time = new ManualTime();
            using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            Task start = server.StartAsync();
            await fake.WaitForAsync("initialize ");

            // A new server gets twice the 30 s limit.
            time.Advance(TimeSpan.FromSeconds(59));
            server.State.ShouldBe(LanguageServerState.Starting);
            time.Advance(TimeSpan.FromSeconds(1));
            (await Should.ThrowAsync<InvalidOperationException>(start)).Message.ShouldContain(
                "did not answer 'initialize' within 60 s"
            );
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            await Wait.UntilAsync(() => FakeServer.HasEnded(fake.ProcessIds[0]));

            // The hang doubled the limit.
            time.Advance(TimeSpan.FromSeconds(0.5));
            await fake.WaitForAsync("initialize ", 2);
            time.Advance(TimeSpan.FromSeconds(119));
            server.State.ShouldBe(LanguageServerState.Starting);
            time.Advance(TimeSpan.FromSeconds(1));
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);

            // Doubled again it would be 240 s, but no start waits longer than 2 minutes.
            time.Advance(TimeSpan.FromSeconds(1));
            await fake.WaitForAsync("initialize ", 3);
            time.Advance(TimeSpan.FromSeconds(119));
            server.State.ShouldBe(LanguageServerState.Starting);
            time.Advance(TimeSpan.FromSeconds(1));
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
        });

    [Fact]
    public Task A_capability_of_another_JSON_kind_than_the_protocol_says_means_its_default() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--text-document-sync", """{ "change": "full" }""");
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one");

            await server.StartAsync();
            // An edit made before the didOpen is written joins it, and no change goes out.
            await fake.WaitForAsync("open ");
            document.Replace(new TextRange(3, 0), " two");

            (await fake.WaitForAsync("change ")).ShouldEndWith(" ranged 1 full 0");
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_server_that_stops_reading_while_documents_come_and_go_ends_as_a_crash(bool move) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            using var server = new LanguageServer(fake.Options with { TimeProvider = new ManualTime() });
            await Wait.ForAsync(server.StartAsync());
            using CodeDocument command = server.CreateDocument(FakeServer.Commands, "stop-reading");
            await fake.WaitForAsync("not reading");
            // More than a pipe holds: writing its didOpen blocks, and what comes after waits.
            using CodeDocument large = server.CreateDocument(CodeLanguage.PlainText, new string('x', 4_000_000));
            using CodeDocument moving = server.CreateDocument(CodeLanguage.PlainText, "text");

            // Each round queues a message or two that the server never reads, while no more documents stay open.
            for (
                int i = 0;
                i < 2 * JsonRpcConnection.DefaultMaxWaitingMessages && server.State == LanguageServerState.Running;
                i++
            )
            {
                if (move)
                    moving.FilePath = Path.Combine(AppContext.BaseDirectory, $"moving-{i % 2}.txt");
                else
                    server.CreateDocument(CodeLanguage.PlainText, "text").Dispose();
            }

            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
        });

    [Fact]
    public Task An_unexpected_failure_while_starting_counts_as_a_failed_start() =>
        _ui.Run(async () =>
        {
            // No URI holds a path with a null character: the start fails after the process started.
            var fake = new FakeServer();
            var server = new LanguageServer(
                fake.Options with
                {
                    RootPath = "invalid\0path",
                    TimeProvider = new ManualTime(),
                }
            );

            // A cap, not a latency: a start that never ends would leave the state machine stuck.
            await Should.ThrowAsync<InvalidOperationException>(
                server.StartAsync().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
            );

            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            await server.DisposeAsync();
            server.State.ShouldBe(LanguageServerState.Stopped);
        });

    [Fact]
    public Task A_restarted_server_opens_the_documents_again_with_their_current_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "before");
            await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "before" }]);

            CodeDocument crash = server.CreateDocument(FakeServer.Commands, "exit 1");
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            // The diagnostics went with the process.
            await Wait.UntilAsync(() => document.Diagnostics.Count == 0);
            crash.Dispose();
            document.Text = "after";
            time.Advance(TimeSpan.FromSeconds(0.5));

            await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "after" }]);
            fake.LinesStartingWith("open ").Count(line => line.Contains(" plaintext ")).ShouldBe(2);
            fake.ProcessIds.Count.ShouldBe(2);
        });

    [Fact]
    public Task DisposeAsync_shuts_the_server_down_and_documents_keep_their_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var server = new LanguageServer(fake.Options);
            CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "text");
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            await server.DisposeAsync();

            server.State.ShouldBe(LanguageServerState.Stopped);
            fake.Lines.ShouldContain("shutdown");
            fake.Lines.ShouldContain("exit");
            FakeServer.HasEnded(fake.ProcessIds[0]).ShouldBeTrue();
            await Wait.UntilAsync(() => document.Diagnostics.Count == 0);
            document.Replace(new TextRange(4, 0), "!");
            document.Text.ShouldBe("text!");
            document.Dispose();
            Should.Throw<ObjectDisposedException>(() => server.CreateDocument(CodeLanguage.PlainText));
        });

    [Fact]
    public Task DisposeAsync_kills_a_server_that_does_not_shut_down_in_time() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--ignore-shutdown");
            var time = new ManualTime();
            var server = new LanguageServer(fake.Options with { TimeProvider = time });
            await server.StartAsync();

            Task dispose = server.DisposeAsync().AsTask();
            await fake.WaitForAsync("shutdown");
            dispose.IsCompleted.ShouldBeFalse();
            time.Advance(TimeSpan.FromSeconds(2));
            await dispose;

            FakeServer.HasEnded(fake.ProcessIds[0]).ShouldBeTrue();
            fake.Lines.ShouldNotContain("exit");
        });

    [Fact]
    public Task Dispose_kills_the_server_at_once() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            var server = new LanguageServer(fake.Options);
            await server.StartAsync();

            server.Dispose();

            server.State.ShouldBe(LanguageServerState.Stopped);
            await Wait.UntilAsync(() => FakeServer.HasEnded(fake.ProcessIds[0]));
            fake.Lines.ShouldNotContain("shutdown");
            await Should.ThrowAsync<ObjectDisposedException>(server.StartAsync());
        });

    [Fact]
    public Task The_server_starts_as_the_options_say_and_gets_only_the_capabilities_the_client_implements() =>
        _ui.Run(async () =>
        {
            string root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            string workingDirectory = Path.GetDirectoryName(root) ?? root;
            var fake = new FakeServer();
            await using var server = new LanguageServer(
                fake.Options with
                {
                    RootPath = root,
                    WorkingDirectory = workingDirectory,
                    EnvironmentVariables = new Dictionary<string, string?> { ["FAKE_SERVER_VARIABLE"] = "set" },
                    InitializationOptions = new JsonObject { ["answer"] = 42 },
                }
            );

            await server.StartAsync();

            fake.Lines.ShouldContain($"cwd {workingDirectory}");
            fake.Lines.ShouldContain("env set");
            JsonNode initialize =
                JsonNode.Parse((await fake.WaitForAsync("initialize "))["initialize ".Length..])
                ?? throw new InvalidDataException();
            ((int?)initialize["processId"]).ShouldBe(Environment.ProcessId);
            ((string?)initialize["rootUri"]).ShouldBe(new Uri(root).AbsoluteUri);
            ((string?)initialize["workspaceFolders"]?[0]?["uri"]).ShouldBe(new Uri(root).AbsoluteUri);
            ((int?)initialize["initializationOptions"]?["answer"]).ShouldBe(42);
            JsonNode
                .DeepEquals(
                    initialize["capabilities"],
                    JsonNode.Parse(
                        """
                        {
                          "workspace": { "configuration": true, "didChangeConfiguration": { "dynamicRegistration": false } },
                          "textDocument": {
                            "synchronization": { "dynamicRegistration": false },
                            "publishDiagnostics": { "versionSupport": true },
                            "hover": { "contentFormat": ["markdown", "plaintext"] },
                            "completion": {
                              "completionItem": {
                                "snippetSupport": true,
                                "commitCharactersSupport": true,
                                "documentationFormat": ["markdown", "plaintext"],
                                "deprecatedSupport": true,
                                "preselectSupport": true,
                                "tagSupport": { "valueSet": [1] },
                                "insertReplaceSupport": true,
                                "resolveSupport": { "properties": ["detail", "documentation", "additionalTextEdits"] }
                              },
                              "completionItemKind": {
                                "valueSet": [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25]
                              },
                              "contextSupport": true
                            },
                            "signatureHelp": {
                              "signatureInformation": {
                                "documentationFormat": ["markdown", "plaintext"],
                                "parameterInformation": { "labelOffsetSupport": true },
                                "activeParameterSupport": true
                              },
                              "contextSupport": true
                            },
                            "semanticTokens": {
                              "requests": { "range": true, "full": true },
                              "tokenTypes": [
                                "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter",
                                "variable", "property", "enumMember", "event", "function", "method", "macro", "keyword",
                                "modifier", "comment", "string", "number", "regexp", "operator", "decorator", "label"
                              ],
                              "tokenModifiers": [
                                "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async",
                                "modification", "documentation", "defaultLibrary"
                              ],
                              "formats": ["relative"],
                              "augmentsSyntaxTokens": true
                            }
                          }
                        }
                        """
                    )
                )
                .ShouldBeTrue(initialize["capabilities"]?.ToJsonString());
            await fake.WaitForAsync("initialized");
        });

    [Fact]
    public Task Standard_error_reaches_the_app_line_by_line() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);

            using CodeDocument command = server.CreateDocument(FakeServer.Commands, "stderr a message\nstderr another");

            await fake.WaitForAsync("another");
            fake.Lines.ShouldContain("a message");
        });

    /// <summary>Starts a server that exits at once, and lets it fail five times in a row, checking the delays.</summary>
    private static async Task FailFiveTimesAsync(LanguageServer server, FakeServer fake, ManualTime time)
    {
        await Should.ThrowAsync<InvalidOperationException>(server.StartAsync());
        foreach (double delay in (double[])[0.5, 1, 2, 4])
        {
            await Wait.UntilAsync(() => server.State == LanguageServerState.Restarting);
            int started = fake.ProcessIds.Count;
            time.Advance(TimeSpan.FromSeconds(delay) - TimeSpan.FromMilliseconds(1));
            server.State.ShouldBe(LanguageServerState.Restarting);
            time.Advance(TimeSpan.FromMilliseconds(1));
            server.State.ShouldBe(LanguageServerState.Starting);
            await Wait.UntilAsync(() => fake.ProcessIds.Count == started + 1);
        }
        await Wait.UntilAsync(() => server.State == LanguageServerState.Failed);
    }
}
