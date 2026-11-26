using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// The types that completion's rows show on their right, as Rider does: a variable's, field's or property's type, and a
/// method's return type. Roslyn's items don't say which symbol they are, so an item's type is that of the symbol of its
/// name among those that its word can name: the receiver's members after a dot, and what is in scope otherwise. Of a
/// method's overloads, the first counts.
/// </summary>
internal sealed class CompletionTypes
{
    /// <summary>Names without namespaces, special types as keywords, and nullable references with their <c>?</c>.</summary>
    private static readonly SymbolDisplayFormat s_format =
        SymbolDisplayFormat.MinimallyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    private readonly Dictionary<string, ITypeSymbol> _types = [];

    /// <param name="model">The script's semantic model.</param>
    /// <param name="position">Where the list's word starts.</param>
    /// <param name="receiver">The type whose member the word is; null without a receiver.</param>
    public CompletionTypes(SemanticModel model, int position, ITypeSymbol? receiver)
    {
        ImmutableArray<ISymbol> symbols = receiver is null
            ? model.LookupSymbols(position)
            : model.LookupSymbols(position, receiver, includeReducedExtensionMethods: true);
        foreach (ISymbol symbol in symbols)
        {
            if (TypeOf(symbol) is { } type)
                _types.TryAdd(symbol.Name, type);
        }
    }

    /// <summary>The type that the row of <paramref name="item"/>, a variable or a member, shows; null for none.</summary>
    public string? Of(CompletionItem item) =>
        // An item of a name that is a keyword escapes it, as "@class".
        _types.TryGetValue(item.DisplayText.TrimStart('@'), out ITypeSymbol? type)
            ? type.ToDisplayString(s_format)
            : null;

    private static ITypeSymbol? TypeOf(ISymbol symbol) =>
        symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            IEventSymbol @event => @event.Type,
            IMethodSymbol method => method.ReturnType,
            _ => null,
        };
}
