using System.Diagnostics;
using Darp.CodeEditor.CSharpScript.Runner;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Tests;
using Microsoft.Extensions.Logging;
using ScriptTests.Api;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using UseCases.Communication;
using UseCases.Failures;
using UseCases.Headless;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace Darp.CodeEditor.CSharpScript.Tests;

/// <summary>Real sessions in real runner processes. Races are forced with gates in the transport or the app side.</summary>
[Collection(ScriptServerFixture.Collection)]
public sealed class ScriptSessionTests
{
    private readonly CSharpScriptServer _server;

    public ScriptSessionTests(ScriptServerFixture fixture)
    {
        Assert.SkipUnless(ScriptServerFixture.HasServer, "This app has no C# helpers next to it.");
        _server = fixture.Server;
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Task<CSharpScriptAssembly<ScriptGlobals>> Compile(string code) =>
        CompileAs(ScriptEnvironments.Default, code);

    private async Task<CSharpScriptAssembly<TGlobals>> CompileAs<TGlobals>(
        CSharpScriptEnvironment<TGlobals> environment,
        string code
    )
        where TGlobals : class
    {
        CSharpScriptCompilation<TGlobals> compilation = await _server.CompileAsync(environment, code, Token);
        return compilation.Script.ShouldNotBeNull(string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
    }

    private static Task<CSharpScriptSession<ISessionSide, Session>> Start(
        TestApp? app = null,
        CSharpScriptSessionOptions? options = null
    ) => CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(app ?? new TestApp(), options ?? new(), Token);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Overlapping_runs_have_their_own_kind_of_globals_and_their_own_input()
    {
        var written = Gate();
        var received = new TaskCompletionSource<(string, double[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new TestApp(
            ask: (question, _) =>
            {
                written.TrySetResult();
                return Task.FromResult(question);
            },
            receive: (runId, values) =>
            {
                received.TrySetResult((runId, values));
                return Task.CompletedTask;
            }
        );
        await using var session = await Start(app);
        CSharpScriptRun writer = await session.StartScriptAsync(
            await Compile(
                """await Messages.Writer.WriteAsync(Input.RunId + Input.Device, Stop); await AskAsync("written");"""
            ),
            new RunInput("seven", "chars"),
            Token
        );
        await Wait.ForAsync(written.Task);
        CSharpScriptRun reader = await session.StartScriptAsync(
            await CompileAs(ScriptEnvironments.Reader, "await SendAsync((await Messages.ReadAsync(Stop)).Length);"),
            Token
        );
        await Wait.ForAsync(Task.WhenAll(writer.Completion, reader.Completion));
        (string runId, double[] values) = await Wait.ForAsync(received.Task);
        runId.ShouldBe("reader");
        values.ShouldBe([10.0]);
    }

    [Fact]
    public async Task Calls_go_both_ways_while_the_other_side_waits()
    {
        CSharpScriptSession<ISessionSide, Session>? session = null;
        string? notified = null;
        var app = new TestApp(
            ask: async (_, token) => await session!.Remote.GetStatusAsync(token),
            notify: message => notified = message
        );
        session = await Start(app);
        await using (session)
        {
            await session.Remote.InitializeAsync("bench", Token);
            CSharpScriptRun run = await session.StartScriptAsync(
                await Compile("""await NotifyAsync(await AskAsync("status?"));"""),
                new RunInput("one", "device"),
                Token
            );
            await Wait.ForAsync(run.Completion);
        }
        notified.ShouldBe("bench: no calculation");
    }

    [Fact]
    public async Task A_callback_that_outlives_its_run_reports_its_original_line()
    {
        CSharpScriptAssembly<ScriptGlobals> script = await Compile(
            """
            // The second line throws, long after this run has ended.
            SetCalculation(x => throw new FormatException("from the callback"));
            """
        );
        await using var session = await Start();
        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(run.Completion);

        var failure = await Should.ThrowAsync<CSharpScriptException>(() => session.Remote.CalculateAsync(1, Token));

        failure.ScriptExceptionType.ShouldBe("System.FormatException");
        failure.Message.ShouldBe("from the callback");
        failure.ScriptSnapshot.ShouldBeSameAs(script.Snapshot);
        failure.ScriptPosition.ShouldNotBeNull().Line.ShouldBe(2);
    }

    [Fact]
    public async Task A_session_method_that_cancels_itself_arrives_as_cancelled()
    {
        CSharpScriptAssembly<ScriptGlobals> script = await Compile(
            "SetCalculation(x => throw new OperationCanceledException());"
        );
        await using var session = await Start();
        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(run.Completion);

        await Should.ThrowAsync<OperationCanceledException>(() => session.Remote.CalculateAsync(1, Token));
    }

    [Fact]
    public async Task A_Create_that_throws_is_a_script_exception_and_ends_the_process()
    {
        var failure = await Should.ThrowAsync<CSharpScriptException>(() =>
            CSharpScriptSession.StartAsync<IAppSide, ISessionSide, ThrowingSession>(new TestApp(), Token)
        );
        failure.ScriptExceptionType.ShouldBe("System.FormatException");
        failure.Message.ShouldBe("The session object failed.");
    }

    [Fact]
    public async Task A_Create_that_throws_its_own_cancellation_stays_a_script_exception()
    {
        var failure = await Should.ThrowAsync<CSharpScriptException>(() =>
            CSharpScriptSession.StartAsync<IAppSide, ISessionSide, CancellingSession>(new TestApp(), Token)
        );
        failure.ScriptExceptionType.ShouldBe("System.OperationCanceledException");
    }

    [Fact]
    public async Task A_bridge_without_generated_proxies_fails_before_a_process_starts()
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            CSharpScriptSession.StartAsync<IUnexportedSide, IUnexportedSide, UnexportedSession>(
                new UnexportedApp(),
                Token
            )
        );
        failure.Message.ShouldContain("ExportRpcContractProxies");
    }

    private sealed class UnexportedApp : IUnexportedSide
    {
        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_cancelled_start_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            CSharpScriptSession.StartAsync<IAppSide, ISessionSide, Session>(new TestApp(), cancelled.Token)
        );
    }

    [Fact]
    public async Task A_failing_script_ends_only_its_run()
    {
        await using var session = await Start();
        CSharpScriptAssembly<ScriptGlobals> failing = await Compile(
            """
            var x = 1;
            throw new InvalidOperationException("line two");
            """
        );
        CSharpScriptRun run = await session.StartScriptAsync(failing, new RunInput("one", "device"), Token);

        var failure = await Should.ThrowAsync<CSharpScriptException>(() => Wait.ForAsync(run.Completion));

        failure.ScriptPosition.ShouldNotBeNull().Line.ShouldBe(2);
        failure.ScriptSnapshot.ShouldBeSameAs(failing.Snapshot);
        (await session.Remote.GetStatusAsync(Token)).ShouldContain("no calculation");
    }

    [Fact]
    public async Task A_cancellation_before_the_stop_token_is_a_failure()
    {
        await using var session = await Start();
        CSharpScriptRun run = await session.StartScriptAsync(
            await Compile("throw new OperationCanceledException();"),
            new RunInput("one", "device"),
            Token
        );
        var failure = await Should.ThrowAsync<CSharpScriptException>(() => Wait.ForAsync(run.Completion));
        failure.ScriptExceptionType.ShouldBe("System.OperationCanceledException");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_tells_a_run_that_stopped_from_one_that_needed_a_kill(bool cooperative)
    {
        var ready = Gate();
        string code = "Console.WriteLine(\"ready\"); await Task.Delay(-1" + (cooperative ? ", Stop);" : ");");
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(ScriptEnvironments.Reader, code);
        await using var session = await Start(
            options: new()
            {
                Output = line =>
                {
                    if (line == "ready")
                        ready.TrySetResult();
                },
            }
        );
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);
        await Wait.ForAsync(ready.Task);

        await Wait.ForAsync(run.StopAsync(cooperative ? null : TimeSpan.Zero));

        if (cooperative)
        {
            run.Completion.IsCanceled.ShouldBeTrue();
            await session.DisposeAsync();
            (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
        }
        else
        {
            await Should.ThrowAsync<ConnectionLostException>(() => run.Completion);
            session.Exited.IsCompletedSuccessfully.ShouldBeTrue();
            (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.StopTimedOut);
        }
    }

    [Fact]
    public async Task A_stop_deadline_leaves_a_session_alone_once_its_run_has_ended()
    {
        string folder = Directory.CreateTempSubdirectory("script-stop").FullName;
        string endFile = Path.Combine(folder, "end");
        var stopWriting = Gate();
        var releaseStop = Gate();
        var clock = new ManualTime();
        // The file is the gate: this script ends without anything crossing the pipe that the test holds.
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            $$"""while (!File.Exists(@"{{endFile}}")) await Task.Delay(5);"""
        );
        await using var session = await SessionConnection.OpenAsync<IAppSide, ISessionSide, Session>(
            new TestApp(),
            new CSharpScriptSessionOptions { TimeProvider = clock },
            Token,
            (output, input) =>
                new GatedHandler(
                    SessionWire.Handler<IAppSide, ISessionSide>(output, input),
                    write: async (message, _) =>
                    {
                        if (
                            message is JsonRpcRequest
                            {
                                Method: SessionWire.ControlPrefix + nameof(IRunnerControl.StopAsync)
                            }
                        )
                        {
                            stopWriting.TrySetResult();
                            await releaseStop.Task;
                        }
                    }
                )
        );
        try
        {
            CSharpScriptRun run = await session.StartScriptAsync(script, Token);
            Task stopping = run.StopAsync(TimeSpan.FromSeconds(2));
            await Wait.ForAsync(stopWriting.Task);
            await File.WriteAllTextAsync(endFile, "", Token);
            await Wait.ForAsync(run.Completion);

            clock.Advance(TimeSpan.FromSeconds(3));

            await Wait.ForAsync(stopping);
            releaseStop.SetResult();
            (await Wait.ForAsync(session.Remote.GetStatusAsync(Token))).ShouldContain("no calculation");
            await Wait.ForAsync(session.DisposeAsync().AsTask());
            (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
        }
        finally
        {
            releaseStop.TrySetResult();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_globals_factory_that_throws_when_stopped_is_a_failure()
    {
        var waiting = Gate();
        CSharpScriptCompilation<ScriptTestGlobals> compilation = await _server.CompileAsync(
            new CSharpScriptEnvironment<ScriptTestGlobals>(),
            "",
            Token
        );
        await using var session = await CSharpScriptSession.StartAsync<IAppSide, ISessionSide, StoppedFactorySession>(
            new TestApp(),
            new CSharpScriptSessionOptions
            {
                Output = line =>
                {
                    if (line == "factory waiting")
                        waiting.TrySetResult();
                },
            },
            Token
        );
        CSharpScriptRun run = await session.StartScriptAsync(compilation.Script.ShouldNotBeNull(), Token);
        await Wait.ForAsync(waiting.Task);

        await Wait.ForAsync(run.StopAsync());

        var failure = await Should.ThrowAsync<CSharpScriptException>(() => run.Completion);
        failure.ScriptExceptionType.ShouldBe("System.OperationCanceledException");
    }

    [Fact]
    public async Task Disposing_reports_the_session_objects_failure_in_Exited_without_throwing()
    {
        await using var session = await Start();
        await session.DisposeAsync();
        CSharpScriptSessionExit exit = await session.Exited;
        exit.Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
        exit.ExitCode.ShouldBe(0);
        exit.DisposeException.ShouldBeNull();
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await session.StartScriptAsync(await Compile(""), new RunInput("late", "device"), Token)
        );
        await Should.ThrowAsync<ConnectionLostException>(() => session.Remote.GetStatusAsync(Token));
    }

    [Fact]
    public async Task Disposal_stops_runs_whose_start_has_not_returned()
    {
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            "await Task.Delay(-1, Stop);"
        );
        await using var session = await Start();
        Task<CSharpScriptRun>[] starts =
        [
            .. Enumerable.Range(0, 4).Select(_ => session.StartScriptAsync(script, Token)),
        ];

        await Wait.ForAsync(session.DisposeAsync().AsTask());

        foreach (Task<CSharpScriptRun> start in starts)
            (await start).Completion.IsCanceled.ShouldBeTrue();
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
    }

    [Fact]
    public async Task New_calls_are_refused_while_disposal_waits_for_an_app_call()
    {
        var entered = Gate();
        var release = Gate();
        var app = new TestApp(
            receive: (_, _) =>
            {
                entered.TrySetResult();
                return release.Task;
            }
        );
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(ScriptEnvironments.Reader, "await SendAsync(1);");
        await using var session = await Start(app);
        ISessionSide remote = session.Remote;
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);
        await Wait.ForAsync(entered.Task);

        Task disposal = session.DisposeAsync().AsTask();
        try
        {
            await Should.ThrowAsync<ConnectionLostException>(() => remote.GetStatusAsync(Token));
            session.Exited.IsCompleted.ShouldBeFalse();
            disposal.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.SetResult();
        }

        await Wait.ForAsync(run.Completion);
        await Wait.ForAsync(disposal);
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
    }

    [Fact]
    public async Task A_call_that_reaches_the_runner_after_admission_closed_is_refused()
    {
        var receiving = Gate();
        var release = Gate();
        var closing = Gate();
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            """Stop.Register(() => Console.WriteLine("closing")); await SendAsync(1); await Task.Delay(-1, Stop);"""
        );
        using ServerProcess process = ServerProcess.Start(
            SessionConnection.StartInfo<IAppSide, ISessionSide, Session>(new()),
            line =>
            {
                if (line == "closing")
                    closing.TrySetResult();
            }
        );
        // The app's own gate would refuse the call first: this connection has none, like a call already on the wire.
        using var rpc = new SessionRpc(SessionWire.Handler<IAppSide, ISessionSide>(process.Input, process.Output));
        IRunnerControl control = SessionWire.Proxy<IRunnerControl>(rpc);
        ISessionSide remote = SessionWire.Proxy<ISessionSide>(rpc);
        var app = new TestApp(
            receive: (_, _) =>
            {
                receiving.TrySetResult();
                return release.Task;
            }
        );
        rpc.AddLocalRpcTarget(RpcTargetMetadata.FromShape<IAppSide>(), app, null);
        rpc.StartListening();
        (await Wait.ForAsync(control.InitializeAsync())).ShouldBeNull();
        await control.BeginAsync(
            new RunRequest(
                1,
                typeof(ReaderGlobals).AssemblyQualifiedName!,
                typeof(ValueTuple).AssemblyQualifiedName!,
                [],
                script.Image.Assembly,
                script.Image.EntryType,
                script.Image.EntryMethod
            )
        );
        await Wait.ForAsync(receiving.Task);

        Task<RunFailure?> close = control.CloseAsync();
        try
        {
            await Wait.ForAsync(closing.Task);
            await Should.ThrowAsync<ConnectionLostException>(() => remote.GetStatusAsync(Token));
            close.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        (await Wait.ForAsync(close)).ShouldBeNull();
    }

    [Fact]
    public async Task An_admitted_call_gets_its_whole_reply_while_the_session_is_disposed()
    {
        var replyWaiting = Gate();
        var readReply = Gate();
        CSharpScriptAssembly<ScriptGlobals> script = await Compile("SetCalculation(x => x + 1);");
        // More than a pipe holds, so that the runner can only finish writing the reply while the app reads it.
        double[] inputs = [.. Enumerable.Repeat(1.0, 200_000)];
        await using var session = await SessionConnection.OpenAsync<IAppSide, ISessionSide, Session>(
            new TestApp(),
            new(),
            Token,
            (output, input) =>
                new GatedHandler(
                    SessionWire.Handler<IAppSide, ISessionSide>(output, input),
                    read: async message =>
                    {
                        if (message is JsonRpcResult { RequestId: var id } && id == s_gatedReply)
                        {
                            replyWaiting.TrySetResult();
                            await readReply.Task;
                        }
                    },
                    write: (message, _) =>
                    {
                        if (message is JsonRpcRequest { Method: nameof(ISessionSide.CalculateManyAsync) } call)
                            s_gatedReply = call.RequestId;
                        return Task.CompletedTask;
                    }
                )
        );
        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(run.Completion);
        Task<double[]> call = session.Remote.CalculateManyAsync(inputs, Token);
        await Wait.ForAsync(replyWaiting.Task);

        Task disposal = session.DisposeAsync().AsTask();
        disposal.IsCompleted.ShouldBeFalse();
        readReply.SetResult();

        (await Wait.ForAsync(call)).Length.ShouldBe(inputs.Length);
        await Wait.ForAsync(disposal);
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
    }

    private static RequestId s_gatedReply;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_kill_during_a_write_reports_connection_loss_after_Exited(bool endOfStreamFirst)
    {
        var writing = Gate();
        var endOfStream = Gate();
        var finishRead = Gate();
        await using var session = await SessionConnection.OpenAsync<IAppSide, ISessionSide, Session>(
            new TestApp(),
            new(),
            Token,
            (output, input) =>
                new GatedHandler(
                    SessionWire.Handler<IAppSide, ISessionSide>(output, input),
                    read: async message =>
                    {
                        if (message is null)
                        {
                            endOfStream.TrySetResult();
                            if (!endOfStreamFirst)
                                await finishRead.Task;
                        }
                    },
                    write: async (message, token) =>
                    {
                        if (message is JsonRpcRequest { Method: nameof(ISessionSide.GetStatusAsync) })
                        {
                            writing.TrySetResult();
                            await endOfStream.Task;
                            if (endOfStreamFirst)
                            {
                                var cancelled = Gate();
                                using CancellationTokenRegistration registration = token.Register(() =>
                                    cancelled.TrySetResult()
                                );
                                await cancelled.Task;
                            }
                            throw new IOException("The runner's pipe closed during the write.");
                        }
                    }
                )
        );
        Task call = session.Remote.GetStatusAsync(Token);
        await Wait.ForAsync(writing.Task);

        Task kill = session.KillAsync();
        try
        {
            var error = await Should.ThrowAsync<ConnectionLostException>(() => Wait.ForAsync(call));
            if (!endOfStreamFirst)
                error.InnerException.ShouldBeOfType<IOException>();
            session.Exited.IsCompletedSuccessfully.ShouldBeTrue();
        }
        finally
        {
            finishRead.TrySetResult();
        }
        await Wait.ForAsync(kill);
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Killed);
    }

    [Fact]
    public async Task A_kill_waits_for_an_app_call_that_was_already_on_the_pipe()
    {
        var buffered = Gate();
        var dispatch = Gate();
        var entered = Gate();
        var release = Gate();
        var app = new TestApp(
            ask: async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return "";
            }
        );
        CSharpScriptAssembly<ScriptGlobals> script = await Compile("""await AskAsync("waiting");""");
        await using var session = await SessionConnection.OpenAsync<IAppSide, ISessionSide, Session>(
            app,
            new(),
            Token,
            (output, input) =>
                new GatedHandler(
                    SessionWire.Handler<IAppSide, ISessionSide>(output, input),
                    read: async message =>
                    {
                        if (message is JsonRpcRequest { Method: nameof(IAppSide.AskAsync) })
                        {
                            buffered.TrySetResult();
                            await dispatch.Task;
                        }
                    }
                )
        );
        Task<CSharpScriptRun> start = session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(buffered.Task);

        Task kill = session.KillAsync();
        try
        {
            await Wait.ForAsync(session.Exited);
            kill.IsCompleted.ShouldBeFalse();
            dispatch.TrySetResult();
            await Wait.ForAsync(entered.Task);
            kill.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            dispatch.TrySetResult();
            release.TrySetResult();
        }
        await Wait.ForAsync(kill);
        await Should.ThrowAsync<ConnectionLostException>(async () => await (await start).Completion);
    }

    [Fact]
    public async Task A_kill_during_disposal_is_the_reason_and_cuts_it_short()
    {
        var entered = Gate();
        var cancelled = Gate();
        var app = new TestApp(
            ask: async (_, token) =>
            {
                entered.TrySetResult();
                using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
                await cancelled.Task;
                return "";
            }
        );
        // It ignores its stop token, so disposing can't get past this run.
        CSharpScriptAssembly<ScriptGlobals> script = await Compile(
            """await AskAsync("waiting"); await Task.Delay(-1);"""
        );
        await using var session = await Start(app);
        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(entered.Task);
        Task disposal = session.DisposeAsync().AsTask();
        disposal.IsCompleted.ShouldBeFalse();

        await Wait.ForAsync(session.KillAsync());

        await Wait.ForAsync(disposal);
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Killed);
        await Should.ThrowAsync<ConnectionLostException>(() => Wait.ForAsync(run.Completion));
    }

    [Fact]
    public async Task A_kill_finishes_even_if_an_app_calls_cancellation_callback_throws()
    {
        var ready = Gate();
        var app = new TestApp(
            ask: async (_, token) =>
            {
                var cancelled = Gate();
                using CancellationTokenRegistration registration = token.Register(() =>
                {
                    cancelled.SetResult();
                    throw new FormatException();
                });
                ready.SetResult();
                await cancelled.Task;
                return "";
            }
        );
        CSharpScriptAssembly<ScriptGlobals> script = await Compile("""await AskAsync("waiting");""");
        await using var session = await Start(app);
        ISessionSide remote = session.Remote;
        CSharpScriptRun run = await session.StartScriptAsync(script, new RunInput("one", "device"), Token);
        await Wait.ForAsync(ready.Task);

        await Wait.ForAsync(session.KillAsync());

        await Should.ThrowAsync<ConnectionLostException>(() => Wait.ForAsync(run.Completion));
        await Should.ThrowAsync<ConnectionLostException>(() => remote.GetStatusAsync(Token));
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Killed);
    }

    [Fact]
    public async Task Disposal_lets_a_caller_cancel_a_call_that_is_still_outstanding()
    {
        var request = new TaskCompletionSource<RequestId>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gotReply = Gate();
        var release = Gate();
        var cancelled = Gate();
        await using var session = await SessionConnection.OpenAsync<IAppSide, ISessionSide, Session>(
            new TestApp(),
            new(),
            Token,
            (output, input) =>
                new GatedHandler(
                    SessionWire.Handler<IAppSide, ISessionSide>(output, input),
                    read: async message =>
                    {
                        if (
                            message is JsonRpcResult result
                            && request.Task.IsCompletedSuccessfully
                            && result.RequestId == await request.Task
                        )
                        {
                            gotReply.TrySetResult();
                            await release.Task;
                        }
                    },
                    write: (message, _) =>
                    {
                        if (message is JsonRpcRequest { Method: nameof(ISessionSide.GetStatusAsync) } status)
                            request.TrySetResult(status.RequestId);
                        if (message is JsonRpcRequest { Method: "$/cancelRequest" })
                            cancelled.TrySetResult();
                        return Task.CompletedTask;
                    }
                )
        );
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<string> call = session.Remote.GetStatusAsync(caller.Token);
        await Wait.ForAsync(gotReply.Task);

        Task disposal = session.DisposeAsync().AsTask();
        try
        {
            await caller.CancelAsync();
            await Wait.ForAsync(cancelled.Task);
        }
        finally
        {
            release.TrySetResult();
        }
        try
        {
            await call;
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }

        await Wait.ForAsync(disposal);
        (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
    }

    [Fact]
    public async Task An_app_side_that_cancels_by_itself_reaches_the_script_as_a_failure()
    {
        var app = new TestApp(ask: (_, _) => Task.FromCanceled<string>(new CancellationToken(canceled: true)));
        await using var session = await Start(app);
        CSharpScriptRun run = await session.StartScriptAsync(
            await Compile("""await AskAsync("wait");"""),
            new RunInput("one", "device"),
            Token
        );
        var failure = await Should.ThrowAsync<CSharpScriptException>(() => Wait.ForAsync(run.Completion));
        failure.ScriptExceptionType.ShouldBe(typeof(RemoteInvocationException).FullName);
    }

    [Fact]
    public async Task A_blocked_output_callback_does_not_hold_up_Exited()
    {
        var entered = Gate();
        var release = Gate();
        var delivered = Gate();
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            """Console.WriteLine("first"); Console.WriteLine("last");"""
        );
        await using var session = await Start(
            options: new()
            {
                Output = line =>
                {
                    if (line == "first")
                    {
                        entered.TrySetResult();
                        release.Task.GetAwaiter().GetResult();
                    }
                    if (line == "last")
                        delivered.TrySetResult();
                },
            }
        );
        try
        {
            CSharpScriptRun run = await session.StartScriptAsync(script, Token);
            await Wait.ForAsync(entered.Task);
            await Wait.ForAsync(run.Completion);
            await Wait.ForAsync(session.DisposeAsync().AsTask());
            CSharpScriptSessionExit exit = await session.Exited;
            exit.Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
            exit.StandardError.ShouldContain("last");
            delivered.Task.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
        }
        await Wait.ForAsync(delivered.Task);
    }

    [Fact]
    public async Task A_child_without_redirects_prints_into_Output_and_reads_an_empty_input()
    {
        var lines = new LineLog();
        string shell = OperatingSystem.IsWindows()
            ? """Process.Start("cmd.exe", "/c findstr x & echo child-out & echo child-err 1>&2")"""
            : """Process.Start("sh", ["-c", "cat; echo child-out; echo child-err >&2"])""";
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            $"""
            var child = System.Diagnostics.{shell}!;
            await child.WaitForExitAsync(Stop);
            Console.WriteLine($"exit {'{'}child.ExitCode{'}'}");
            """
        );
        await using var session = await Start(options: new() { Output = lines.Add });
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);

        await Wait.ForAsync(run.Completion);

        await lines.WaitForAsync("exit 0");
        await lines.WaitForAsync("child-out");
        await lines.WaitForAsync("child-err");
    }

    [Fact]
    public async Task Native_code_writing_to_descriptor_1_reaches_Output_and_leaves_the_connection_alone()
    {
        var lines = new LineLog();
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            """
            byte[] text = System.Text.Encoding.ASCII.GetBytes("from-native-code\n");
            if (OperatingSystem.IsWindows())
                Native._write(1, text, (uint)text.Length);
            else
                Native.write(1, text, (nuint)text.Length);

            static class Native
            {
                [System.Runtime.InteropServices.DllImport("ucrtbase")]
                public static extern int _write(int descriptor, byte[] buffer, uint count);

                [System.Runtime.InteropServices.DllImport("libc")]
                public static extern nint write(int descriptor, byte[] buffer, nuint count);
            }
            """
        );
        await using var session = await Start(options: new() { Output = lines.Add });
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);

        await Wait.ForAsync(run.Completion);

        await lines.WaitForAsync("from-native-code");
        (await Wait.ForAsync(session.Remote.GetStatusAsync(Token))).ShouldContain("no calculation");
    }

    [Fact]
    public async Task A_child_inherits_no_descriptor_of_the_connection()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Reads the descriptors from /proc.");
        var lines = new LineLog();
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            """
            var child = System.Diagnostics.Process.Start("ls", ["-C", "-w", "200", "/proc/self/fd"])!;
            await child.WaitForExitAsync(Stop);
            """
        );
        await using var session = await Start(options: new() { Output = lines.Add });
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);

        await Wait.ForAsync(run.Completion);

        string descriptors = await lines.WaitForMatchAsync(line => line.StartsWith('0'));
        // What any child of this process gets: the standard streams, the folder ls reads, and what the test run left open.
        using var baseline = Process.Start(
            new ProcessStartInfo("ls", ["-C", "-w", "200", "/proc/self/fd"]) { RedirectStandardOutput = true }
        )!;
        string inherited = await baseline.StandardOutput.ReadToEndAsync(Token);
        descriptors
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ShouldBeSubsetOf(inherited.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task A_surviving_child_with_one_redirected_stream_does_not_delay_Exited()
    {
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // It redirects standard output only, so it holds the log pipe through standard error for all its life.
        string command = OperatingSystem.IsWindows()
            ? """new("ping", "-n 600 127.0.0.1")"""
            : """new("sleep", "600")""";
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            $$"""
            System.Diagnostics.ProcessStartInfo info = {{command}};
            info.RedirectStandardOutput = true;
            Console.WriteLine("pid " + System.Diagnostics.Process.Start(info)!.Id);
            """
        );
        await using var session = await Start(
            options: new()
            {
                Output = line =>
                {
                    if (line.StartsWith("pid ", StringComparison.Ordinal))
                        pid.TrySetResult(int.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture));
                },
            }
        );
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);
        using Process child = Process.GetProcessById(await Wait.ForAsync(pid.Task));
        try
        {
            await Wait.ForAsync(run.Completion);
            await Wait.ForAsync(session.DisposeAsync().AsTask());
            (await session.Exited).Reason.ShouldBe(CSharpScriptSessionExitReason.Disposed);
            child.HasExited.ShouldBeFalse();
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public async Task A_failure_before_the_runner_is_initialized_reports_its_standard_error()
    {
        // The runtime can't set up its heap, so it fails before any code of the runner runs.
        var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
            Start(
                options: new()
                {
                    EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_GCHeapHardLimit"] = "1" },
                }
            )
        );
        failure.Message.ShouldContain("did not answer");
        failure.Message.ShouldContain("CoreCLR", Case.Insensitive);
    }

    [Fact]
    public async Task A_runtime_crash_report_reaches_the_tail_of_Exited_with_its_message()
    {
        // The report starts with the message, above one line per frame: a stack this deep needs a long tail.
        CSharpScriptAssembly<ReaderGlobals> script = await CompileAs(
            ScriptEnvironments.Reader,
            """
            int Deep(int depth) => depth == 0 ? Crash() : 1 + Deep(depth - 1);
            int Crash() { Environment.FailFast("the-crash-message"); return 0; }
            Deep(40);
            """
        );
        await using var session = await Start();
        CSharpScriptRun run = await session.StartScriptAsync(script, Token);

        await Should.ThrowAsync<ConnectionLostException>(() => Wait.ForAsync(run.Completion));

        session.Exited.IsCompletedSuccessfully.ShouldBeTrue();
        CSharpScriptSessionExit exit = await session.Exited;
        exit.Reason.ShouldBe(CSharpScriptSessionExitReason.Crashed);
        exit.StandardError.ShouldContain("the-crash-message");
        exit.StandardError.Split('\n').Length.ShouldBeGreaterThan(40);
    }

    [Fact]
    public async Task Use_case_06_runs_a_saved_script_and_reports_each_failure()
    {
        string folder = Directory.CreateTempSubdirectory("script-session").FullName;
        try
        {
            var log = new ListLogger();
            var saved = new SavedScripts(_server, log);
            string good = Path.Combine(folder, "good.csx");
            await File.WriteAllTextAsync(good, """await SendAsync(1); await SendAsync(2);""", Token);
            string broken = Path.Combine(folder, "broken.csx");
            await File.WriteAllTextAsync(broken, "Missing();", Token);
            string failing = Path.Combine(folder, "failing.csx");
            await File.WriteAllTextAsync(failing, """throw new FormatException("no");""", Token);

            (await saved.RunAsync(good, Token)).ShouldBeTrue();
            (await saved.RunAsync(broken, Token)).ShouldBeFalse();
            (await saved.RunAsync(failing, Token)).ShouldBeFalse();

            log.Lines.ShouldContain(line => line.Contains("good: ", StringComparison.Ordinal));
            log.Lines.ShouldContain(line =>
                line.Contains("CS0103", StringComparison.Ordinal) || line.Contains("Missing", StringComparison.Ordinal)
            );
            log.Lines.ShouldContain(line => line.Contains("System.FormatException: no", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Use_case_07_app_to_script()
    {
        var log = new LineLog();
        await new AppToScript(_server).RunAsync(log.Add, Token);
        await log.WaitForAsync("3 -> 9");
        await log.WaitForAsync("1024 results, the last 1046529");
        await log.WaitForAsync("demo: calculation set");
    }

    [Fact]
    public async Task Use_case_07_script_to_app()
    {
        var messages = new LineLog();
        long values = 0;
        await new ScriptToApp(_server).RunAsync(
            (_, _) => Task.FromResult("yes"),
            (runId, message) => messages.Add($"{runId}: {message}"),
            (runId, batch) =>
            {
                if (runId == "streaming")
                    Interlocked.Add(ref values, batch.Length);
            },
            Token
        );
        await messages.WaitForAsync("asking: The answer was yes.");
        await messages.WaitForAsync("ticking: tick 0");
        values.ShouldBe(1_000_000);
    }

    [Fact]
    public async Task Use_case_07_script_to_script()
    {
        var received = Gate();
        await new ScriptToScript(_server).RunAsync(
            (runId, values) =>
            {
                if (runId == "reader" && values.Length > 0)
                    received.TrySetResult();
            },
            TimeSpan.FromSeconds(1),
            Token
        );
        await Wait.ForAsync(received.Task);
    }

    [Fact]
    public async Task Use_case_08_reports_a_failure_and_a_kill_and_a_crash()
    {
        var reports = new LineLog();
        await using CSharpScriptSession<ISessionSide, Session> session = await Scripting.StartSessionAsync(
            new AppSide((_, _) => Task.FromResult("")),
            Token
        );
        var guarded = new GuardedRuns(session, reports.Add);

        await guarded.RunAsync(
            await Compile("""throw new FormatException("from the script");"""),
            new RunInput("failing", "none"),
            Token
        );
        await reports.WaitForMatchAsync(line =>
            line.StartsWith("The script failed: System.FormatException", StringComparison.Ordinal)
        );

        using var stop = new CancellationTokenSource();
        Task ignoring = guarded.RunAsync(
            await Compile("""await AskAsync("ready"); await Task.Delay(-1);"""),
            new RunInput("ignoring", "none"),
            stop.Token
        );
        await stop.CancelAsync();
        await Wait.ForAsync(ignoring);
        await reports.WaitForMatchAsync(line =>
            line.Contains("StopTimedOut", StringComparison.Ordinal) || line == "Stopped."
        );

        // A session that has ended reports that, also for a run started afterwards.
        await guarded.RunAsync(await Compile("Environment.Exit(3);"), new RunInput("crash", "none"), Token);
        await reports.WaitForMatchAsync(line => line.StartsWith("The session ended", StringComparison.Ordinal));
    }

    /// <summary>The lines of a session's output, which arrive on another thread.</summary>
    private sealed class LineLog
    {
        private readonly List<string> _lines = [];

        public void Add(string line)
        {
            lock (_lines)
                _lines.Add(line);
        }

        public Task WaitForAsync(string text) => WaitForMatchAsync(line => line.Trim() == text);

        public async Task<string> WaitForMatchAsync(Func<string, bool> match)
        {
            string? found = null;
            await Wait.UntilAsync(() =>
            {
                lock (_lines)
                    found = _lines.Find(l => match(l));
                return found is not null;
            });
            return found!;
        }
    }

    private sealed class ListLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                    return [.. _lines];
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            lock (_lines)
                _lines.Add(formatter(state, exception));
        }
    }

    private sealed class TestApp(
        Func<string, CancellationToken, Task<string>>? ask = null,
        Action<string>? notify = null,
        Func<string, double[], Task>? receive = null
    ) : IAppSide
    {
        private readonly Func<string, CancellationToken, Task<string>>? _ask = ask;
        private readonly Action<string>? _notify = notify;
        private readonly Func<string, double[], Task>? _receive = receive;

        public Task<string> AskAsync(string question, CancellationToken cancellationToken) =>
            _ask?.Invoke(question, cancellationToken) ?? Task.FromResult(question);

        public Task NotifyAsync(string runId, string message, CancellationToken cancellationToken)
        {
            _notify?.Invoke(message);
            return Task.CompletedTask;
        }

        public Task ReceiveAsync(string runId, double[] values, CancellationToken cancellationToken) =>
            _receive?.Invoke(runId, values) ?? Task.CompletedTask;
    }

    /// <summary>Stops a message on its way, so that a test can force the order of two events on the pipe.</summary>
    private sealed class GatedHandler : IJsonRpcMessageHandler, IJsonRpcMessageBufferManager, IDisposable
    {
        private readonly IJsonRpcMessageHandler _inner;
        private readonly Func<JsonRpcMessage?, Task>? _read;
        private readonly Func<JsonRpcMessage, CancellationToken, Task>? _write;

        public GatedHandler(
            IJsonRpcMessageHandler inner,
            Func<JsonRpcMessage?, Task>? read = null,
            Func<JsonRpcMessage, CancellationToken, Task>? write = null
        )
        {
            _inner = inner;
            _read = read;
            _write = write;
        }

        public bool CanRead => _inner.CanRead;

        public bool CanWrite => _inner.CanWrite;

        public IJsonRpcMessageFormatter Formatter => _inner.Formatter;

        public void Dispose() => ((IDisposable)_inner).Dispose();

        public void DeserializationComplete(JsonRpcMessage message) =>
            ((IJsonRpcMessageBufferManager)_inner).DeserializationComplete(message);

        public async ValueTask<JsonRpcMessage?> ReadAsync(CancellationToken cancellationToken)
        {
            JsonRpcMessage? message = await _inner.ReadAsync(cancellationToken);
            if (_read is not null)
                await _read(message);
            return message;
        }

        public async ValueTask WriteAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (_write is not null)
                await _write(message, cancellationToken);
            await _inner.WriteAsync(message, cancellationToken);
        }
    }
}
