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
// - Run does not check the diagnostics first: CompileAsync reports the same errors for the text it compiled.
//
// Questions:
// - Should Run wait for the latest diagnostics, or is compiling and showing the compiler's errors enough?
// - The script's console output goes to the runner's Output callback, shared by all scripts of the runner. Should
//   output be per run instead?

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
    private IPacketHandler? _handler;

    /// <summary>Creates the page with the default script.</summary>
    public PacketScriptViewModel(CSharpScriptServer scripts, CSharpScriptRunner runner, ScriptLanguages languages)
    {
        _scripts = scripts;
        _runner = runner;
        Script = new CodeDocument(languages.Packets, DefaultScript);
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
            WriteLine("The script has errors; see the error list.");
            return;
        }

        _handler?.Dispose();
        _handler = null;
        try
        {
            _handler = await _runner.RunAsync<IPacketHandler>(
                compilation.Script,
                [CSharpScriptService.Create<IOutputLog>(new OutputLog(this))],
                cancellationToken
            );
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

    /// <inheritdoc />
    public void Dispose()
    {
        _handler?.Dispose();
        Script.Dispose();
    }

    /// <summary>The <see cref="IOutputLog"/> that scripts write to: this page's output panel.</summary>
    private sealed class OutputLog(PacketScriptViewModel page) : IOutputLog
    {
        public Task WriteAsync(string message, CancellationToken cancellationToken = default)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => page.WriteLine(message));
            return Task.CompletedTask;
        }
    }
}
