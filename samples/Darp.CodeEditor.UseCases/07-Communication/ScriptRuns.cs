using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using Darp.CodeEditor.CSharpScript.Runner;
using UseCases.ScriptApi;

namespace UseCases.Communication;

// The use cases in this folder show how the app and its scripts talk: app to script and script to app, each with a
// call, a callback and fast data, and script to script. This file holds what they share.

/// <summary>Helpers that the communication use cases share.</summary>
internal static class ScriptRuns
{
    /// <summary>Compiles <paramref name="code"/> in <paramref name="environment"/>. These scripts are fixed, so errors throw.</summary>
    public static async Task<CSharpScriptAssembly<TGlobals>> CompileAsync<TGlobals>(
        CSharpScriptServer scripts,
        CSharpScriptEnvironment<TGlobals> environment,
        string code,
        CancellationToken cancellationToken
    )
        where TGlobals : class
    {
        CSharpScriptCompilation<TGlobals> compilation = await scripts.CompileAsync(
            environment,
            code,
            cancellationToken
        );
        return compilation.Succeeded
            ? compilation.Script
            : throw new InvalidOperationException(
                compilation.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Message
            );
    }

    /// <summary>
    /// Observes how a run that the app stopped ended. StopAsync doesn't throw for it: ending through the stop token is
    /// expected, and anything else, such as a failure or a kill, throws here.
    /// </summary>
    public static async Task ObserveStoppedAsync(CSharpScriptRun run)
    {
        try
        {
            await run.Completion;
        }
        catch (OperationCanceledException)
        {
            // The run ended through its stop token.
        }
    }

    /// <summary>The answer for scripts that ask nothing.</summary>
    public static Task<string> NoAnswer(string question, CancellationToken cancellationToken) => Task.FromResult("");
}
