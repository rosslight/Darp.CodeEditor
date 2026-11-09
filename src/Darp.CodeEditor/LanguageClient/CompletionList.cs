using System.Text.Json;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.LanguageClient;

/// <summary>
/// A server's answer to <c>textDocument/completion</c>: its items, in the order they show before anything is typed,
/// with their edits in offsets of the text it was asked for.
/// </summary>
internal sealed class CompletionList
{
    private readonly Func<CompletionItem, CancellationToken, Task<CompletionItem?>>? _resolve;

    /// <param name="items">The items, sorted by their <see cref="CompletionItem.SortKey"/>.</param>
    /// <param name="isIncomplete">Whether typing more asks the server again.</param>
    /// <param name="resolve">Asks the server that answered to resolve an item; null when it does not resolve.</param>
    public CompletionList(
        IReadOnlyList<CompletionItem> items,
        bool isIncomplete,
        Func<CompletionItem, CancellationToken, Task<CompletionItem?>>? resolve
    )
    {
        Items = items;
        IsIncomplete = isIncomplete;
        _resolve = resolve;
    }

    /// <summary>The items, sorted by their <see cref="CompletionItem.SortKey"/>.</summary>
    public IReadOnlyList<CompletionItem> Items { get; }

    /// <summary>Whether the list may lack items for more typed text: typing more asks the server again.</summary>
    public bool IsIncomplete { get; }

    /// <summary>Whether the server resolves items: adds their details, documentation and additional edits.</summary>
    public bool Resolves => _resolve is not null;

    /// <summary>
    /// The item as the server that answered resolves it; null when it does not resolve, failed or ended, and when
    /// <paramref name="cancellationToken"/> cancelled the request. Never waits on the calling thread.
    /// </summary>
    public Task<CompletionItem?> ResolveAsync(CompletionItem item, CancellationToken cancellationToken) =>
        _resolve?.Invoke(item, cancellationToken) ?? Task.FromResult<CompletionItem?>(null);

    /// <summary>
    /// Reads a completion result: a <c>CompletionList</c> or an array of items; null for a null result. Items whose
    /// edit is not on the line asked for, or does not contain the position asked for, are left out, as VS Code leaves
    /// them out.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="snapshot">The text that completion was asked for.</param>
    /// <param name="word">The word before the position asked for, which ends there: what items without an edit replace.</param>
    /// <param name="commitCharacters">The characters that commit items that name none of their own.</param>
    /// <param name="resolve">Asks the server to resolve an item; null when it does not resolve.</param>
    /// <exception cref="JsonException">The result is not a completion list.</exception>
    public static CompletionList? Read(
        JsonElement result,
        TextSnapshot snapshot,
        TextRange word,
        string commitCharacters,
        Func<CompletionItem, CancellationToken, Task<CompletionItem?>>? resolve
    )
    {
        JsonElement items;
        bool isIncomplete = false;
        switch (result.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
                items = result;
                break;
            case JsonValueKind.Object
                when result.TryGetProperty("items", out items) && items.ValueKind == JsonValueKind.Array:
                isIncomplete = ServerJson.IsTrue(result, "isIncomplete");
                break;
            default:
                throw new JsonException("A completion result must be a list or an array of items.");
        }
        var read = new List<CompletionItem>(items.GetArrayLength());
        int line = snapshot.GetPosition(word.End).Line - 1;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (ReadItem(item, snapshot, line, word, commitCharacters) is { } completion)
                read.Add(completion);
        }
        return new CompletionList([.. read.OrderBy(item => item.SortKey)], isIncomplete, resolve);
    }

    /// <summary>
    /// <paramref name="item"/> with what <c>completionItem/resolve</c> answered: its detail, documentation and
    /// additional edits, where the answer has them.
    /// </summary>
    /// <param name="item">The item that was resolved.</param>
    /// <param name="result">The answer.</param>
    /// <param name="snapshot">The text that the item's list was asked for.</param>
    /// <exception cref="JsonException">An additional edit is not a text edit.</exception>
    public static CompletionItem Resolved(CompletionItem item, JsonElement result, TextSnapshot snapshot) =>
        result.ValueKind != JsonValueKind.Object
            ? item
            : item with
            {
                Detail = ServerJson.String(result, "detail") ?? item.Detail,
                Documentation = ServerJson.Documentation(result) ?? item.Documentation,
                AdditionalEdits = AdditionalEdits(result, snapshot) ?? item.AdditionalEdits,
            };

    private static CompletionItem? ReadItem(
        JsonElement item,
        TextSnapshot snapshot,
        int line,
        TextRange word,
        string commitCharacters
    )
    {
        if (item.ValueKind != JsonValueKind.Object || ServerJson.String(item, "label") is not { } label)
            throw new JsonException("A completion item needs a label.");
        TextRange range;
        string newText;
        if (item.TryGetProperty("textEdit", out JsonElement edit) && edit.ValueKind != JsonValueKind.Null)
        {
            if (Edit(edit, snapshot, line, word.End) is not { } valid)
                return null;
            (range, newText) = valid;
        }
        else
        {
            (range, newText) = (word, ServerJson.String(item, "insertText") ?? label);
        }
        // As VS Code, an item without a kind, or with one the protocol does not name, is a property.
        int kind = ServerJson.Number(item, "kind") is int k and >= 1 and <= 25 ? k : CompletionItemKinds.Property;
        return new CompletionItem(
            label,
            kind,
            ServerJson.String(item, "detail"),
            ServerJson.Documentation(item),
            IsDeprecated: ServerJson.IsTrue(item, "deprecated") || HasDeprecatedTag(item),
            IsPreselected: ServerJson.IsTrue(item, "preselect"),
            ServerJson.String(item, "filterText") ?? label,
            new CompletionSortKey(ServerJson.String(item, "sortText") ?? label, label, kind),
            new CompletionEdit(range, newText),
            AdditionalEdits(item, snapshot),
            item.TryGetProperty("commitCharacters", out JsonElement _)
                ? ServerFeatures.Characters(item, "commitCharacters")
                : commitCharacters,
            item
        );
    }

    /// <summary>
    /// An item's <c>textEdit</c>: a <c>TextEdit</c>, or an <c>InsertReplaceEdit</c>, whose insert range applies, as in
    /// VS Code by default. Null for one that is not on the line asked for, or does not contain the position asked for.
    /// </summary>
    private static (TextRange Range, string NewText)? Edit(
        JsonElement edit,
        TextSnapshot snapshot,
        int line,
        int position
    )
    {
        if (edit.ValueKind != JsonValueKind.Object || ServerJson.String(edit, "newText") is not { } newText)
            throw new JsonException("A completion item's text edit needs new text.");
        TextRange? range;
        if (
            edit.TryGetProperty("insert", out JsonElement insert)
            && edit.TryGetProperty("replace", out JsonElement replace)
        )
        {
            range = OnLine(insert, snapshot, line, position);
            if (OnLine(replace, snapshot, line, position)?.Start != range?.Start)
                return null;
        }
        else
        {
            range = edit.TryGetProperty("range", out JsonElement value)
                ? OnLine(value, snapshot, line, position)
                : throw new JsonException("A completion item's text edit needs a range.");
        }
        return range is { } valid ? (valid, newText) : null;
    }

    /// <summary>The offsets of a range on <paramref name="line"/> around <paramref name="position"/>; null otherwise.</summary>
    private static TextRange? OnLine(JsonElement value, TextSnapshot snapshot, int line, int position)
    {
        LspRange range = ServerJson.Range(value);
        if (range.Start.Line != line || range.End.Line != line)
            return null;
        int start = Positions.ToOffset(snapshot, range.Start);
        int end = Positions.ToOffset(snapshot, range.End);
        return start <= position && position <= end ? TextRange.FromBounds(start, end) : null;
    }

    /// <summary>The <c>additionalTextEdits</c>, in offsets of the text asked for; null without any.</summary>
    private static IReadOnlyList<CompletionEdit>? AdditionalEdits(JsonElement item, TextSnapshot snapshot)
    {
        if (
            !item.TryGetProperty("additionalTextEdits", out JsonElement edits)
            || edits.ValueKind != JsonValueKind.Array
        )
            return null;
        var read = new List<CompletionEdit>();
        foreach (JsonElement edit in edits.EnumerateArray())
        {
            if (
                edit.ValueKind != JsonValueKind.Object
                || ServerJson.String(edit, "newText") is not { } newText
                || !edit.TryGetProperty("range", out JsonElement value)
            )
            {
                throw new JsonException("An additional edit must be a text edit.");
            }
            LspRange range = ServerJson.Range(value);
            int start = Positions.ToOffset(snapshot, range.Start);
            read.Add(
                new CompletionEdit(
                    TextRange.FromBounds(start, Math.Max(start, Positions.ToOffset(snapshot, range.End))),
                    newText
                )
            );
        }
        return read;
    }

    private static bool HasDeprecatedTag(JsonElement item) =>
        item.TryGetProperty("tags", out JsonElement tags)
        && tags.ValueKind == JsonValueKind.Array
        && tags.EnumerateArray()
            .Any(tag => tag.ValueKind == JsonValueKind.Number && tag.TryGetInt32(out int t) && t == 1);
}

/// <summary>A server's completion item, with its edits in offsets of the text its list was asked for.</summary>
/// <param name="Label">What shows in the list.</param>
/// <param name="Kind">Its kind, as the protocol numbers them: from 1 for text to 25 for a type parameter.</param>
/// <param name="Detail">A line about it, such as its type or signature; null for none.</param>
/// <param name="Documentation">Its documentation; null for none.</param>
/// <param name="IsDeprecated">Whether it is deprecated: it shows struck through.</param>
/// <param name="IsPreselected">Whether the server wants it selected, among the items that match best.</param>
/// <param name="FilterText">The text it is matched by.</param>
/// <param name="SortKey">Where it is in the list before anything is typed.</param>
/// <param name="Edit">What committing it does to the text.</param>
/// <param name="AdditionalEdits">What else committing it changes, such as an import; null when it names none.</param>
/// <param name="CommitCharacters">The characters that commit it as they are typed.</param>
/// <param name="Source">The item as the server sent it, for <c>completionItem/resolve</c>.</param>
internal sealed record CompletionItem(
    string Label,
    int Kind,
    string? Detail,
    HoverText? Documentation,
    bool IsDeprecated,
    bool IsPreselected,
    string FilterText,
    CompletionSortKey SortKey,
    CompletionEdit Edit,
    IReadOnlyList<CompletionEdit>? AdditionalEdits,
    string CommitCharacters,
    JsonElement Source
);

/// <summary>A change of the text: <paramref name="Range"/> becomes <paramref name="NewText"/>.</summary>
internal readonly record struct CompletionEdit(TextRange Range, string NewText);

/// <summary>How completion was asked for.</summary>
/// <param name="Kind">One of <see cref="CompletionTriggerKind"/>.</param>
/// <param name="Character">The character typed, for a trigger character.</param>
internal readonly record struct CompletionTrigger(int Kind, char? Character = null);

/// <summary>The kinds of completion items that the client treats apart.</summary>
internal static class CompletionItemKinds
{
    public const int Property = 10;
}
