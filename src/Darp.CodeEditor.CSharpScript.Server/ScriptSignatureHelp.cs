using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// Signature help on the semantic model, since Roslyn's own service is not public. It shows what the arguments around the
/// caret go to: the overloads of a call, a constructor, a constructor initializer, an attribute, an indexer or a
/// collection initializer's entry, or the type parameters of a generic name. Overload resolution says which one the
/// arguments so far mean. Each signature has its summary, and each parameter its description, from the XML docs.
/// </summary>
internal static class ScriptSignatureHelp
{
    private static readonly SymbolDisplayFormat s_minimal = SymbolDisplayFormat.MinimallyQualifiedFormat;

    private static readonly SymbolDisplayFormat s_heading = s_minimal.WithMemberOptions(
        SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeType
    );

    private static readonly SymbolDisplayFormat s_generic = s_heading
        .AddMemberOptions(SymbolDisplayMemberOptions.IncludeParameters)
        .AddGenericsOptions(SymbolDisplayGenericsOptions.IncludeTypeConstraints);

    private static readonly ExpressionSyntax s_default = SyntaxFactory.LiteralExpression(
        SyntaxKind.DefaultLiteralExpression
    );

    /// <summary>The signatures that the arguments at the caret go to; null outside arguments.</summary>
    public static async Task<LspSignatureHelp?> GetAsync(
        ScriptSnapshot script,
        SignatureHelpParams parameters,
        CancellationToken cancellationToken
    )
    {
        int position = script.Lines.ToOffset(parameters.Position);
        if (await script.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return null;
        if (
            await SiteAtAsync(script.Document, model, position, cancellationToken).ConfigureAwait(false) is not { } site
        )
            return null;

        List<ISymbol> candidates = [.. site.Candidates];
        int best = 0;
        // A selected symbol outside the candidates, as a constructor that calls itself, selects none.
        if (site.Selected is { } selected && candidates.FindIndex(c => SameSymbol(c, selected)) is >= 0 and var index)
        {
            best = index;
            // With its type arguments, as the call infers them.
            candidates[index] = selected;
        }
        List<LspSignatureInformation> signatures =
        [
            .. candidates.Select(c => ToSignature(Shape(c, site, model, position, cancellationToken), site)),
        ];
        return new LspSignatureHelp(
            signatures,
            KeptChoice(parameters.Context.ActiveSignatureHelp, signatures) ?? best,
            JsonValue.Create(best)
        );
    }

    /// <summary>
    /// The arguments at the caret and what they can go to.
    /// </summary>
    /// <param name="Candidates">What they can go to: methods, indexers, or generic types and methods; at least one.</param>
    /// <param name="Selected">The candidate that overload resolution selects; null for none.</param>
    /// <param name="Argument">The argument at the caret, from 0.</param>
    /// <param name="Name">The name of the argument at the caret, for a named one; null otherwise.</param>
    /// <param name="Settable">An attribute's fields and properties, which named arguments set; empty otherwise.</param>
    /// <param name="TypeArguments">Whether the arguments are type arguments.</param>
    private sealed record Site(
        IReadOnlyList<ISymbol> Candidates,
        ISymbol? Selected,
        int Argument,
        string? Name,
        IReadOnlyList<ISymbol> Settable,
        bool TypeArguments = false
    );

    /// <summary>
    /// The innermost arguments around the caret that go somewhere: a type argument list, or else the argument list
    /// nearest the caret that has candidates, as an array's brackets have none.
    /// </summary>
    private static async Task<Site?> SiteAtAsync(
        Document document,
        SemanticModel model,
        int position,
        CancellationToken cancellationToken
    )
    {
        SyntaxNode root = await model.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        if (TypeArgumentsAt(model, root, position, cancellationToken) is { } typeArguments)
            return typeArguments;
        foreach (SyntaxNode list in ArgumentListsAround(root, position))
        {
            if (
                await ArgumentsAsync(document, model, list, position, cancellationToken).ConfigureAwait(false) is
                { } site
            )
                return site;
        }
        return null;
    }

    /// <summary>The token that the caret continues: the one before it, also in the whitespace before the next.</summary>
    private static SyntaxToken TokenBefore(SyntaxNode root, int position)
    {
        SyntaxToken token = root.FindToken(position);
        return position <= token.SpanStart ? token.GetPreviousToken() : token;
    }

    /// <summary>
    /// The type argument list that the caret is in, written or partly written: the tokens between its <c>&lt;</c> and
    /// the caret could all be type arguments, and the name before it is generic. A <c>&lt;</c> that compares, as in
    /// <c>a &lt; b</c>, has no generic name before it.
    /// </summary>
    private static Site? TypeArgumentsAt(
        SemanticModel model,
        SyntaxNode root,
        int position,
        CancellationToken cancellationToken
    )
    {
        int argument = 0;
        int depth = 0;
        for (SyntaxToken token = TokenBefore(root, position); !token.IsKind(SyntaxKind.None); )
        {
            switch (token.Kind())
            {
                case SyntaxKind.LessThanToken when depth == 0:
                    return
                        token.Parent is TypeParameterListSyntax
                        || token.GetPreviousToken() is not { RawKind: (int)SyntaxKind.IdentifierToken } name
                        ? null
                        : GenericSite(model, name, position, argument, cancellationToken);
                case SyntaxKind.LessThanToken:
                    depth--;
                    break;
                case SyntaxKind.GreaterThanToken:
                    depth++;
                    break;
                case SyntaxKind.GreaterThanGreaterThanToken:
                    depth += 2;
                    break;
                case SyntaxKind.CommaToken:
                    argument += depth == 0 ? 1 : 0;
                    break;
                case SyntaxKind.IdentifierToken
                or SyntaxKind.DotToken
                or SyntaxKind.ColonColonToken
                or SyntaxKind.QuestionToken
                or SyntaxKind.AsteriskToken
                or SyntaxKind.OpenBracketToken
                or SyntaxKind.CloseBracketToken:
                    break;
                case var kind when SyntaxFacts.IsPredefinedType(kind):
                    break;
                default:
                    return null;
            }
            token = token.GetPreviousToken();
        }
        return null;
    }

    /// <summary>The generic types or methods that <paramref name="name"/> can mean, as Roslyn looks them up.</summary>
    private static Site? GenericSite(
        SemanticModel model,
        SyntaxToken name,
        int position,
        int argument,
        CancellationToken cancellationToken
    )
    {
        if (name.Parent is not SimpleNameSyntax simpleName)
            return null;
        ExpressionSyntax? left = simpleName.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Name == simpleName => access.Expression,
            QualifiedNameSyntax qualified when qualified.Right == simpleName => qualified.Left,
            _ => null,
        };
        ISymbol? leftSymbol = left is null ? null : model.GetSymbolInfo(left, cancellationToken).Symbol;
        ITypeSymbol? leftType = left is null ? null : model.GetTypeInfo(left, cancellationToken).Type;
        INamespaceOrTypeSymbol? container = leftSymbol as INamespaceOrTypeSymbol ?? leftType;
        IEnumerable<ISymbol> found = SyntaxFacts.IsInNamespaceOrTypeContext(simpleName)
            ? model.LookupNamespacesAndTypes(position, container, name.ValueText)
            : model.LookupSymbols(
                position,
                container,
                name.ValueText,
                includeReducedExtensionMethods: leftSymbol is not INamespaceOrTypeSymbol && leftType is not null
            );
        List<ISymbol> candidates =
        [
            .. found.Where(symbol =>
                symbol is INamedTypeSymbol { Arity: > 0 } or IMethodSymbol { Arity: > 0 }
                && SignatureCandidates.IsBrowsable(symbol)
            ),
        ];
        if (candidates.Count == 0)
            return null;
        ISymbol selected = candidates.FirstOrDefault(c => TypeParameters(c).Length > argument) ?? candidates[0];
        return new Site(candidates, selected, argument, null, [], TypeArguments: true);
    }

    private static ImmutableArray<ITypeParameterSymbol> TypeParameters(ISymbol symbol) =>
        symbol is IMethodSymbol method ? method.TypeParameters : ((INamedTypeSymbol)symbol).TypeParameters;

    /// <summary>
    /// The argument lists around the caret, innermost first; in whitespace, those that go on there. Statements inside a
    /// lambda are not arguments of the call around it.
    /// </summary>
    private static IEnumerable<SyntaxNode> ArgumentListsAround(SyntaxNode root, int position)
    {
        for (SyntaxNode? node = TokenBefore(root, position).Parent; node is not null; node = node.Parent)
        {
            if (node is BlockSyntax)
                yield break;
            (SyntaxToken open, SyntaxToken close) = node switch
            {
                ArgumentListSyntax list => (list.OpenParenToken, list.CloseParenToken),
                BracketedArgumentListSyntax list => (list.OpenBracketToken, list.CloseBracketToken),
                AttributeArgumentListSyntax list => (list.OpenParenToken, list.CloseParenToken),
                InitializerExpressionSyntax entry when entry.IsKind(SyntaxKind.ComplexElementInitializerExpression) => (
                    entry.OpenBraceToken,
                    entry.CloseBraceToken
                ),
                _ => default,
            };
            if (
                !open.IsKind(SyntaxKind.None)
                && position >= open.Span.End
                && (close.IsMissing || position <= close.SpanStart)
            )
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// What the arguments in <paramref name="list"/> can go to; null for nothing. An unfinished list is bound as if each
    /// missing argument were <c>default</c>, with the fewest added first, so that what was typed decides. It binds in a
    /// fork of the script, where it keeps its receiver and target type. The candidates then come from that fork: the
    /// script's own methods are symbols of one compilation.
    /// </summary>
    private static async Task<Site?> ArgumentsAsync(
        Document document,
        SemanticModel model,
        SyntaxNode list,
        int position,
        CancellationToken cancellationToken
    )
    {
        List<ISymbol> candidates = SignatureCandidates.Of(model, list, cancellationToken);
        if (candidates.Count == 0)
            return null;
        (int argument, string? name) = ArgumentAt(list, position);
        IReadOnlyList<ISymbol> settable =
            list.Parent is AttributeSyntax attribute
            && model.GetTypeInfo(attribute, cancellationToken).Type is INamedTypeSymbol attributeType
                ? SignatureCandidates.Settable(attributeType, model, position)
                : [];
        if (Bound(model, list, cancellationToken) is { } exact)
            return new Site(candidates, exact, argument, name, settable);

        int written = Arguments(list).Count();
        var completed = new SyntaxAnnotation();
        IEnumerable<int> missing = candidates
            .Select(c => Math.Max(0, Parameters(c).Count(p => !p.IsOptional && !p.IsParams) - written))
            .Distinct()
            .Order();
        foreach (int added in missing)
        {
            Document fork = document.WithSyntaxRoot(
                list.SyntaxTree.GetRoot(cancellationToken)
                    .ReplaceNode(list, Completed(list, added).WithAdditionalAnnotations(completed))
            );
            if (await fork.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } forked)
                break;
            SyntaxNode forkRoot = await forked.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
            SyntaxNode forkList = forkRoot.GetAnnotatedNodes(completed).Single();
            if (Bound(forked, forkList, cancellationToken) is { } selected)
                return new Site(
                    SignatureCandidates.Of(forked, forkList, cancellationToken),
                    selected,
                    argument,
                    name,
                    settable
                );
        }
        return new Site(candidates, null, argument, name, settable);
    }

    /// <summary>The argument at the caret, from 0, and its name, for a named one.</summary>
    private static (int Argument, string? Name) ArgumentAt(SyntaxNode list, int position)
    {
        (IReadOnlyList<SyntaxNode> arguments, IEnumerable<SyntaxToken> separators) = list switch
        {
            BaseArgumentListSyntax l => (l.Arguments, l.Arguments.GetSeparators()),
            AttributeArgumentListSyntax l => (l.Arguments, l.Arguments.GetSeparators()),
            InitializerExpressionSyntax l => ((IReadOnlyList<SyntaxNode>)l.Expressions, l.Expressions.GetSeparators()),
            _ => ([], []),
        };
        int argument = separators.Count(separator => separator.SpanStart < position);
        string? name = arguments.ElementAtOrDefault(argument) switch
        {
            ArgumentSyntax { NameColon: { } colon } => colon.Name.Identifier.ValueText,
            AttributeArgumentSyntax { NameColon: { } colon } => colon.Name.Identifier.ValueText,
            AttributeArgumentSyntax { NameEquals: { } equals } => equals.Name.Identifier.ValueText,
            _ => null,
        };
        return (argument, name);
    }

    private static IEnumerable<SyntaxNode> Arguments(SyntaxNode list) =>
        list switch
        {
            BaseArgumentListSyntax arguments => arguments.Arguments,
            // Positional ones: named ones set members.
            AttributeArgumentListSyntax arguments => arguments.Arguments.Where(a => a.NameEquals is null),
            InitializerExpressionSyntax entry => entry.Expressions,
            _ => [],
        };

    /// <summary><paramref name="list"/> with <c>default</c> for each missing argument, and <paramref name="added"/> more.</summary>
    private static SyntaxNode Completed(SyntaxNode list, int added) =>
        list switch
        {
            BaseArgumentListSyntax arguments => arguments.WithArguments(
                SyntaxFactory.SeparatedList([
                    .. arguments.Arguments.Select(a => a.Expression.IsMissing ? a.WithExpression(s_default) : a),
                    .. Enumerable.Repeat(SyntaxFactory.Argument(s_default), added),
                ])
            ),
            AttributeArgumentListSyntax arguments => arguments.WithArguments(
                SyntaxFactory.SeparatedList([
                    .. arguments.Arguments.Select(a => a.Expression.IsMissing ? a.WithExpression(s_default) : a),
                    .. Enumerable.Repeat(SyntaxFactory.AttributeArgument(s_default), added),
                ])
            ),
            InitializerExpressionSyntax entry => entry.WithExpressions(
                SyntaxFactory.SeparatedList([
                    .. entry.Expressions.Select(e => e.IsMissing ? s_default : e),
                    .. Enumerable.Repeat(s_default, added),
                ])
            ),
            _ => list,
        };

    /// <summary>The method or indexer that overload resolution selects for the arguments in <paramref name="list"/>.</summary>
    private static ISymbol? Bound(SemanticModel model, SyntaxNode list, CancellationToken cancellationToken)
    {
        SymbolInfo info = list is InitializerExpressionSyntax entry
            ? model.GetCollectionInitializerSymbolInfo(entry, cancellationToken)
            : model.GetSymbolInfo(list.Parent ?? list, cancellationToken);
        return info.CandidateReason == CandidateReason.Ambiguous ? info.CandidateSymbols[0] : info.Symbol;
    }

    private static IReadOnlyList<IParameterSymbol> Parameters(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.Parameters,
            IPropertySymbol property => property.Parameters,
            _ => [],
        };

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

    /// <summary>A signature as it shows: its parameters between the text before and after them, and its summary.</summary>
    private sealed record SignatureShape(
        string Before,
        IReadOnlyList<ParameterShape> Parameters,
        string After,
        string? Summary
    );

    /// <param name="Lead">What goes before it, outside its part of the label.</param>
    /// <param name="Text">Its part of the label.</param>
    /// <param name="Name">The name that a named argument gives it.</param>
    /// <param name="IsParams">Whether further arguments go to it too.</param>
    /// <param name="Description">What it is for; null for nothing.</param>
    /// <param name="IsNamedOnly">Whether only a named argument goes to it, as to an attribute's property.</param>
    private sealed record ParameterShape(
        string Lead,
        string Text,
        string Name,
        bool IsParams,
        string? Description,
        bool IsNamedOnly = false
    );

    private static SignatureShape Shape(
        ISymbol symbol,
        Site site,
        SemanticModel model,
        int position,
        CancellationToken cancellationToken
    )
    {
        // A delegate's parameters are described on the delegate, an extension method's where it is declared.
        ISymbol documented = symbol switch
        {
            IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } invoke => invoke.ContainingType,
            IMethodSymbol { ReducedFrom: { } declared } => declared,
            _ => symbol,
        };
        XmlDocs docs = XmlDocs.Of(documented.OriginalDefinition, model, position, cancellationToken);
        if (site.TypeArguments)
            return TypeParameterShape(symbol, docs);
        IEnumerable<ParameterShape> parameters = Parameters(symbol)
            .Select(p => new ParameterShape(
                "",
                p.ToDisplayString(s_minimal),
                p.Name,
                p.IsParams,
                docs.Parameter(p.Name)
            ));
        if (symbol is IPropertySymbol indexer)
        {
            string before =
                $"{indexer.Type.ToDisplayString(s_minimal)} {indexer.ContainingType.ToDisplayString(s_minimal)}[";
            return new SignatureShape(before, [.. parameters], "]", docs.Summary);
        }
        var method = (IMethodSymbol)symbol;
        string heading =
            method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.ToDisplayString(s_minimal)
                : method.ToDisplayString(s_heading);
        // An attribute's settable members follow its constructor's parameters, as Roslyn shows them.
        IEnumerable<ParameterShape> settable = site.Settable.Select(
            (member, index) =>
                new ParameterShape(
                    index == 0 ? "Properties: " : "",
                    $"{member.Name} = {TypeOf(member).ToDisplayString(s_minimal)}",
                    member.Name,
                    false,
                    XmlDocs.Of(member, model, position, cancellationToken).Summary,
                    IsNamedOnly: true
                )
        );
        return new SignatureShape(heading + "(", [.. parameters, .. settable], ")", docs.Summary);
    }

    private static ITypeSymbol TypeOf(ISymbol member) =>
        member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;

    /// <summary>A generic type or method with its type parameters as the parameters, and its constraints.</summary>
    private static SignatureShape TypeParameterShape(ISymbol symbol, XmlDocs docs)
    {
        List<SymbolDisplayPart> parts = [.. symbol.ToDisplayParts(s_generic)];
        int name = parts.FindIndex(part => SymbolEqualityComparer.Default.Equals(part.Symbol, symbol));
        List<int> typeParameters =
        [
            .. Enumerable
                .Range(name + 1, parts.Count - name - 1)
                .Where(i => parts[i].Kind == SymbolDisplayPartKind.TypeParameterName)
                .Take(TypeParameters(symbol).Length),
        ];
        return new SignatureShape(
            string.Concat(parts[..typeParameters[0]]),
            [
                .. typeParameters.Select(i => new ParameterShape(
                    "",
                    parts[i].ToString(),
                    parts[i].ToString(),
                    false,
                    docs.TypeParameter(parts[i].ToString())
                )),
            ],
            string.Concat(parts[(typeParameters[^1] + 1)..]),
            docs.Summary
        );
    }

    private static LspSignatureInformation ToSignature(SignatureShape shape, Site site)
    {
        var label = new StringBuilder(shape.Before);
        var parameters = new List<LspParameterInformation>(shape.Parameters.Count);
        foreach (ParameterShape parameter in shape.Parameters)
        {
            if (parameters.Count > 0)
                label.Append(", ");
            label.Append(parameter.Lead);
            int start = label.Length;
            label.Append(parameter.Text);
            parameters.Add(new LspParameterInformation([start, label.Length], Documentation(parameter.Description)));
        }
        label.Append(shape.After);
        return new LspSignatureInformation(
            label.ToString(),
            parameters,
            ActiveParameter(shape.Parameters, site.Argument, site.Name),
            Documentation(shape.Summary)
        );
    }

    private static LspMarkupContent? Documentation(string? text) =>
        text is null ? null : new LspMarkupContent("markdown", Markdown.Escape(text));

    /// <summary>
    /// The parameter that argument number <paramref name="argument"/> goes to, by its name, or else by its place among
    /// those that take an argument by place; -1 for none.
    /// </summary>
    private static int ActiveParameter(IReadOnlyList<ParameterShape> parameters, int argument, string? name)
    {
        if (name is not null)
            return parameters.Select(parameter => parameter.Name).ToList().IndexOf(name);
        int byPlace = parameters.Count(parameter => !parameter.IsNamedOnly);
        if (argument < byPlace)
            return argument;
        return byPlace > 0 && parameters[byPlace - 1].IsParams ? byPlace - 1 : -1;
    }

    private static bool SameSymbol(ISymbol a, ISymbol b) =>
        SymbolEqualityComparer.Default.Equals(a.OriginalDefinition, b.OriginalDefinition);
}
