using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>A script's errors and warnings, as the compiler reports them, and its directives that scripts can't use.</summary>
internal static class ScriptDiagnostics
{
    private const int Error = 1;
    private const int Warning = 2;

    /// <summary>The diagnostics of <paramref name="script"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<IReadOnlyList<LspDiagnostic>> GetAsync(
        ScriptSnapshot script,
        CancellationToken cancellationToken
    )
    {
        var diagnostics = new List<LspDiagnostic>();
        if (script.EnvironmentError is { } environmentError)
            diagnostics.Add(new LspDiagnostic(ToRange(script, default), environmentError, Error));
        if (await script.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root)
        {
            foreach (DirectiveTriviaSyntax directive in UnsupportedDirectives(root))
            {
                string message = $"The #{directive.DirectiveNameToken.Text} directive is not available in scripts.";
                diagnostics.Add(new LspDiagnostic(ToRange(script, directive.Span), message, Error));
            }
        }
        if (
            await script.Document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is
            { } compilation
        )
        {
            foreach (Diagnostic diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (diagnostic.Severity < DiagnosticSeverity.Warning || diagnostic.IsSuppressed)
                    continue;
                diagnostics.Add(
                    new LspDiagnostic(
                        ToRange(script, diagnostic.Location.IsInSource ? diagnostic.Location.SourceSpan : default),
                        diagnostic.GetMessage(),
                        diagnostic.Severity == DiagnosticSeverity.Error ? Error : Warning,
                        diagnostic.Id
                    )
                );
            }
        }
        return diagnostics;
    }

    private static IEnumerable<DirectiveTriviaSyntax> UnsupportedDirectives(SyntaxNode root)
    {
        for (
            DirectiveTriviaSyntax? directive = root.GetFirstDirective(IsUnsupported);
            directive is not null;
            directive = directive.GetNextDirective(IsUnsupported)
        )
        {
            yield return directive;
        }
    }

    private static bool IsUnsupported(DirectiveTriviaSyntax directive) =>
        directive.IsActive
        && directive
            is ReferenceDirectiveTriviaSyntax
                or LoadDirectiveTriviaSyntax
                or LineDirectiveTriviaSyntax
                or LineSpanDirectiveTriviaSyntax;

    private static LspRange ToRange(ScriptSnapshot script, TextSpan span) =>
        new(script.Lines.ToPosition(span.Start), script.Lines.ToPosition(span.End));
}
