using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/signatureHelp</c> for method calls and object creations.</summary>
/// <remarks>
/// Roslyn's signature help service is internal, so this works with the semantic model: every accessible overload of
/// the called member, the parameter the caret is in (also for named arguments), and the best overload for the
/// arguments typed so far.
/// </remarks>
internal static partial class SignatureHelpHandler
{
    private const SymbolDisplayMiscellaneousOptions DisplayOptions =
        SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
        | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier;

    private static readonly SymbolDisplayFormat s_methodFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeContainingType,
        miscellaneousOptions: DisplayOptions
    );

    private static readonly SymbolDisplayFormat s_typeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: DisplayOptions
    );

    private static readonly SymbolDisplayFormat s_parameterFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeName
            | SymbolDisplayParameterOptions.IncludeDefaultValue
            | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: DisplayOptions
    );

    public static SignatureHelpOptions Options { get; } =
        new(TriggerCharacters: ["(", ","], RetriggerCharacters: [")"]);

    public static void Register(JsonRpcConnection connection, ScriptSession session) =>
        connection.OnRequest(LspMethods.SignatureHelp, (request, token) => HandleAsync(session, request, token));

    private static async Task<SignatureHelp?> HandleAsync(
        ScriptSession session,
        SignatureHelpParams request,
        CancellationToken token
    )
    {
        if (!session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return null;
        int position = snapshot.Lines.GetOffset(request.Position);
        SignatureHelp? previous = request.Context?.ActiveSignatureHelp;
        ClientFeatures client = session.Client;
        // The snapshot is taken; the rest runs on the thread pool while later messages are dispatched.
        await Task.Yield();
        return await StaleWork
            .RunAsync(
                token,
                snapshot.Changed,
                async cancellationToken =>
                    await GetSignatureHelpAsync(
                            snapshot.Owner.GetDocument(
                                await session.Workspace.WaitAsync(cancellationToken).ConfigureAwait(false),
                                snapshot
                            ),
                            position,
                            previous,
                            client,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    /// <param name="previous">The help the client shows, whose overload stays selected while it still fits.</param>
    public static async Task<SignatureHelp?> GetSignatureHelpAsync(
        Document document,
        int position,
        SignatureHelp? previous,
        ClientFeatures client,
        CancellationToken cancellationToken
    )
    {
        SyntaxNode root =
            await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not provide a syntax tree.");
        // The token before the caret: at the end of the document, the token at the caret is the end of file.
        ArgumentListSyntax? arguments = root.FindToken(Math.Max(0, position - 1))
            .Parent?.AncestorsAndSelf()
            .OfType<ArgumentListSyntax>()
            .FirstOrDefault(a =>
                a.OpenParenToken.Span.End <= position
                && (a.CloseParenToken.IsMissing || position <= a.CloseParenToken.SpanStart)
                && a.Parent is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax
            );
        if (arguments?.Parent is not ExpressionSyntax call)
            return null;

        SemanticModel model =
            await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not provide a semantic model.");
        ImmutableArray<IMethodSymbol> methods = GetCandidates(model, call, position, cancellationToken);
        if (methods.IsEmpty)
            return null;

        int argumentIndex = arguments.Arguments.GetSeparators().Count(s => s.SpanStart < position);
        string? argumentName =
            argumentIndex < arguments.Arguments.Count
                ? arguments.Arguments[argumentIndex].NameColon?.Name.Identifier.ValueText
                : null;
        List<SignatureInformation> signatures =
        [
            .. methods.Select(m =>
                CreateSignature(m, GetParameterIndex(m, argumentIndex, argumentName), client, cancellationToken)
            ),
        ];
        int active = ChooseSignature(
            model,
            call,
            methods,
            arguments,
            argumentIndex,
            signatures,
            previous,
            cancellationToken
        );
        return new SignatureHelp(
            signatures,
            active,
            OutsideIfMissing(GetParameterIndex(methods[active], argumentIndex, argumentName), methods[active])
        );
    }

    private static ImmutableArray<IMethodSymbol> GetCandidates(
        SemanticModel model,
        ExpressionSyntax call,
        int position,
        CancellationToken cancellationToken
    )
    {
        switch (call)
        {
            case InvocationExpressionSyntax invocation:
                ImmutableArray<IMethodSymbol> group =
                [
                    .. model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>(),
                ];
                if (!group.IsEmpty)
                    return group;
                // A delegate, such as a Func<int, int> local, is called through its Invoke method.
                return
                    model.GetTypeInfo(invocation.Expression, cancellationToken).Type
                        is INamedTypeSymbol { DelegateInvokeMethod: { } invoke }
                    ? [invoke]
                    : [];
            case BaseObjectCreationExpressionSyntax creation:
                return model.GetTypeInfo(creation, cancellationToken).Type is INamedTypeSymbol type
                    ? [.. type.InstanceConstructors.Where(c => model.IsAccessible(position, c))]
                    : [];
            default:
                return [];
        }
    }

    /// <summary>
    /// The overload the call binds to; otherwise the client's current one while the typed arguments still fit it;
    /// otherwise the one whose parameters fit the typed arguments best.
    /// </summary>
    private static int ChooseSignature(
        SemanticModel model,
        ExpressionSyntax call,
        ImmutableArray<IMethodSymbol> methods,
        ArgumentListSyntax arguments,
        int argumentIndex,
        List<SignatureInformation> signatures,
        SignatureHelp? previous,
        CancellationToken cancellationToken
    )
    {
        if (model.GetSymbolInfo(call, cancellationToken).Symbol is IMethodSymbol bound)
        {
            int index = methods.IndexOf(bound, Normalized.Instance);
            if (index >= 0)
                return index;
        }
        int[] scores = [.. methods.Select(m => Score(model, m, arguments, argumentIndex))];
        if (
            previous is { ActiveSignature: { } previousIndex, Signatures: { } previousSignatures }
            && previousIndex < previousSignatures.Count
        )
        {
            string label = previousSignatures[previousIndex].Label;
            int same = signatures.FindIndex(s => s.Label == label);
            if (same >= 0 && scores[same] >= 0)
                return same;
        }
        int best = 0;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[best])
                best = i;
        }
        return best;
    }

    /// <summary>
    /// How well the typed arguments fit <paramref name="method"/>: -1 when they cannot, else 2 for each argument of the
    /// parameter's type and 1 for each other implicit conversion.
    /// </summary>
    private static int Score(SemanticModel model, IMethodSymbol method, ArgumentListSyntax arguments, int argumentIndex)
    {
        int score = 0;
        SeparatedSyntaxList<ArgumentSyntax> list = arguments.Arguments;
        // The argument at the caret needs a parameter even while it is still empty.
        for (int i = 0; i < Math.Max(list.Count, argumentIndex + 1); i++)
        {
            ArgumentSyntax? argument = i < list.Count ? list[i] : null;
            IParameterSymbol? parameter = GetParameter(method, i, argument?.NameColon?.Name.Identifier.ValueText);
            if (parameter is null)
                return -1;
            if (argument is null || argument.Expression.IsMissing)
                continue;
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
            {
                if (parameter.RefKind == RefKind.None)
                    return -1;
                score++;
                continue;
            }
            Conversion conversion = model.ClassifyConversion(argument.Expression, parameter.Type);
            if (
                !conversion.IsImplicit
                && parameter is { IsParams: true, Type: IArrayTypeSymbol { ElementType: { } element } }
            )
            {
                conversion = model.ClassifyConversion(argument.Expression, element);
            }
            if (!conversion.IsImplicit)
                return -1;
            score += conversion.IsIdentity ? 2 : 1;
        }
        return score;
    }

    private static SignatureInformation CreateSignature(
        IMethodSymbol method,
        int activeParameter,
        ClientFeatures client,
        CancellationToken cancellationToken
    )
    {
        (string? summary, Dictionary<string, string> parameterDocs) = ReadDocumentation(method, cancellationToken);
        var label = new StringBuilder(
            method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.ToDisplayString(s_typeFormat)
                : method.ToDisplayString(s_methodFormat)
        );
        label.Append('(');
        var parameters = new List<ParameterInformation>(method.Parameters.Length);
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameters.Count > 0)
                label.Append(", ");
            int start = label.Length;
            string text = parameter.ToDisplayString(s_parameterFormat);
            label.Append(text);
            parameters.Add(
                new ParameterInformation(
                    client.SignatureLabelOffsets ? new ParameterLabel(start, label.Length) : new ParameterLabel(text),
                    parameterDocs.TryGetValue(parameter.Name, out string? doc)
                        ? Markup.Create(null, [doc], client.SignatureMarkdown)
                        : null
                )
            );
        }
        label.Append(')');
        return new SignatureInformation(
            label.ToString(),
            summary is null ? null : Markup.Create(null, [summary], client.SignatureMarkdown),
            parameters,
            client.SignatureActiveParameter ? OutsideIfMissing(activeParameter, method) : null
        );
    }

    /// <summary>The parameter an argument goes to: by name, by position, or the params array.</summary>
    private static IParameterSymbol? GetParameter(IMethodSymbol method, int argumentIndex, string? name)
    {
        if (name is not null)
            return method.Parameters.FirstOrDefault(p => p.Name == name);
        if (argumentIndex < method.Parameters.Length)
            return method.Parameters[argumentIndex];
        return method.Parameters is [.., { IsParams: true } last] ? last : null;
    }

    private static int GetParameterIndex(IMethodSymbol method, int argumentIndex, string? name) =>
        GetParameter(method, argumentIndex, name) is { } parameter ? method.Parameters.IndexOf(parameter) : -1;

    /// <summary>No parameter is active when the argument has none; LSP expresses that with an index past the end.</summary>
    private static int OutsideIfMissing(int index, IMethodSymbol method) =>
        index >= 0 ? index : method.Parameters.Length;

    private static (string? Summary, Dictionary<string, string> Parameters) ReadDocumentation(
        IMethodSymbol method,
        CancellationToken cancellationToken
    )
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        string? xml = (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentXml(
            cancellationToken: cancellationToken
        );
        if (string.IsNullOrWhiteSpace(xml))
            return (null, parameters);
        try
        {
            XElement root = XElement.Parse(xml);
            foreach (XElement parameter in root.Elements("param"))
            {
                if (parameter.Attribute("name")?.Value is { } name)
                    parameters[name] = ToText(parameter);
            }
            return (root.Element("summary") is { } summary ? ToText(summary) : null, parameters);
        }
        catch (XmlException)
        {
            return (null, parameters);
        }
    }

    /// <summary>The text of a documentation element, with references shown by their short names.</summary>
    private static string ToText(XElement element)
    {
        var builder = new StringBuilder();
        foreach (XNode node in element.Nodes())
        {
            builder.Append(
                node switch
                {
                    XText text => text.Value,
                    XElement { Name.LocalName: "see" or "seealso" } reference => reference.Attribute("langword")?.Value
                        ?? ShortName(reference.Attribute("cref")?.Value),
                    XElement { Name.LocalName: "paramref" or "typeparamref" } reference => reference
                        .Attribute("name")
                        ?.Value,
                    XElement nested => ToText(nested),
                    _ => null,
                }
            );
        }
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    private static string? ShortName(string? cref)
    {
        if (cref is null)
            return null;
        string name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        int parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        if (parenthesis >= 0)
            name = name[..parenthesis];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Compares methods by definition, so a bound generic or extension method matches its overload.</summary>
    private sealed class Normalized : IEqualityComparer<IMethodSymbol>
    {
        public static readonly Normalized Instance = new();

        public bool Equals(IMethodSymbol? x, IMethodSymbol? y) =>
            SymbolEqualityComparer.Default.Equals(Normalize(x), Normalize(y));

        public int GetHashCode(IMethodSymbol obj) => SymbolEqualityComparer.Default.GetHashCode(Normalize(obj));

        private static IMethodSymbol? Normalize(IMethodSymbol? method) =>
            (method?.ReducedFrom ?? method)?.OriginalDefinition;
    }
}
