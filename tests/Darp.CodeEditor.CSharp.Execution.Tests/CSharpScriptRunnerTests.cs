using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Darp.CodeEditor.CSharp.Execution.TestApi;
using Darp.CodeEditor.CSharp.Execution.TestHost;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
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
    public async Task Start_fails_when_the_script_api_next_to_the_app_is_another_build()
    {
        // Same name and version as the app's script API, but another build, as an outdated deployment leaves it.
        AssemblyName appBuild = typeof(TestGlobals).Assembly.GetName();
        string directory = Directory.CreateTempSubdirectory("csharp-runner-api-").FullName;
        try
        {
            string otherBuild = Path.Combine(directory, appBuild.Name + ".dll");
            EmitOtherBuild(otherBuild, appBuild);

            InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(() =>
                CSharpScriptRunner.StartAsync(
                    new CSharpScriptRunnerOptions
                    {
                        Environment = TestEnvironment.Create() with
                        {
                            Globals = new CSharpScriptGlobals(otherBuild, typeof(TestGlobals).FullName!),
                        },
                        Services = [CSharpScriptService.Create<ITestHost>(new RecordingHost())],
                    },
                    Cancellation
                )
            );

            exception.Message.ShouldContain($"Ship the {appBuild.Name} assembly the app was built with");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Runner_gets_the_app_environment_without_dotnet_runtime_variables()
    {
        // Harmless stand-ins for startup hooks, profilers and runtime settings meant for the app.
        string[] inherited = ["DOTNET_DARP_RUNNER_TEST", "CORECLR_DARP_RUNNER_TEST", "DARP_RUNNER_TEST"];
        foreach (string name in inherited)
            Environment.SetEnvironmentVariable(name, "app");
        try
        {
            await using CSharpScriptRunner runner = await CSharpScriptRunner.StartAsync(
                new CSharpScriptRunnerOptions
                {
                    Environment = TestEnvironment.Create(),
                    Services = [CSharpScriptService.Create<ITestHost>(new RecordingHost())],
                    RunnerEnvironmentVariables = new Dictionary<string, string> { ["DOTNET_DARP_RUNNER_SET"] = "set" },
                },
                Cancellation
            );
            using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
                Handler(
                    "return 0;",
                    """
                    string[] names =
                        ["DOTNET_DARP_RUNNER_TEST", "CORECLR_DARP_RUNNER_TEST", "DARP_RUNNER_TEST", "DOTNET_DARP_RUNNER_SET"];
                    return names.Select(name => Environment.GetEnvironmentVariable(name) is null ? 0 : 1).ToArray();
                    """
                ),
                Cancellation
            );

            (await handler.HandleBatchAsync([], Cancellation)).ShouldBe([0, 0, 1, 1]);
        }
        finally
        {
            foreach (string name in inherited)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("0", 1)]
    public async Task Scripts_run_culture_invariant_unless_the_app_turns_globalization_on(
        string? invariant,
        int formatsGerman
    )
    {
        Assert.SkipUnless(
            1.5.ToString(new CultureInfo("de-DE")) == "1,5",
            "Needs ICU, which the runner then loads too."
        );
        await using CSharpScriptRunner runner = await CSharpScriptRunner.StartAsync(
            new CSharpScriptRunnerOptions
            {
                Environment = TestEnvironment.Create(),
                Services = [CSharpScriptService.Create<ITestHost>(new RecordingHost())],
                RunnerEnvironmentVariables = invariant is null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = invariant },
            },
            Cancellation
        );
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                "return 0;",
                """
                try
                {
                    return [1.5.ToString(new System.Globalization.CultureInfo("de-DE")) == "1,5" ? 1 : -1];
                }
                catch (System.Globalization.CultureNotFoundException)
                {
                    return [0];
                }
                """
            ),
            Cancellation
        );

        (await handler.HandleBatchAsync([], Cancellation)).ShouldBe([formatsGerman]);
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
    public async Task Script_output_reaches_the_app_in_lines_of_bounded_length()
    {
        var lines = new ConcurrentQueue<string>();
        await using CSharpScriptRunner runner = await StartAsync(output: lines.Enqueue);
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(
            Handler(
                """
                // 20 million characters without a line break must not pile up in the app.
                for (int i = 0; i < 20; i++)
                    Console.Write(new string('x', 1_000_000));
                Console.WriteLine();
                Console.WriteLine("after the long line");
                return 1;
                """
            ),
            Cancellation
        );

        await handler.HandleAsync(new Sample(0, []), Cancellation);

        (await WaitUntilAsync(() => lines.Contains("after the long line"), TimeSpan.FromSeconds(30))).ShouldBeTrue();
        lines.Single(line => line.StartsWith('x')).ShouldBe(new string('x', 4096) + "…");
    }

    [Fact]
    public async Task Call_past_its_deadline_is_cancelled_and_the_runner_keeps_running()
    {
        await using CSharpScriptRunner runner = await StartAsync(
            callTimeout: TimeSpan.FromSeconds(1),
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
        await Task.Delay(TimeSpan.FromMilliseconds(2500), Cancellation);
        exited.IsCompleted.ShouldBeFalse();
        runner.ProcessId.ShouldBe(processId);
        (await handler.HandleAsync(new Sample(2, []), Cancellation)).ShouldBe(2);
    }

    [Fact]
    public async Task Busy_call_past_its_deadline_kills_the_runner_and_the_next_script_gets_a_new_one()
    {
        await using CSharpScriptRunner runner = await StartAsync(
            callTimeout: TimeSpan.FromSeconds(1),
            gracePeriod: TimeSpan.FromMilliseconds(500)
        );
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
    public async Task Cancelling_an_evaluation_returns_although_the_runner_does_not_read_and_the_runner_is_killed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Stops the runner with SIGSTOP.");
        await using CSharpScriptRunner runner = await StartAsync(compileTimeout: TimeSpan.FromSeconds(3));
        int stopped = runner.ProcessId.ShouldNotBeNull();
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        Signal(stopped, "STOP");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            long start = Stopwatch.GetTimestamp();

            await Should.ThrowAsync<OperationCanceledException>(() =>
                runner.EvaluateAsync<ITestHandler>(LargerThanAPipe(Handler("return 1;")), cancellation.Token)
            );

            Stopwatch.GetElapsedTime(start).ShouldBeLessThan(TimeSpan.FromSeconds(2));
            // Nobody waits for it any more, but a runner that does not read its input is stuck.
            (await exited.WaitAsync(TimeSpan.FromSeconds(10), Cancellation)).Reason.ShouldBe(
                ScriptRunnerExitReason.Killed
            );
        }
        finally
        {
            if (IsRunning(stopped))
                Signal(stopped, "CONT");
        }
        using ITestHandler handler = await runner.EvaluateAsync<ITestHandler>(Handler("return 1;"), Cancellation);
        (await handler.HandleAsync(new Sample(0, []), Cancellation)).ShouldBe(1);
    }

    [Fact]
    public async Task Evaluating_with_a_runner_that_does_not_read_times_out_and_kills_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), "Stops the runner with SIGSTOP.");
        await using CSharpScriptRunner runner = await StartAsync(compileTimeout: TimeSpan.FromSeconds(1));
        int stopped = runner.ProcessId.ShouldNotBeNull();
        Task<ScriptRunnerExitedEventArgs> exited = runner.NextExitAsync();
        Signal(stopped, "STOP");
        try
        {
            await Should.ThrowAsync<TimeoutException>(() =>
                runner
                    .EvaluateAsync<ITestHandler>(LargerThanAPipe(Handler("return 1;")), Cancellation)
                    .WaitAsync(TimeSpan.FromSeconds(10), Cancellation)
            );

            (await exited.WaitAsync(TimeSpan.FromSeconds(10), Cancellation)).Reason.ShouldBe(
                ScriptRunnerExitReason.Killed
            );
        }
        finally
        {
            if (IsRunning(stopped))
                Signal(stopped, "CONT");
        }
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

    private static void EmitOtherBuild(string path, AssemblyName name)
    {
        string source = $$"""
            [assembly: System.Reflection.AssemblyVersion("{{name.Version}}")]
            namespace Darp.CodeEditor.CSharp.Execution.TestApi;
            public interface ITestHost { }
            public sealed class TestGlobals { public TestGlobals(ITestHost host) { } }
            """;
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        MetadataReference[] references =
        [
            .. platform
                .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
                .Select(p => MetadataReference.CreateFromFile(p)),
        ];
        EmitResult result = CSharpCompilation
            .Create(
                name.Name,
                [CSharpSyntaxTree.ParseText(source, cancellationToken: Cancellation)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            )
            .Emit(path, cancellationToken: Cancellation);
        result.Success.ShouldBeTrue(string.Join('\n', result.Diagnostics));
    }

    /// <summary>Pads a script past a pipe buffer, so sending it blocks until the runner reads.</summary>
    private static string LargerThanAPipe(string code) => code + "\n// " + new string('x', 1_000_000);
}
