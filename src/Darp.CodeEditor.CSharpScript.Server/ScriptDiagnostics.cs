using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>A script's errors and warnings, as the compiler reports them, and its directives that scripts can't use.</summary>
internal static class ScriptDiagnostics
{
    public const int Error = 1;
    private const int Warning = 2;

    /// <summary>What every diagnostic of the server names as its source.</summary>
    private const string Source = "csharp";

    /// <summary>The diagnostics of <paramref name="script"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<IReadOnlyList<LspDiagnostic>> GetAsync(
        ScriptSnapshot script,
        CancellationToken cancellationToken
    )
    {
        var diagnostics = new List<LspDiagnostic>();
        if (script.EnvironmentError is { } environmentError)
            diagnostics.Add(new LspDiagnostic(ToRange(script, default), environmentError, Error, Source: Source));
        var rejected = new List<TextSpan>();
        if (await script.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root)
        {
            foreach (DirectiveTriviaSyntax directive in UnsupportedDirectives(root))
            {
                string message = $"The #{directive.DirectiveNameToken.Text} directive is not available in scripts.";
                diagnostics.Add(new LspDiagnostic(ToRange(script, directive.Span), message, Error, Source: Source));
                rejected.Add(directive.Span);
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
                // A directive that scripts can't use has one error, its own: the compiler's, such as that #r is not
                // supported or comes too late, would say the same twice.
                if (
                    diagnostic.Location.IsInSource
                    && rejected.Any(span => span.Contains(diagnostic.Location.SourceSpan))
                )
                    continue;
                diagnostics.Add(ToLsp(script, diagnostic));
            }
        }
        return diagnostics;
    }

    /// <summary>A compiler diagnostic in the client's positions, its code linked to the page its descriptor names.</summary>
    public static LspDiagnostic ToLsp(ScriptSnapshot script, Diagnostic diagnostic) =>
        new(
            ToRange(script, diagnostic.Location.IsInSource ? diagnostic.Location.SourceSpan : default),
            diagnostic.GetMessage(),
            diagnostic.Severity == DiagnosticSeverity.Error ? Error : Warning,
            diagnostic.Id,
            Source,
            Uri.IsWellFormedUriString(diagnostic.Descriptor.HelpLinkUri, UriKind.Absolute)
                ? new LspCodeDescription(diagnostic.Descriptor.HelpLinkUri)
                : null
        );

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
