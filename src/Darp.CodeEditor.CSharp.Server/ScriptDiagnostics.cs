using System.Collections.Immutable;
using EmmyLua.LanguageServer.Framework.Protocol.Model;
using EmmyLua.LanguageServer.Framework.Protocol.Model.Diagnostic;
using Microsoft.CodeAnalysis;
using Diagnostic = Microsoft.CodeAnalysis.Diagnostic;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
using LspDiagnostic = EmmyLua.LanguageServer.Framework.Protocol.Model.Diagnostic.Diagnostic;
using LspDiagnosticSeverity = EmmyLua.LanguageServer.Framework.Protocol.Model.Diagnostic.DiagnosticSeverity;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>Compiler diagnostics of a script. No analyzers run.</summary>
internal static class ScriptDiagnostics
{
    /// <summary>The diagnostics of the script's compilation, as the host's compilation reports them. Hidden ones are left out.</summary>
    public static async Task<ImmutableArray<Diagnostic>> GetAsync(
        Document document,
        CancellationToken cancellationToken
    )
    {
        Compilation compilation =
            await document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not provide a compilation.");
        return [.. compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity != DiagnosticSeverity.Hidden)];
    }

    public static List<LspDiagnostic> ToLsp(ImmutableArray<Diagnostic> diagnostics, LineMap lines)
    {
        var result = new List<LspDiagnostic>(diagnostics.Length);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            result.Add(
                new LspDiagnostic
                {
                    // Diagnostics without a place in the script, such as a broken reference, go to its start.
                    Range = diagnostic.Location.IsInSource
                        ? lines.GetRange(diagnostic.Location.SourceSpan)
                        : new DocumentRange(new Position(0, 0), new Position(0, 0)),
                    Severity = diagnostic.Severity switch
                    {
                        DiagnosticSeverity.Error => LspDiagnosticSeverity.Error,
                        DiagnosticSeverity.Warning => LspDiagnosticSeverity.Warning,
                        _ => LspDiagnosticSeverity.Information,
                    },
                    Code = diagnostic.Id,
                    Source = "csharp",
                    Message = diagnostic.GetMessage(),
                    Tags = GetTags(diagnostic),
                }
            );
        }
        return result;
    }

    private static List<DiagnosticTag>? GetTags(Diagnostic diagnostic)
    {
        if (diagnostic.Id is "CS0612" or "CS0618" or "CS0619")
            return [DiagnosticTag.Deprecated];
        return diagnostic.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.Unnecessary)
            ? [DiagnosticTag.Unnecessary]
            : null;
    }
}
