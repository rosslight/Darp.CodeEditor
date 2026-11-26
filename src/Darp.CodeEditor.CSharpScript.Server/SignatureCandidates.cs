using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// What an argument list's arguments can go to, filtered as Roslyn filters them for signature help: by the receiver,
/// by access through the receiver's type, and without what editors hide or a more specific overload hides.
/// </summary>
internal static class SignatureCandidates
{
    /// <summary>
    /// What the arguments in <paramref name="list"/> can go to, as the user may call it: the methods of a method group,
    /// a delegate's invoke method, a type's constructors, the other constructors of a constructor initializer, an
    /// attribute's constructors, the indexers of an element access, or the <c>Add</c> methods of a collection
    /// initializer's entry. They are accessible through the receiver's type, and not hidden from editors.
    /// </summary>
    public static List<ISymbol> Of(SemanticModel model, SyntaxNode list, CancellationToken cancellationToken)
    {
        int position = list.SpanStart;
        (IEnumerable<ISymbol> Found, ITypeSymbol? Through) candidates = (list, list.Parent) switch
        {
            (ArgumentListSyntax, InvocationExpressionSyntax invocation) => Invoked(
                model,
                invocation,
                cancellationToken
            ),
            (ArgumentListSyntax, BaseObjectCreationExpressionSyntax creation) => (
                Constructors(model.GetTypeInfo(creation, cancellationToken).Type),
                null
            ),
            (ArgumentListSyntax, ConstructorInitializerSyntax { Parent: { } constructor } initializer) => (
                OtherConstructors(
                    model.GetDeclaredSymbol(constructor, cancellationToken) as IMethodSymbol,
                    initializer.IsKind(SyntaxKind.BaseConstructorInitializer)
                ),
                null
            ),
            (AttributeArgumentListSyntax, AttributeSyntax attribute) => (
                Constructors(model.GetTypeInfo(attribute, cancellationToken).Type),
                null
            ),
            (BracketedArgumentListSyntax, ElementAccessExpressionSyntax access) => Indexers(
                model,
                model.GetTypeInfo(access.Expression, cancellationToken).Type,
                position
            ),
            (BracketedArgumentListSyntax, ElementBindingExpressionSyntax binding) => Indexers(
                model,
                ReceiverTypeOf(model, binding, cancellationToken),
                position
            ),
            (
                BracketedArgumentListSyntax,
                ImplicitElementAccessSyntax { Parent.Parent: InitializerExpressionSyntax initializer }
            ) => Indexers(model, InitializedType(model, initializer, cancellationToken), position),
            (InitializerExpressionSyntax, InitializerExpressionSyntax initializer)
                when InitializedType(model, initializer, cancellationToken) is { } collection => (
                model
                    .LookupSymbols(position, collection, "Add", includeReducedExtensionMethods: true)
                    .OfType<IMethodSymbol>()
                    .Where(method => !method.IsStatic),
                collection
            ),
            _ => ([], null),
        };
        ISymbol within = Within(model, position, cancellationToken);
        List<ISymbol> reachable =
        [
            .. candidates.Found.Where(symbol =>
                model.Compilation.IsSymbolAccessibleWithin(symbol, within, candidates.Through) && IsBrowsable(symbol)
            ),
        ];
        return [.. reachable.Where(symbol => !reachable.Any(other => IsHiddenBy(symbol, other)))];
    }

    /// <summary>
    /// The methods that an invocation's receiver reaches (<see cref="OfReceiver"/>); without a method group, a
    /// delegate's invoke method. The receiver's type is what access is checked through.
    /// </summary>
    private static (IEnumerable<ISymbol>, ITypeSymbol?) Invoked(
        SemanticModel model,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken
    )
    {
        if (model.GetMemberGroup(invocation.Expression, cancellationToken) is not { IsEmpty: false } group)
        {
            return
                model.GetTypeInfo(invocation.Expression, cancellationToken).Type
                    is INamedTypeSymbol { DelegateInvokeMethod: { } invoke }
                ? ([invoke], null)
                : ([], null);
        }
        IEnumerable<IMethodSymbol> methods = group.OfType<IMethodSymbol>();
        if (ReceiverOf(invocation.Expression) is not { } receiver)
            return (methods, null);
        return (
            OfReceiver(model, receiver, methods, cancellationToken),
            // Through "base", access is the base type's own: its protected members are reachable.
            receiver is BaseExpressionSyntax
                ? null
                : model.GetTypeInfo(receiver, cancellationToken).Type
        );
    }

    /// <summary>
    /// The methods of <paramref name="methods"/> that <paramref name="receiver"/> reaches, as Roslyn filters them: a
    /// type's static methods, a value's instance and extension methods, and both for a name that is a value and its
    /// type alike, as <c>Color Color</c>.
    /// </summary>
    public static IEnumerable<IMethodSymbol> OfReceiver(
        SemanticModel model,
        ExpressionSyntax receiver,
        IEnumerable<IMethodSymbol> methods,
        CancellationToken cancellationToken
    )
    {
        SymbolInfo info = model.GetSymbolInfo(receiver, cancellationToken);
        bool isType = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is ITypeSymbol;
        ITypeSymbol? type = model.GetTypeInfo(receiver, cancellationToken).Type;
        bool isBoth =
            receiver is IdentifierNameSyntax { Identifier.ValueText: var name }
            && (
                isType
                    ? model.LookupSymbols(receiver.SpanStart, name: name).Any(s => s is not INamespaceOrTypeSymbol)
                    : model
                        .LookupNamespacesAndTypes(receiver.SpanStart, name: name)
                        .Any(t => SymbolEqualityComparer.Default.Equals(t, type))
            );
        return methods.Where(method => isBoth || method.IsStatic == isType);
    }

    /// <summary>
    /// The receiver of a member access, <c>a</c> in <c>a.b</c>, or of a binding, <c>a</c> in <c>a?.b</c> or
    /// <c>a?[b]</c>; null for anything else.
    /// </summary>
    public static ExpressionSyntax? ReceiverOf(SyntaxNode? access) =>
        access switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
            MemberBindingExpressionSyntax or ElementBindingExpressionSyntax => access
                .FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()
                ?.Expression,
            _ => null,
        };

    /// <summary>
    /// The type whose members <paramref name="access"/> reaches: its receiver's, and after <c>?</c> a nullable value's
    /// value's; null without a receiver.
    /// </summary>
    public static ITypeSymbol? ReceiverTypeOf(
        SemanticModel model,
        SyntaxNode? access,
        CancellationToken cancellationToken
    )
    {
        if (ReceiverOf(access) is not { } receiver)
            return null;
        ITypeSymbol? type = model.GetTypeInfo(receiver, cancellationToken).Type;
        return
            access is MemberBindingExpressionSyntax or ElementBindingExpressionSyntax
            && type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;
    }

    /// <summary>
    /// The type that an object or collection initializer initializes: a new object's, or a member's that is
    /// initialized in place, as <c>Names = { "a" }</c>.
    /// </summary>
    private static ITypeSymbol? InitializedType(
        SemanticModel model,
        InitializerExpressionSyntax initializer,
        CancellationToken cancellationToken
    ) =>
        initializer.Parent switch
        {
            BaseObjectCreationExpressionSyntax creation => model.GetTypeInfo(creation, cancellationToken).Type,
            AssignmentExpressionSyntax member => model.GetTypeInfo(member.Left, cancellationToken).Type,
            _ => null,
        };

    private static IEnumerable<ISymbol> Constructors(ITypeSymbol? type) =>
        type is INamedTypeSymbol named ? named.InstanceConstructors : [];

    /// <summary>The constructors that a constructor initializer of <paramref name="constructor"/> can call.</summary>
    private static IEnumerable<ISymbol> OtherConstructors(IMethodSymbol? constructor, bool ofBase) =>
        Constructors(ofBase ? constructor?.ContainingType.BaseType : constructor?.ContainingType)
            .Where(c => !SymbolEqualityComparer.Default.Equals(c, constructor));

    private static (IEnumerable<ISymbol>, ITypeSymbol?) Indexers(SemanticModel model, ITypeSymbol? type, int position)
    {
        if (type is null)
            return ([], null);
        return (model.LookupSymbols(position, type, WellKnownMemberNames.Indexer), type);
    }

    /// <summary>The fields and properties that an attribute's named arguments can set, by name.</summary>
    public static List<ISymbol> Settable(INamedTypeSymbol attribute, SemanticModel model, int position)
    {
        var settable = new List<ISymbol>();
        for (INamedTypeSymbol? type = attribute; type is not null; type = type.BaseType)
        {
            settable.AddRange(
                type.GetMembers()
                    .Where(member =>
                        member switch
                        {
                            IFieldSymbol field => !field.IsStatic && !field.IsReadOnly && !field.IsConst,
                            IPropertySymbol property => !property.IsStatic
                                && !property.IsIndexer
                                && property.SetMethod is { } setter
                                && model.IsAccessible(position, setter),
                            _ => false,
                        }
                        && model.IsAccessible(position, member)
                        && IsBrowsable(member)
                    )
            );
        }
        return [.. settable.DistinctBy(member => member.Name).OrderBy(member => member.Name, StringComparer.Ordinal)];
    }

    /// <summary>The type whose code is at <paramref name="position"/>, which access is checked within.</summary>
    private static ISymbol Within(SemanticModel model, int position, CancellationToken cancellationToken)
    {
        for (ISymbol? symbol = model.GetEnclosingSymbol(position, cancellationToken); symbol is not null; )
        {
            if (symbol is INamedTypeSymbol)
                return symbol;
            symbol = symbol.ContainingSymbol;
        }
        return model.Compilation.Assembly;
    }

    /// <summary>
    /// Whether editors show <paramref name="symbol"/>: one from a referenced assembly that marks it
    /// <c>[EditorBrowsable(EditorBrowsableState.Never)]</c> stays hidden, as in completion.
    /// </summary>
    public static bool IsBrowsable(ISymbol symbol) =>
        !symbol.Locations.Any(location => location.IsInMetadata)
        || !symbol
            .GetAttributes()
            .Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.ComponentModel.EditorBrowsableAttribute"
                && attribute.ConstructorArguments is [{ Value: (int)EditorBrowsableState.Never }]
            );

    /// <summary>
    /// Whether <paramref name="other"/> hides <paramref name="symbol"/>, as Roslyn has it: with the same parameters, it
    /// was written with types that <paramref name="symbol"/> has only from type arguments, as <c>Put(int value)</c>
    /// beside <c>Put(T value)</c> of a <c>Box&lt;int&gt;</c>.
    /// </summary>
    private static bool IsHiddenBy(ISymbol symbol, ISymbol other) =>
        symbol is IMethodSymbol method
        && other is IMethodSymbol hiding
        && !SymbolEqualityComparer.Default.Equals(method, hiding)
        && method.Parameters.Length == hiding.Parameters.Length
        && method
            .Parameters.Zip(hiding.Parameters)
            .All(pair =>
                pair.First.Name == pair.Second.Name
                && pair.First.RefKind == pair.Second.RefKind
                && SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type)
            )
        && FromTypeArguments(method) > FromTypeArguments(hiding);

    /// <summary>How many of the method's parameters have their type from type arguments.</summary>
    private static int FromTypeArguments(IMethodSymbol method) =>
        method
            .Parameters.Zip(method.OriginalDefinition.Parameters)
            .Count(pair => !SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type));
}
