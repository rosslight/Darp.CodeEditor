using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;
using UseCases.Startup;

namespace UseCases.PacketScripts;

// Scenario: a page where the user edits a packet script and presses Run (F5). Run compiles exactly the text the
// editor shows, starts the script, and feeds its handler a sample packet. Stop cancels a run. The error list shows
// the document's diagnostics; clicking one moves the caret there (see the view).
//
// - The document lives in the view model: leaving the page and coming back keeps the text and its undo history.
// - Run does not check the document's diagnostics first: the compilation has its own, for exactly the version it
//   compiled, and Run prints those.
// - Scripts write to the app's ScriptOutput service, which every page with an output panel shows.
//
// Questions:
// - Should Run wait for the latest diagnostics, or is compiling and showing the compiler's errors enough?
// - Output is per runner, not per run. Does a page need only the output of its own script?

/// <summary>The packet script page.</summary>
public sealed partial class PacketScriptViewModel : ObservableObject, IDisposable
{
    private const string DefaultScript = """
        // Decodes temperature packets: two bytes, little-endian, in hundredths of a degree.
        return new TemperatureHandler();

        sealed class TemperatureHandler : IPacketHandler
        {
            public Task<Reading> HandleAsync(Packet packet, CancellationToken cancellationToken) =>
                Task.FromResult(Decode(packet));

            public Task<Reading[]> HandleBatchAsync(Packet[] packets, CancellationToken cancellationToken) =>
                Task.FromResult(packets.Select(Decode).ToArray());

            private static Reading Decode(Packet packet) =>
                new(packet.Channel, BitConverter.ToInt16(packet.Payload) / 100.0, "°C");

            public void Dispose() { }
        }
        """;

    private readonly CSharpScriptServer _scripts;
    private readonly CSharpScriptRunner _runner;
    private readonly ScriptOutput _output;
    private IPacketHandler? _handler;

    /// <summary>Creates the page with the default script.</summary>
    public PacketScriptViewModel(CSharpScriptServer scripts, CSharpScriptRunner runner, ScriptOutput output)
    {
        _scripts = scripts;
        _runner = runner;
        _output = output;
        _output.Written += OnScriptOutput;
        Script = scripts.CreateDocument(ScriptEnvironments.Packets, DefaultScript);
    }

    /// <summary>The script the editor shows.</summary>
    public CodeDocument Script { get; }

    /// <summary>What runs printed, for the output panel.</summary>
    [ObservableProperty]
    public partial string Output { get; set; } = "";

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        CSharpScriptCompilation compilation = await _scripts.CompileAsync(Script, cancellationToken);
        if (!compilation.Succeeded)
        {
            foreach (Diagnostic error in compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                WriteLine($"({error.Start.Line},{error.Start.Column}): {error.Code} {error.Message}");
            return;
        }

        _handler?.Dispose();
        _handler = null;
        try
        {
            _handler = await _runner.RunAsync<IPacketHandler>(compilation.Script, cancellationToken);
            Reading reading = await _handler.HandleAsync(new Packet(1, [0x0B, 0x09]), cancellationToken);
            WriteLine($"Channel {reading.Channel}: {reading.Value} {reading.Unit}");
        }
        catch (ScriptException exception)
        {
            WriteLine($"The script failed: {exception.ScriptExceptionType}: {exception.Message}");
        }
        catch (OperationCanceledException)
        {
            WriteLine("Stopped.");
        }
    }

    private void WriteLine(string line) => Output += line + "\n";

    private void OnScriptOutput(string line) => Dispatcher.UIThread.Post(() => WriteLine(line));

    /// <inheritdoc />
    public void Dispose()
    {
        _output.Written -= OnScriptOutput;
        _handler?.Dispose();
        Script.Dispose();
    }
}
