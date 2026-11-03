using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Darp.CodeEditor.CSharp.Execution;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.Demo.Scripting;
using StreamJsonRpc;

namespace Darp.CodeEditor.Samples;

/// <summary>The Run action of both demos: runs a packet script in the C# script runner and reports what happened.</summary>
internal sealed class PacketScriptRun(Action<string> output) : IAsyncDisposable
{
    private readonly Action<string> _output = output;
    private CSharpScriptRunner? _runner;
    private IPacketHandler? _handler;

    /// <summary>
    /// The environment of packet scripts. NativeAOT apps ship the script API as a file next to the app, so the path is
    /// the same in both demos.
    /// </summary>
    public static CSharpScriptEnvironment Environment { get; } =
        new()
        {
            Globals = new CSharpScriptGlobals(
                Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Demo.ScriptApi.dll"),
                typeof(PacketScriptGlobals).FullName!
            ),
            FrameworkReferences = ["System.Collections", "System.Linq"],
            Imports =
            [
                "System",
                "System.Collections.Generic",
                "System.Linq",
                "System.Threading",
                "System.Threading.Tasks",
                "Darp.CodeEditor.Demo.Scripting",
            ],
        };

    /// <summary>
    /// Wires a demo window: the Run button and F5 run <paramref name="editor"/>'s text, and the results go to
    /// <paramref name="output"/>. With --run-script, runs once when the window opens, prints the output and closes.
    /// </summary>
    public static void Attach(
        Window window,
        Button button,
        TextBox output,
        CodeEditor editor,
        Action showEditor,
        DemoOptions options
    )
    {
        var run = new PacketScriptRun(line =>
        {
            if (options.RunScript)
                Console.WriteLine(line);
            Dispatcher.UIThread.Post(() =>
            {
                output.Text += line + "\n";
                output.CaretIndex = output.Text.Length;
            });
        });
        editor.Text = SampleDocuments.PacketScript;
        button.Click += async (_, _) => await RunAsync();
        window.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.F5)
                return;
            e.Handled = true;
            await RunAsync();
        };
        window.Closed += async (_, _) => await run.DisposeAsync();
        if (options.RunScript)
        {
            window.Opened += async (_, _) =>
            {
                await RunAsync();
                if (!options.Hold)
                    window.Close();
            };
        }

        async Task RunAsync()
        {
            button.IsEnabled = false;
            showEditor();
            await run.RunAsync(editor.Text);
            button.IsEnabled = true;
        }
    }

    /// <summary>Evaluates <paramref name="code"/>, then sends it one packet and a batch of 1000.</summary>
    public async Task RunAsync(string code)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            _runner ??= await CSharpScriptRunner.StartAsync(
                new CSharpScriptRunnerOptions
                {
                    Environment = Environment,
                    Services = [CSharpScriptService.Create<IDeviceApp>(new DemoDeviceApp(_output))],
                    CallTimeout = TimeSpan.FromSeconds(5),
                    Output = line => _output($"runner: {line}"),
                }
            );
            _handler?.Dispose();
            _handler = null;
            _handler = await _runner.EvaluateAsync<IPacketHandler>(code);
            _output($"Script ready after {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F0} ms");

            start = Stopwatch.GetTimestamp();
            Reading reading = await _handler.HandleAsync(new Packet(1, BitConverter.GetBytes((short)2315)));
            _output(
                $"One packet:  channel {reading.Channel}: {reading.Value:F2} {reading.Unit} "
                    + $"({Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms)"
            );

            Packet[] packets =
            [
                .. Enumerable.Range(0, 1000).Select(i => new Packet(i % 4, BitConverter.GetBytes((short)(2000 + i)))),
            ];
            start = Stopwatch.GetTimestamp();
            Reading[] readings = await _handler.HandleBatchAsync(packets);
            _output(
                $"1000 packets in one batch: average {readings.Average(r => r.Value):F2} °C "
                    + $"({Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2} ms)"
            );
        }
        catch (ScriptCompilationException exception)
        {
            _output("The script does not compile:");
            foreach (ScriptDiagnostic diagnostic in exception.Diagnostics)
                _output($"  {diagnostic}");
        }
        catch (ScriptException exception)
        {
            _output($"The script failed: {exception.ScriptExceptionType}: {exception.Message}");
        }
        catch (TimeoutException)
        {
            _output("The script did not answer in time and was stopped.");
        }
        catch (Exception exception) when (exception is ConnectionLostException or ObjectDisposedException)
        {
            _output("The runner ended. Run again to start a new one.");
        }
        catch (Exception exception)
        {
            _output($"Could not run the script: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_runner is not null)
            await _runner.DisposeAsync();
    }

    private sealed class DemoDeviceApp(Action<string> output) : IDeviceApp
    {
        private readonly Action<string> _output = output;

        public Task<double> ReadTemperatureAsync(CancellationToken cancellationToken) =>
            Task.FromResult(21.5 + Random.Shared.NextDouble() / 10);

        public Task LogAsync(string message, CancellationToken cancellationToken)
        {
            _output($"script: {message}");
            return Task.CompletedTask;
        }
    }
}
