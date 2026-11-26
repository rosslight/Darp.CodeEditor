using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// Roslyn's items of types and extension methods from namespaces that a script does not import. A list has thousands of
/// them, which it already computes, so the server sends only the best that match the typed name, as the client ranks
/// them, also with a typo where the client tolerates one, and only those that the script may name: from .NET and the
/// app's marked assemblies, not from what those depend on.
/// </summary>
internal static class ScriptImports
{
    /// <summary>The most that a list sends; with more that match, it asks again as the user types on.</summary>
    public const int MaxItems = 100;

    /// <summary>Kept for reuse, as its tables are large; ranking runs on one thread from start to end.</summary>
    [ThreadStatic]
    private static CompletionMatcher? t_matcher;

    /// <summary>
    /// Whether <paramref name="item"/>, an edit beyond its word, may be one: Roslyn describes such an item by its
    /// namespace. Other complex items, such as snippets, describe what they write.
    /// </summary>
    public static bool MayBe(CompletionItem item) => item.IsComplexTextEdit && item.InlineDescription.Length > 0;

    /// <summary>
    /// The indices of the best of <paramref name="candidates"/> for <paramref name="typed"/>, at most
    /// <see cref="MaxItems"/>, best first, of those that the script may name.
    /// </summary>
    /// <param name="items">The list's items.</param>
    /// <param name="candidates">The indices of the items that <see cref="MayBe"/> are imports.</param>
    /// <param name="typed">The text from the items' word's start up to the caret; not empty.</param>
    /// <param name="filterText">The text that the client matches an item by.</param>
    /// <param name="others">
    /// How many other items the list sends. The client tolerates a typo in a list of up to
    /// <see cref="CompletionMatcher.MaxGracefulItems"/> items, so the ranking does too while these and the most imports
    /// stay within it.
    /// </param>
    /// <param name="compilation">The script's compilation.</param>
    /// <param name="receiver">The type of the receiver whose member the word is; null for none.</param>
    /// <param name="cancellationToken">Stops the ranking.</param>
    /// <returns>The indices, and whether more match.</returns>
    public static (IReadOnlyList<int> Best, bool More) Best(
        IReadOnlyList<CompletionItem> items,
        IReadOnlyList<int> candidates,
        string typed,
        Func<CompletionItem, string> filterText,
        int others,
        Compilation compilation,
        ITypeSymbol? receiver,
        CancellationToken cancellationToken
    )
    {
        var texts = new CompletionCandidate[candidates.Count];
        for (int i = 0; i < texts.Length; i++)
        {
            CompletionItem item = items[candidates[i]];
            string label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
            string filter = filterText(item);
            texts[i] = new CompletionCandidate(
                typed,
                label,
                filter,
                string.Equals(filter, label, StringComparison.OrdinalIgnoreCase)
            );
        }
        var namespaces = new Dictionary<string, INamespaceSymbol?>();
        var best = new List<int>(MaxItems);
        bool graceful = others + MaxItems <= CompletionMatcher.MaxGracefulItems;
        t_matcher ??= new CompletionMatcher();
        foreach (RankedCompletion match in t_matcher.Rank(texts, graceful, cancellationToken))
        {
            int index = candidates[match.Index];
            if (!IsNameable(items[index], compilation, receiver, namespaces))
                continue;
            if (best.Count == MaxItems)
                return (best, true);
            best.Add(index);
        }
        return (best, false);
    }

    /// <summary>
    /// Whether <paramref name="item"/> is a type or an extension method that the script may name, in the namespace that
    /// its description names. The compilation's global namespace merges only the assemblies that scripts may name: the
    /// ones under the private alias are not in it. Roslyn's item does not say which method it is, so an extension
    /// method's counts when one of its name there that the script may call applies to <paramref name="receiver"/>.
    /// </summary>
    private static bool IsNameable(
        CompletionItem item,
        Compilation compilation,
        ITypeSymbol? receiver,
        Dictionary<string, INamespaceSymbol?> namespaces
    )
    {
        if (!namespaces.TryGetValue(item.InlineDescription, out INamespaceSymbol? found))
            namespaces[item.InlineDescription] = found = Namespace(compilation.GlobalNamespace, item.InlineDescription);
        if (found is null)
            return false;
        if (item.Tags.Contains(WellKnownTags.ExtensionMethod))
            return receiver is not null
                && found
                    .GetTypeMembers()
                    .Where(type => type.MightContainExtensionMethods)
                    .SelectMany(type => type.GetMembers(item.DisplayText))
                    .Any(member =>
                        member is IMethodSymbol { IsExtensionMethod: true } method
                        && compilation.IsSymbolAccessibleWithin(method, compilation.Assembly)
                        && method.ReduceExtensionMethod(receiver) is not null
                    );
        // An attribute's item leaves out the "Attribute" of its name.
        return !found.GetTypeMembers(item.DisplayText).IsEmpty
            || !found.GetTypeMembers(item.DisplayText + "Attribute").IsEmpty;
    }

    /// <summary>The namespace of <paramref name="name"/> in <paramref name="global"/>; null when it has none.</summary>
    private static INamespaceSymbol? Namespace(INamespaceSymbol global, string name)
    {
        INamespaceSymbol? found = global;
        foreach (string part in name.Split('.'))
            found = found?.GetMembers(part).OfType<INamespaceSymbol>().FirstOrDefault();
        return found;
    }
}
