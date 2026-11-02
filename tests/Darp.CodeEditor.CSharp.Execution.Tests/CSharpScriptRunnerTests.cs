using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Execution.TestHost;
using Darp.CodeEditor.CSharp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Shouldly;
using StreamJsonRpc;
using static Darp.CodeEditor.CSharp.Execution.Tests.TestRunners;

namespace Darp.CodeEditor.CSharp.Execution.Tests;

/// <summary>The runner as an app uses it: scripts return handlers, call back into the app, and fail in known ways.</summary>
public sealed class CSharpScriptRunnerTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task App_calls_the_handler_a_script_returns()
    {
        await using CSharpScriptRunner runner = await StartAsync();

        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler("return sample.Channel * 100 + sample.Payload.Length;"),
            Cancellation
        );

        (await handler.HandleAsync(new Sample(3, new byte[7]), Cancellation)).ShouldBe(307);
        (await handler.HandleBatchAsync([new Sample(1, []), new Sample(5, [])], Cancellation)).ShouldBe([2, 10]);
    }

    [Fact]
    public async Task Scripts_call_back_into_the_app_through_their_globals()
    {
        var host = new RecordingHost();
        await using CSharpScriptRunner runner = await StartAsync(host);

        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                """
                await _host.LogAsync($"channel {sample.Channel}", cancellationToken);
                return await _host.AddAsync(sample.Channel, 1000, cancellationToken);
                """
            ),
            Cancellation
        );

        (await handler.HandleAsync(new Sample(3, []), Cancellation)).ShouldBe(1003);
        host.Messages.ShouldBe(["channel 3"]);
    }

    [Fact]
    public async Task Start_fails_when_the_globals_need_a_service_the_app_does_not_provide()
    {
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            CSharpScriptRunner.StartAsync(
                new CSharpScriptRunnerOptions { Environment = TestEnvironment.Create() },
                Cancellation
            )
        );

        exception.Message.ShouldContain(typeof(ITestHost).FullName!);
    }

    [Fact]
    public async Task Compile_errors_are_the_diagnostics_the_editor_shows()
    {
        const string code = """
            int count = "three";
            Host.Missing();
            """;
        await using CSharpScriptRunner runner = await StartAsync();

        ScriptCompilationException exception = await Should.ThrowAsync<ScriptCompilationException>(() =>
            runner.EvaluateAsync<ITestHandler>(code, Cancellation)
        );

        exception
            .Diagnostics.Select(d => (d.Id, d.Severity, d.StartLine, d.StartCharacter, d.EndLine, d.EndCharacter))
            .ShouldBe([
                ("CS0029", ScriptDiagnosticSeverity.Error, 0, 12, 0, 19),
                ("CS1061", ScriptDiagnosticSeverity.Error, 1, 5, 1, 12),
            ]);
        // The editor compiles with ResolvedScriptEnvironment as well; the runner must report its diagnostics exactly.
        ResolvedScriptEnvironment environment = ResolvedScriptEnvironment.Resolve(TestEnvironment.Create());
        Script<object> script = environment.CreateScript(code, typeof(TestGlobals), environment.CreateScriptOptions());
        exception
            .Diagnostics.Select(d => (d.Id, d.Message, d.StartLine, d.StartCharacter))
            .ShouldBe(
                script
                    .GetCompilation()
                    .GetDiagnostics(Cancellation)
                    .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                    .Select(d =>
                        (
                            d.Id,
                            d.GetMessage(),
                            d.Location.GetLineSpan().StartLinePosition.Line,
                            d.Location.GetLineSpan().StartLinePosition.Character
                        )
                    )
            );
    }

    [Fact]
    public async Task Diagnostic_lines_follow_the_editor_line_breaks()
    {
        // U+2028, here inside a verbatim string, ends a line for Roslyn but not for LSP clients such as the editor.
        const string code = "var text = @\"first\u2028second\";\nint count = \"three\";";
        await using CSharpScriptRunner runner = await StartAsync();

        ScriptCompilationException exception = await Should.ThrowAsync<ScriptCompilationException>(() =>
            runner.EvaluateAsync<ITestHandler>(code, Cancellation)
        );

        ScriptDiagnostic error = exception.Diagnostics.Single(d => d.Severity == ScriptDiagnosticSeverity.Error);
        error.Id.ShouldBe("CS0029");
        (error.StartLine, error.StartCharacter).ShouldBe((1, 12));
    }

    [Fact]
    public async Task Script_exception_fails_the_call_and_the_handler_keeps_working()
    {
        await using CSharpScriptRunner runner = await StartAsync();
        int? processId = runner.ProcessId;
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                """
                if (sample.Channel == 0)
                    throw new InvalidOperationException("bad sample");
                return sample.Channel;
                """
            ),
            Cancellation
        );

        ScriptException exception = await Should.ThrowAsync<ScriptException>(() =>
            handler.HandleAsync(new Sample(0, []), Cancellation)
        );

        exception.ScriptExceptionType.ShouldBe("System.InvalidOperationException");
        exception.Message.ShouldBe("bad sample");
        exception.ScriptStackTrace.ShouldNotBeNull().ShouldContain("Handler.HandleAsync");
        (await handler.HandleAsync(new Sample(4, []), Cancellation)).ShouldBe(4);
        runner.ProcessId.ShouldBe(processId);
    }

    [Theory]
    [InlineData("throw new InvalidOperationException(\"no handler\");", "no handler")]
    [InlineData(
        "42",
        "The script returned a System.Int32, not a Darp.CodeEditor.CSharp.Execution.TestApi.ITestHandler"
    )]
    public async Task Script_that_fails_to_produce_a_handler_throws_a_script_exception(string code, string message)
    {
        await using CSharpScriptRunner runner = await StartAsync();

        ScriptException exception = await Should.ThrowAsync<ScriptException>(() =>
            runner.EvaluateAsync<ITestHandler>(code, Cancellation)
        );

        exception.Message.ShouldStartWith(message);
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(Handler("return 1;"), Cancellation);
        (await handler.HandleAsync(new Sample(0, []), Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task Call_past_its_deadline_is_cancelled_and_the_runner_keeps_running()
    {
        await using CSharpScriptRunner runner = await StartAsync(
            callTimeout: TimeSpan.FromMilliseconds(300),
            gracePeriod: TimeSpan.FromSeconds(1)
        );
        int? processId = runner.ProcessId;
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                """
                if (sample.Channel == 0)
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                return sample.Channel;
                """
            ),
            Cancellation
        );

        await Should.ThrowAsync<TimeoutException>(() => handler.HandleAsync(new Sample(0, []), Cancellation));

        // Past the grace period: the script honoured the cancellation, so nothing was killed.
        await Task.Delay(TimeSpan.FromMilliseconds(1600), Cancellation);
        exited.IsCompleted.ShouldBeFalse();
        runner.ProcessId.ShouldBe(processId);
        (await handler.HandleAsync(new Sample(2, []), Cancellation)).ShouldBe(2);
    }

    [Fact]
    public async Task Busy_call_past_its_deadline_kills_the_runner_and_the_next_script_gets_a_new_one()
    {
        await using CSharpScriptRunner runner = await StartAsync(callTimeout: TimeSpan.FromMilliseconds(300));
        int firstProcess = runner.ProcessId.ShouldNotBeNull();
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        string code = Handler(
            """
            if (sample.Channel == 0)
                while (true) { }
            return sample.Channel;
            """
        );
        ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(code, Cancellation);

        await Should.ThrowAsync<TimeoutException>(() => handler.HandleAsync(new Sample(0, []), Cancellation));

        (await exited.WaitAsync(TimeSpan.FromSeconds(5), Cancellation)).Reason.ShouldBe(ScriptRunnerExitReason.Killed);
        (await WaitUntilExitedAsync(firstProcess, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(() => handler.HandleAsync(new Sample(1, []), Cancellation));
        using ITestHandler restarted = await runner.EvaluateAsync<ITestHandler>(code, Cancellation);
        (await restarted.HandleAsync(new Sample(1, []), Cancellation)).ShouldBe(1);
        runner.ProcessId.ShouldNotBe(firstProcess);
    }

    [Fact]
    public async Task Crash_is_reported_and_the_next_script_gets_a_new_runner()
    {
        await using CSharpScriptRunner runner = await StartAsync();
        int firstProcess = runner.ProcessId.ShouldNotBeNull();
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        string code = Handler(
            """
            if (sample.Channel == 0)
                Environment.FailFast("script crash");
            return sample.Channel;
            """
        );
        ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(code, Cancellation);

        await Should.ThrowAsync<ConnectionLostException>(() => handler.HandleAsync(new Sample(0, []), Cancellation));

        ScriptRunnerExitedEventArgs exit = await exited.WaitAsync(TimeSpan.FromSeconds(5), Cancellation);
        exit.Reason.ShouldBe(ScriptRunnerExitReason.Crashed);
        exit.ExitCode.ShouldNotBeNull().ShouldNotBe(0);
        exit.StandardError.ShouldContain("script crash");
        using ITestHandler restarted = await runner.EvaluateAsync<ITestHandler>(code, Cancellation);
        (await restarted.HandleAsync(new Sample(5, []), Cancellation)).ShouldBe(5);
        runner.ProcessId.ShouldNotBe(firstProcess);
    }

    [Fact]
    public async Task Dispose_ends_the_runner_and_what_its_scripts_started_while_a_call_is_busy()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Starts the Unix sleep command.");
        CSharpScriptRunner runner = await StartAsync(callTimeout: Timeout.InfiniteTimeSpan);
        int runnerProcess = runner.ProcessId.ShouldNotBeNull();
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                """
                if (sample.Channel == 0)
                    return System.Diagnostics.Process.Start("sleep", "60").Id;
                while (true) { }
                """
            ),
            Cancellation
        );
        int childProcess = await handler.HandleAsync(new Sample(0, []), Cancellation);
        Task busy = handler.HandleAsync(new Sample(1, []), Cancellation);

        await runner.DisposeAsync();

        await Should.ThrowAsync<ConnectionLostException>(() => busy);
        (await WaitUntilExitedAsync(runnerProcess, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await WaitUntilExitedAsync(childProcess, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        exited.IsCompleted.ShouldBeFalse();
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            runner.EvaluateAsync<ITestHandler>(Handler("return 1;"), Cancellation)
        );
    }
}
