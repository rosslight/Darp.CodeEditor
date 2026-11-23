using System.Text;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// Signature help on the semantic model, since Roslyn's own service is not public. It shows the overloads of the call
/// around the caret, and asks overload resolution which one the arguments so far mean.
/// </summary>
internal static class ScriptSignatureHelp
{
    private static readonly SymbolDisplayFormat s_heading =
        SymbolDisplayFormat.MinimallyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeType
        );

    private static readonly ExpressionSyntax s_default = SyntaxFactory.LiteralExpression(
        SyntaxKind.DefaultLiteralExpression
    );

    /// <summary>The signatures of the call at the caret; null outside a call.</summary>
    public static async Task<LspSignatureHelp?> GetAsync(
        ScriptSnapshot script,
        SignatureHelpParams parameters,
        CancellationToken cancellationToken
    )
    {
        int position = script.Lines.ToOffset(parameters.Position);
        if (await script.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return null;
        SyntaxNode root = await model.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (ArgumentListAt(root, position) is not { Parent: ExpressionSyntax call } list)
            return null;
        var (candidates, resolved) = await BindAsync(script.Document, model, call, list, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
            return null;
        int argument = list.Arguments.GetSeparators().Count(separator => separator.SpanStart < position);
        string? name = list.Arguments.ElementAtOrDefault(argument)?.NameColon?.Name.Identifier.ValueText;

        int best = 0;
        if (resolved is not null)
        {
            best = Math.Max(0, candidates.FindIndex(c => SameMethod(c, resolved)));
            // With its type arguments, as the call infers them.
            candidates[best] = resolved;
        }
        List<LspSignatureInformation> signatures = [.. candidates.Select(c => ToSignature(c, argument, name))];
        return new LspSignatureHelp(
            signatures,
            KeptChoice(parameters.Context.ActiveSignatureHelp, signatures) ?? best,
            JsonValue.Create(best)
        );
    }

    /// <summary>The argument list that the caret is in; in whitespace, the one that goes on there.</summary>
    private static ArgumentListSyntax? ArgumentListAt(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);
        // Before a token, as on an indented continuation line: what the caret continues ends on the left.
        if (position <= token.SpanStart)
            token = token.GetPreviousToken();
        for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
        {
            // Statements inside a lambda are not arguments of the call around it.
            if (node is BlockSyntax)
                return null;
            if (
                node is ArgumentListSyntax list
                && position >= list.OpenParenToken.Span.End
                && (list.CloseParenToken.IsMissing || position <= list.CloseParenToken.SpanStart)
            )
            {
                return list;
            }
        }
        return null;
    }

    /// <summary>What the call can mean: a method group, a delegate's invoke method or a type's constructors.</summary>
    private static List<IMethodSymbol> Candidates(
        SemanticModel model,
        ExpressionSyntax call,
        CancellationToken cancellationToken
    )
    {
        IEnumerable<IMethodSymbol> methods = call switch
        {
            InvocationExpressionSyntax invocation
                when model.GetMemberGroup(invocation.Expression, cancellationToken) is { IsEmpty: false } group =>
                group.OfType<IMethodSymbol>(),
            InvocationExpressionSyntax invocation
                when model.GetTypeInfo(invocation.Expression, cancellationToken).Type
                    is INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => [invoke],
            BaseObjectCreationExpressionSyntax
                when model.GetTypeInfo(call, cancellationToken).Type is INamedTypeSymbol type =>
                type.InstanceConstructors,
            _ => [],
        };
        return [.. methods.Where(method => model.IsAccessible(call.SpanStart, method))];
    }

    /// <summary>
    /// The overload the call selects, and the candidates to show it among. An unfinished call is bound as if each missing
    /// argument were <c>default</c>, with the fewest added first, so that what was typed decides. It binds in a fork of
    /// the script, where it keeps its receiver and target type. The candidates then come from that fork: the script's own
    /// methods are symbols of one compilation.
    /// </summary>
    private static async Task<(List<IMethodSymbol> Candidates, IMethodSymbol? Selected)> BindAsync(
        Document document,
        SemanticModel model,
        ExpressionSyntax call,
        ArgumentListSyntax list,
        CancellationToken cancellationToken
    )
    {
        List<IMethodSymbol> candidates = Candidates(model, call, cancellationToken);
        if (model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol exact)
            return (candidates, exact);
        var completed = new SyntaxAnnotation();
        IEnumerable<int> missing = candidates
            .Select(method =>
                Math.Max(0, method.Parameters.Count(p => !p.IsOptional && !p.IsParams) - list.Arguments.Count)
            )
            .Distinct()
            .Order();
        foreach (int added in missing)
        {
            ExpressionSyntax filled = call.ReplaceNode(list, Completed(list, added))
                .WithAdditionalAnnotations(completed);
            Document fork = document.WithSyntaxRoot(
                call.SyntaxTree.GetRoot(cancellationToken).ReplaceNode(call, filled)
            );
            if (await fork.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } forked)
                break;
            SyntaxNode forkRoot = await forked.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            ExpressionSyntax forkCall = forkRoot.GetAnnotatedNodes(completed).OfType<ExpressionSyntax>().Single();
            SymbolInfo info = forked.GetSymbolInfo(forkCall, cancellationToken);
            ISymbol? selected =
                info.CandidateReason == CandidateReason.Ambiguous ? info.CandidateSymbols[0] : info.Symbol;
            if (selected is IMethodSymbol method)
                return (Candidates(forked, forkCall, cancellationToken), method);
        }
        return (candidates, null);
    }

    private static ArgumentListSyntax Completed(ArgumentListSyntax list, int added) =>
        list.WithArguments(
            SyntaxFactory.SeparatedList([
                .. list.Arguments.Select(a => a.Expression.IsMissing ? a.WithExpression(s_default) : a),
                .. Enumerable.Repeat(SyntaxFactory.Argument(s_default), added),
            ])
        );

    /// <summary>
    /// The signature the user chose by key, when it still exists. The answer before says which one the server chose, so
    /// that an overload that only followed the arguments follows them again when their types change.
    /// </summary>
    private static int? KeptChoice(JsonNode? showing, List<LspSignatureInformation> signatures)
    {
        if (
            (int?)showing?["activeSignature"] is not { } active
            || active == (int?)showing["data"]
            || showing["signatures"] is not JsonArray shown
            || active >= shown.Count
        )
        {
            return null;
        }
        string? chosen = (string?)shown[active]?["label"];
        int index = signatures.FindIndex(signature => signature.Label == chosen);
        return index < 0 ? null : index;
    }

    private static LspSignatureInformation ToSignature(IMethodSymbol method, int argument, string? argumentName)
    {
        var label = new StringBuilder(method.ToDisplayString(s_heading)).Append('(');
        var parameters = new List<LspParameterInformation>(method.Parameters.Length);
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameters.Count > 0)
                label.Append(", ");
            int start = label.Length;
            label.Append(parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            parameters.Add(new LspParameterInformation([start, label.Length]));
        }
        label.Append(')');
        return new LspSignatureInformation(
            label.ToString(),
            parameters,
            ActiveParameter(method, argument, argumentName)
        );
    }

    /// <summary>The parameter that argument number <paramref name="argument"/> goes to; -1 for none.</summary>
    private static int ActiveParameter(IMethodSymbol method, int argument, string? name)
    {
        if (name is not null)
            return method.Parameters.Select(parameter => parameter.Name).ToList().IndexOf(name);
        if (argument < method.Parameters.Length)
            return argument;
        return method.Parameters.LastOrDefault()?.IsParams == true ? method.Parameters.Length - 1 : -1;
    }

    private static bool SameMethod(IMethodSymbol a, IMethodSymbol b) =>
        SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition);
}
