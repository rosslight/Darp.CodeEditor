using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.Startup;

namespace UseCases.Headless;

// Scenario: the app runs the sequence script the user saved last time, at startup or on a schedule, with no editor
// open. The server compiles the file's text in the sequence environment, the same one their editors use; the runner
// runs it. The app's Stop button cancels it.
//
// - Compiling needs the C# server, so the first run after app start waits for Roslyn to start (under a second with
//   ReadyToRun in the prototype).
// - The runner package depends on the C# package, which depends on the editor package and Avalonia. Running scripts
//   in an app without Avalonia, such as a console tool, would need the compiler in a package of its own.
//
// Questions:
// - Is there an app that runs scripts without Avalonia?
// - Should a compiled script be storable, so that later runs skip the compiler entirely?

/// <summary>Runs saved sequence scripts.</summary>
public sealed class SavedSequences
{
    private readonly CSharpScriptServer _scripts;
    private readonly CSharpScriptRunner _runner;
    private readonly ScriptOutput _log;

    /// <summary>Runs sequences with <paramref name="scripts"/> and <paramref name="runner"/>.</summary>
    public SavedSequences(CSharpScriptServer scripts, CSharpScriptRunner runner, ScriptOutput log)
    {
        _scripts = scripts;
        _runner = runner;
        _log = log;
    }

    /// <summary>Compiles and runs the sequence in <paramref name="path"/>; <paramref name="stop"/> stops it.</summary>
    /// <returns>Whether the sequence compiled and ran to its end.</returns>
    public async Task<bool> RunAsync(string path, CancellationToken stop)
    {
        string code = await File.ReadAllTextAsync(path, stop);
        CSharpScriptCompilation compilation = await _scripts.CompileAsync(code, ScriptEnvironments.Sequences, stop);
        if (!compilation.Succeeded)
        {
            foreach (Diagnostic error in compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                await _log.WriteAsync($"{path}({error.Start.Line},{error.Start.Column}): {error.Message}", stop);
            return false;
        }

        await _runner.RunAsync(compilation.Script, stop);
        return true;
    }
}
