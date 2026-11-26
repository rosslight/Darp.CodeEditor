using System.Collections.Immutable;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// The occurrences of the symbol at a position, by the symbol's identity: its declarations in the script, as text, as
/// Roslyn's own server answers, and its references, as reads or writes.
/// </summary>
internal static class ScriptHighlights
{
    /// <summary>The occurrences, in order; none where there is no symbol.</summary>
    public static async Task<LspDocumentHighlight[]?> GetAsync(
        ScriptSnapshot script,
        LspPosition position,
        CancellationToken cancellationToken
    )
    {
        Document document = script.Document;
        ISymbol? symbol = await SymbolFinder
            .FindSymbolAtPositionAsync(document, script.Lines.ToOffset(position), cancellationToken)
            .ConfigureAwait(false);
        if (
            symbol is null
            || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
        )
            return [];
        IEnumerable<ReferencedSymbol> found = await SymbolFinder
            .FindReferencesAsync(
                symbol,
                document.Project.Solution,
                ImmutableHashSet.Create(document),
                cancellationToken
            )
            .ConfigureAwait(false);
        var occurrences = new Dictionary<TextSpan, int>();
        foreach (ReferencedSymbol referenced in found)
        {
            foreach (Location location in referenced.Definition.Locations)
            {
                if (location.SourceTree == root.SyntaxTree)
                    occurrences[location.SourceSpan] = DocumentHighlightKind.Text;
            }
            foreach (ReferenceLocation reference in referenced.Locations)
            {
                TextSpan span = reference.Location.SourceSpan;
                if (!reference.IsImplicit && reference.Location.SourceTree == root.SyntaxTree)
                    occurrences.TryAdd(
                        span,
                        IsWritten(root, span) ? DocumentHighlightKind.Write : DocumentHighlightKind.Read
                    );
            }
        }
        return
        [
            .. occurrences
                .OrderBy(occurrence => occurrence.Key.Start)
                .Select(occurrence => new LspDocumentHighlight(
                    new LspRange(
                        script.Lines.ToPosition(occurrence.Key.Start),
                        script.Lines.ToPosition(occurrence.Key.End)
                    ),
                    occurrence.Value
                )),
        ];
    }

    /// <summary>
    /// Whether the name at <paramref name="span"/> is written: assigned, also as a member or in a deconstruction,
    /// incremented or decremented, or passed by <c>out</c> or <c>ref</c>.
    /// </summary>
    private static bool IsWritten(SyntaxNode root, TextSpan span)
    {
        SyntaxNode target = root.FindNode(span, getInnermostNodeForTie: true);
        while (target.Parent is { } parent && IsPartOfTarget(parent, target))
            target = parent;
        return target.Parent switch
        {
            AssignmentExpressionSyntax assignment => assignment.Left == target,
            PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax => target.Parent.Kind()
                is SyntaxKind.PreIncrementExpression
                    or SyntaxKind.PreDecrementExpression
                    or SyntaxKind.PostIncrementExpression
                    or SyntaxKind.PostDecrementExpression,
            ArgumentSyntax argument => argument.RefKindKeyword.Kind() is SyntaxKind.OutKeyword or SyntaxKind.RefKeyword,
            _ => false,
        };
    }

    /// <summary>
    /// Whether <paramref name="parent"/> is written where <paramref name="child"/> is: the name of a member access, in
    /// parentheses or in a tuple.
    /// </summary>
    private static bool IsPartOfTarget(SyntaxNode parent, SyntaxNode child) =>
        parent switch
        {
            MemberAccessExpressionSyntax access => access.Name == child,
            ParenthesizedExpressionSyntax
            or TupleExpressionSyntax
            or ArgumentSyntax { Parent: TupleExpressionSyntax } => true,
            _ => false,
        };
}
