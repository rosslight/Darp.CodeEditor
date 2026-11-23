using Darp.CodeEditor.CSharpScript.Protocol;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>Emits a script from its immutable snapshot, beside the editor's requests on the same text.</summary>
internal static class ScriptCompiler
{
    /// <summary>The diagnostics of <paramref name="script"/> and, without errors, its assembly.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<ScriptCompileResult> CompileAsync(
        ScriptSnapshot script,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<LspDiagnostic> diagnostics = await ScriptDiagnostics
            .GetAsync(script, cancellationToken)
            .ConfigureAwait(false);
        if (diagnostics.Any(d => d.Severity == ScriptDiagnostics.Error))
            return new ScriptCompileResult(diagnostics, null);

        Compilation? source = await script
            .Document.Project.GetCompilationAsync(cancellationToken)
            .ConfigureAwait(false);
        SyntaxTree? tree = await script.Document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
        if (source is null || tree is null)
            throw new InvalidOperationException("A C# script always has a compilation and a syntax tree.");

        // Its own identity, so that a session can load the same text twice.
        string name = $"Script_{Guid.NewGuid():N}";
        Compilation compilation = WithEntryPoint(source, tree, cancellationToken).WithAssemblyName(name);
        using var image = new MemoryStream();
        EmitResult emitted = compilation.Emit(
            image,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            cancellationToken: cancellationToken
        );
        if (!emitted.Success)
        {
            IEnumerable<Diagnostic> errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            return new ScriptCompileResult(
                [.. diagnostics, .. errors.Select(e => ScriptDiagnostics.ToLsp(script, e))],
                null
            );
        }
        IMethodSymbol entry =
            compilation.GetEntryPoint(cancellationToken)
            ?? throw new InvalidOperationException("A script with statements has an entry point.");
        return new ScriptCompileResult(
            diagnostics,
            new ScriptImage(image.ToArray(), name, entry.ContainingType.MetadataName, entry.MetadataName)
        );
    }

    /// <summary>
    /// Roslyn emits no entry point for a script of only trivia and usings, so such a script gets an empty statement
    /// after its text, where it moves no position.
    /// </summary>
    private static Compilation WithEntryPoint(
        Compilation compilation,
        SyntaxTree tree,
        CancellationToken cancellationToken
    )
    {
        if (((CompilationUnitSyntax)tree.GetRoot(cancellationToken)).Members.Count > 0)
            return compilation;
        SourceText text = tree.GetText(cancellationToken);
        return compilation.ReplaceSyntaxTree(
            tree,
            tree.WithChangedText(text.WithChanges(new TextChange(new TextSpan(text.Length, 0), "\n;")))
        );
    }
}
