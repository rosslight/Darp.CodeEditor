using Darp.CodeEditor;
using Darp.CodeEditor.CSharpScript;
using UseCases.ScriptApi;

namespace UseCases.Communication;

// The use cases in this folder show how the app and its scripts talk: app to script and script to app, each with a
// call, a callback and fast data, and script to script. This file holds what they share.

/// <summary>Helpers that the communication use cases share.</summary>
internal static class ScriptRuns
{
    /// <summary>Compiles <paramref name="code"/> in <paramref name="environment"/>. These scripts are fixed, so errors throw.</summary>
    public static async Task<CompiledScript<Session, TInput>> CompileAsync<TInput>(
        CSharpScriptServer scripts,
        string code,
        CSharpScriptEnvironment<Session, TInput> environment,
        CancellationToken cancellationToken
    )
    {
        CSharpScriptCompilation<Session, TInput> compilation = await scripts.CompileAsync(
            code,
            environment,
            cancellationToken
        );
        return compilation.Succeeded
            ? compilation.Script
            : throw new InvalidOperationException(
                compilation.Diagnostics.First(d => d.Severity == DiagnosticSeverity.Error).Message
            );
    }

    /// <summary>The answer for scripts that ask nothing.</summary>
    public static Task<string> NoAnswer(string question, CancellationToken cancellationToken) => Task.FromResult("");
}
