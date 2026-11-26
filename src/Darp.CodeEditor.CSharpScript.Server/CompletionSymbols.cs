using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// The symbols that the items of a completion list name, for their order and the types that their rows show, as Rider
/// shows them: a variable's, field's or property's type, and a method's return type. Roslyn's items don't say which
/// symbol they are, so an item's symbols are those of its name among what its word can name: the members of an object
/// initializer's type in the place of a member's name; after a dot, the members that the receiver reaches, a type its
/// static ones and a value its instance and extension ones; and what is in scope otherwise. A generic method's item,
/// <c>Pick&lt;&gt;</c>, names the generic methods of its name and the other item the rest. Of a method's overloads,
/// the first names the type.
/// </summary>
internal sealed class CompletionSymbols
{
    /// <summary>Names without namespaces, special types as keywords, and nullable references with their <c>?</c>.</summary>
    private static readonly SymbolDisplayFormat s_format =
        SymbolDisplayFormat.MinimallyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    private readonly Dictionary<string, List<ISymbol>> _byName = [];

    private CompletionSymbols(IEnumerable<ISymbol> symbols)
    {
        foreach (ISymbol symbol in symbols)
        {
            if (!_byName.TryGetValue(symbol.Name, out List<ISymbol>? named))
                _byName[symbol.Name] = named = [];
            named.Add(symbol);
        }
    }

    /// <summary>What the word that starts at <paramref name="position"/> can name.</summary>
    public static CompletionSymbols At(SemanticModel model, int position, CancellationToken cancellationToken)
    {
        SyntaxToken before = model.SyntaxTree.GetRoot(cancellationToken).FindToken(position).GetPreviousToken();
        // A member's name in an object initializer, after its "{" or a ",".
        if (
            before.Kind() is SyntaxKind.OpenBraceToken or SyntaxKind.CommaToken
            && before.Parent is InitializerExpressionSyntax initializer
            && MembersOf(model, initializer, position, cancellationToken) is { } members
        )
        {
            return new(members);
        }
        if (SignatureCandidates.ReceiverOf(before.Parent) is { } receiver)
        {
            return new(
                SignatureCandidates.ReceiverTypeOf(model, before.Parent, cancellationToken) is { } type
                    ? SignatureCandidates.OfReceiver(
                        model,
                        receiver,
                        model.LookupSymbols(position, type, includeReducedExtensionMethods: true),
                        cancellationToken
                    )
                    : []
            );
        }
        return new(model.LookupSymbols(position));
    }

    /// <summary>
    /// The members that an entry of <paramref name="initializer"/> names: those of an object initializer's type, also of
    /// one that Roslyn parses as a collection initializer until its first <c>=</c>, as <c>new C { Na</c>, for a type
    /// that is no collection; null for a collection's entries, which are values.
    /// </summary>
    private static IEnumerable<ISymbol>? MembersOf(
        SemanticModel model,
        InitializerExpressionSyntax initializer,
        int position,
        CancellationToken cancellationToken
    )
    {
        bool isObject = initializer.IsKind(SyntaxKind.ObjectInitializerExpression);
        if (!isObject && !initializer.IsKind(SyntaxKind.CollectionInitializerExpression))
            return null;
        if (SignatureCandidates.InitializedType(model, initializer, cancellationToken) is not { } type)
            return isObject ? [] : null;
        if (!isObject && IsCollection(type))
            return null;
        return model.LookupSymbols(position, type).Where(symbol => !symbol.IsStatic);
    }

    private static bool IsCollection(ITypeSymbol type) =>
        type.SpecialType == SpecialType.System_Collections_IEnumerable
        || type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerable);

    /// <summary><paramref name="type"/> as a row shows it.</summary>
    public static string Display(ITypeSymbol type) => type.ToDisplayString(s_format);

    /// <summary>The type that the row of <paramref name="item"/>, a variable or a member, shows; null for none.</summary>
    public string? TypeOf(CompletionItem item) =>
        Of(item).Select(TypeOf).FirstOrDefault(type => type is not null) is { } type ? Display(type) : null;

    /// <summary>
    /// Whether <paramref name="item"/> is a script's variable: Roslyn makes those of a script's top level fields of its
    /// class.
    /// </summary>
    public bool IsScriptVariable(CompletionItem item) =>
        Of(item).Any(symbol => symbol is IFieldSymbol { ContainingType.IsScriptClass: true });

    /// <summary>Whether <paramref name="item"/> names only methods that every object has, or overrides of them.</summary>
    public bool IsObjectMethod(CompletionItem item)
    {
        IReadOnlyList<ISymbol> symbols = Of(item);
        return symbols.Count > 0 && symbols.All(symbol => symbol is IMethodSymbol method && IsObjects(method));
    }

    /// <summary>The symbols of <paramref name="item"/>'s name, its generic methods for a generic method's item.</summary>
    private IReadOnlyList<ISymbol> Of(CompletionItem item)
    {
        // An item of a name that is a keyword escapes it, as "@class".
        if (!_byName.TryGetValue(item.DisplayText.TrimStart('@'), out List<ISymbol>? named))
            return [];
        bool generic = item.DisplayTextSuffix == "<>";
        List<ISymbol> matching = named.FindAll(symbol => IsGeneric(symbol) == generic);
        return matching.Count > 0 ? matching : named;
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> is a generic method, also as an extension method whose type arguments come
    /// from its receiver, as Roslyn's items name them.
    /// </summary>
    private static bool IsGeneric(ISymbol symbol) =>
        symbol is IMethodSymbol method && (method.ReducedFrom ?? method).Arity > 0;

    private static bool IsObjects(IMethodSymbol method)
    {
        IMethodSymbol overridden = method;
        while (overridden.OverriddenMethod is { } next)
            overridden = next;
        return overridden.ContainingType?.SpecialType == SpecialType.System_Object;
    }

    private static ITypeSymbol? TypeOf(ISymbol symbol) =>
        symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            IEventSymbol @event => @event.Type,
            IMethodSymbol method => method.ReturnType,
            // A range variable's type is not public.
            _ => null,
        };
}
