using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Darp.CodeEditor.Lsp;
using Darp.CodeEditor.Lsp.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using CompletionItem = Darp.CodeEditor.Lsp.Protocol.CompletionItem;
using CompletionItemKind = Darp.CodeEditor.Lsp.Protocol.CompletionItemKind;
using CompletionTriggerKind = Darp.CodeEditor.Lsp.Protocol.CompletionTriggerKind;
using LspCompletionOptions = Darp.CodeEditor.Lsp.Protocol.CompletionOptions;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/completion</c> and <c>completionItem/resolve</c> from Roslyn's completion service.</summary>
/// <remarks>
/// <para>
/// Each item's edit comes from <see cref="CompletionService.GetChangeAsync"/> where that is cheap: the typed word
/// replaced by the item. Items whose change goes further, such as types from namespaces that are not imported, insert
/// their name; resolving them adds the rest, such as the <c>using</c> directive, as additional edits. Snippets, such
/// as <c>for</c>, expand on resolve, for clients that resolve <c>textEdit</c> lazily, and are left out for others:
/// expanding them costs milliseconds each, and word lists have thousands of items.
/// </para>
/// <para>
/// Lists are written straight to JSON, since word lists have up to a thousand items and writing them is a large part of
/// answering. Items are sorted as Roslyn sorts them; <c>sortText</c> is the item's rank.
/// </para>
/// <para>
/// Like every request, a completion request goes on when the text changes: the client can still use the list while
/// the user keeps typing the same word. Closing the document answers it with null.
/// </para>
/// </remarks>
internal static class CompletionHandler
{
    private static readonly List<string> s_defaultCommitCharacters = ToStrings(
        CompletionRules.Default.DefaultCommitCharacters
    );

    /// <summary><c>textDocument/completion</c>, answered with the list as <see cref="Write"/> wrote it.</summary>
    private static readonly RequestType<CompletionParams, CompletionListJson> s_completion = new(
        LspMethods.Completion.Method,
        LspMethods.Completion.Params,
        ServerJsonContext.Default.CompletionListJson
    );

    /// <summary>Relaxed escaping, as the connection writes: the output is no HTML page.</summary>
    private static readonly JsonWriterOptions s_writerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes Roslyn's completion lists with computing the changes of their items. Roslyn shares some item objects,
    /// such as types from namespaces that are not imported, between lists of all documents and sets their span for
    /// each new list; a change computed with another list's span is wrong or fails.
    /// </summary>
    private static readonly SemaphoreSlim s_sharedItems = new(1, 1);

    public static LspCompletionOptions Options { get; } =
        new(TriggerCharacters: ["."], AllCommitCharacters: s_defaultCommitCharacters, ResolveProvider: true);

    public static void Register(JsonRpcConnection connection, ScriptSession session)
    {
        connection.OnRequest(s_completion, (request, token) => HandleAsync(session, request, token));
        connection.OnRequest(LspMethods.CompletionResolve, (item, token) => ResolveRequestAsync(session, item, token));
    }

    /// <summary>Computes the completion list at <paramref name="position"/> as the JSON of an LSP <c>CompletionList</c>.</summary>
    /// <returns>Null when nothing applies.</returns>
    public static async Task<CompletionListJson?> GetCompletionsAsync(
        ScriptWorkspace workspace,
        DocumentSnapshot snapshot,
        int position,
        RoslynCompletionTrigger trigger,
        ClientFeatures client,
        CompletionListLimits limits,
        CompletionResults results,
        CancellationToken cancellationToken
    )
    {
        Document document = snapshot.Owner.GetDocument(workspace, snapshot);
        CompletionService service = GetService(document);
        if (trigger.Kind == Microsoft.CodeAnalysis.Completion.CompletionTriggerKind.Insertion)
        {
            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!service.ShouldTriggerCompletion(text, position, trigger))
                return null;
        }
        await s_sharedItems.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await CreateListAsync(
                    service,
                    document,
                    snapshot,
                    position,
                    trigger,
                    client,
                    limits,
                    results,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            s_sharedItems.Release();
        }
    }

    private static async Task<CompletionListJson?> CreateListAsync(
        CompletionService service,
        Document document,
        DocumentSnapshot snapshot,
        int position,
        RoslynCompletionTrigger trigger,
        ClientFeatures client,
        CompletionListLimits limits,
        CompletionResults results,
        CancellationToken cancellationToken
    )
    {
        LineMap lines = snapshot.Lines;
        RoslynCompletionList roslynList = await service
            .GetCompletionsAsync(document, position, trigger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        RoslynCompletionItem[] roslynItems =
        [
            .. roslynList
                .ItemsList.Where(item => GetEditKind(item, client) is not null)
                .OrderBy(item => item.SortText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.DisplayText, StringComparer.Ordinal),
        ];
        if (roslynItems.Length == 0)
            return null;
        // Filter by what was typed before the caret; text after it (as in "Con|ZZZ") is still replaced on commit.
        var typed = TextSpan.FromBounds(
            roslynList.Span.Start,
            Math.Clamp(position, roslynList.Span.Start, roslynList.Span.End)
        );
        (int[] ranks, bool incomplete) = Select(roslynItems, snapshot.Text.ToString(typed), limits);

        long resultId = results.NextId();
        // In suggestion mode, such as when naming a new variable, typing must not commit an item.
        bool suggestionMode = roslynList.SuggestionModeItem is not null;
        List<string> defaultCommitCharacters = suggestionMode
            ? []
            : ToStrings(roslynList.Rules.DefaultCommitCharacters);
        var commitCharacters = new Dictionary<ImmutableArray<CharacterSetModificationRule>, List<string>>();
        string rankFormat = "D" + roslynItems.Length.ToString(CultureInfo.InvariantCulture).Length;
        var items = new List<Item>(ranks.Length);
        foreach (int rank in ranks)
        {
            RoslynCompletionItem roslynItem = roslynItems[rank];
            string label = roslynItem.DisplayTextPrefix + roslynItem.DisplayText + roslynItem.DisplayTextSuffix;
            (TextSpan span, string newText) =
                GetEditKind(roslynItem, client) == EditKind.Change
                    ? await GetChangeAsync(service, document, roslynItem, cancellationToken).ConfigureAwait(false)
                    // The rest of the change, or the expanded snippet, comes on resolve.
                    : (roslynItem.Span, roslynItem.DisplayText);
            List<string> itemCommitCharacters =
                roslynItem.Rules.CommitCharacterRules.IsEmpty || suggestionMode
                    ? defaultCommitCharacters
                    : GetCommitCharacters(roslynList.Rules, roslynItem.Rules, commitCharacters);
            items.Add(
                new Item(
                    label,
                    client.CompletionLabelDetails && roslynItem.InlineDescription.Length > 0
                        ? roslynItem.InlineDescription
                        : null,
                    GetKind(roslynItem.Tags),
                    rank.ToString(rankFormat, CultureInfo.InvariantCulture),
                    roslynItem.FilterText == label ? null : roslynItem.FilterText,
                    roslynItem.Rules.MatchPriority == MatchPriority.Preselect,
                    span,
                    newText,
                    client.CompletionDefaultCommitCharacters
                        ? itemCommitCharacters == defaultCommitCharacters
                            ? null
                            : itemCommitCharacters
                        : client.CompletionCommitCharacters
                        && !itemCommitCharacters.SequenceEqual(s_defaultCommitCharacters)
                            ? itemCommitCharacters
                            : null,
                    string.Create(CultureInfo.InvariantCulture, $"{resultId}:{items.Count}")
                )
            );
        }
        results.Set(
            snapshot.Owner,
            new CompletionResult(
                resultId,
                document,
                lines,
                position,
                trigger,
                roslynList.Span,
                [.. ranks.Select(rank => roslynItems[rank])]
            )
        );
        return Write(items, incomplete, roslynList.Span, defaultCommitCharacters, lines, client);
    }

    public static async Task<CompletionItem> ResolveAsync(
        CompletionItem item,
        CompletionResult result,
        RoslynCompletionItem roslynItem,
        ClientFeatures client,
        CancellationToken cancellationToken
    )
    {
        CompletionService service = GetService(result.Document);
        CompletionDescription? description = await service
            .GetDescriptionAsync(result.Document, roslynItem, cancellationToken)
            .ConfigureAwait(false);
        if (description is { TaggedParts.Length: > 0 })
            item = item with
            {
                Documentation = Markup.FromDescription(description.TaggedParts, client.CompletionMarkdown),
            };

        EditKind? edit = GetEditKind(roslynItem, client);
        if (edit is EditKind.Change)
            return item;
        CompletionChange change = await GetResolvedChangeAsync(service, result, roslynItem, cancellationToken)
            .ConfigureAwait(false);
        if (edit is EditKind.ResolvedSnippet)
        {
            // Snippets such as "for" expand to several lines; with snippet syntax the caret lands where Roslyn puts it.
            TextChange textChange = change.TextChange;
            string newText = textChange.NewText ?? "";
            InsertTextFormat? format = null;
            if (client.CompletionSnippets)
            {
                int caret = Math.Clamp(
                    (change.NewPosition ?? textChange.Span.End) - textChange.Span.Start,
                    0,
                    newText.Length
                );
                newText = EscapeSnippet(newText[..caret]) + "$0" + EscapeSnippet(newText[caret..]);
                format = InsertTextFormat.Snippet;
            }
            return item with
            {
                InsertTextFormat = format,
                TextEditText = null,
                TextEdit = new CompletionTextEdit(newText, result.Lines.GetRange(textChange.Span)),
            };
        }
        // The item inserted its display text; everything else in the change, such as a using directive, is extra.
        List<TextEdit> additional =
        [
            .. change
                .TextChanges.Where(c => c.NewText != roslynItem.DisplayText)
                .Select(c => new TextEdit(result.Lines.GetRange(c.Span), c.NewText ?? "")),
        ];
        if (additional.Count == change.TextChanges.Length)
        {
            Console.Error.WriteLine(
                $"Completion item {roslynItem.DisplayText}: its change does not insert its display text."
            );
            return item;
        }
        return item with { AdditionalTextEdits = additional };
    }

    private static async Task<CompletionListJson?> HandleAsync(
        ScriptSession session,
        CompletionParams request,
        CancellationToken token
    )
    {
        if (!session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return null;
        int position = snapshot.Lines.GetOffset(request.Position);
        RoslynCompletionTrigger trigger = request.Context
            is { TriggerKind: CompletionTriggerKind.TriggerCharacter, TriggerCharacter: [char character] }
            ? RoslynCompletionTrigger.CreateInsertionTrigger(character)
            : RoslynCompletionTrigger.Invoke;
        ClientFeatures client = session.Client;
        // The snapshot is taken; the rest runs on the thread pool while later messages are dispatched.
        await Task.Yield();
        return await DocumentWork
            .RunAsync(
                token,
                snapshot.Owner,
                async cancellationToken =>
                    await GetCompletionsAsync(
                            await session.Workspace.WaitAsync(cancellationToken).ConfigureAwait(false),
                            snapshot,
                            position,
                            trigger,
                            client,
                            session.Options.CompletionListLimits,
                            session.Completions,
                            cancellationToken
                        )
                        .ConfigureAwait(false)
            )
            .ConfigureAwait(false);
    }

    /// <summary>Resolves an item of the newest list of its document; items of older or closed lists come back as they are.</summary>
    /// <remarks>It goes on when the document changes or closes meanwhile: the item is for the text of its list.</remarks>
    private static async Task<CompletionItem?> ResolveRequestAsync(
        ScriptSession session,
        CompletionItem item,
        CancellationToken token
    )
    {
        if (
            item.Data is not { ValueKind: JsonValueKind.String } data
            || !TryParseData(data.GetString() ?? "", out long resultId, out int index)
            || !session.Completions.TryGet(resultId, out CompletionResult? result)
            || index >= result.Items.Length
        )
        {
            return item;
        }
        ClientFeatures client = session.Client;
        await Task.Yield();
        return await ResolveAsync(item, result, result.Items[index], client, token).ConfigureAwait(false);
    }

    /// <summary>How the edit of an item is computed, or null when the client cannot apply it.</summary>
    private static EditKind? GetEditKind(RoslynCompletionItem item, ClientFeatures client)
    {
        if (!item.IsComplexTextEdit)
            return EditKind.Change;
        if (item.Tags.Contains(WellKnownTags.Snippet))
            return client.CompletionResolvesTextEdit ? EditKind.ResolvedSnippet : null;
        // Without lazily resolved additional edits, the item would insert a name without its using directive.
        return client.CompletionResolvesAdditionalTextEdits ? EditKind.ResolvedAdditionalEdits : null;
    }

    /// <summary>The change of an item of an earlier list, with the span the item has in that list.</summary>
    private static async Task<CompletionChange> GetResolvedChangeAsync(
        CompletionService service,
        CompletionResult result,
        RoslynCompletionItem item,
        CancellationToken cancellationToken
    )
    {
        await s_sharedItems.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (item.Span != result.Span)
            {
                // A later list reused the item; computing the list again gives it this list's span.
                RoslynCompletionList list = await service
                    .GetCompletionsAsync(
                        result.Document,
                        result.Position,
                        result.Trigger,
                        cancellationToken: cancellationToken
                    )
                    .ConfigureAwait(false);
                item =
                    list.ItemsList.FirstOrDefault(i =>
                        i.DisplayText == item.DisplayText
                        && i.SortText == item.SortText
                        && i.InlineDescription == item.InlineDescription
                    ) ?? item;
            }
            return await service
                .GetChangeAsync(result.Document, item, commitCharacter: null, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            s_sharedItems.Release();
        }
    }

    private static async Task<(TextSpan Span, string NewText)> GetChangeAsync(
        CompletionService service,
        Document document,
        RoslynCompletionItem item,
        CancellationToken cancellationToken
    )
    {
        CompletionChange change = await service
            .GetChangeAsync(document, item, commitCharacter: null, cancellationToken)
            .ConfigureAwait(false);
        return (change.TextChange.Span, change.TextChange.NewText ?? "");
    }

    /// <summary>
    /// The ranks of the items to send. Long lists are cut to the items that match the typed text, best matches first,
    /// with the editor's own <see cref="CompletionMatcher"/>, and marked incomplete so the client asks again as the
    /// user keeps typing.
    /// </summary>
    private static (int[] Ranks, bool Incomplete) Select(
        RoslynCompletionItem[] items,
        string typed,
        CompletionListLimits limits
    )
    {
        if (items.Length <= limits.FilterAbove)
            return ([.. Enumerable.Range(0, items.Length)], false);
        int[] matching =
        [
            .. Enumerable
                .Range(0, items.Length)
                .Select(rank => (Rank: rank, Score: CompletionMatcher.Score(items[rank].FilterText, typed)))
                .Where(match => match.Score > 0)
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Rank)
                .Take(limits.MaxItems)
                .Select(match => match.Rank)
                .Order(),
        ];
        return (matching, matching.Length < items.Length);
    }

    private static CompletionListJson Write(
        List<Item> items,
        bool incomplete,
        TextSpan defaultSpan,
        List<string> defaultCommitCharacters,
        LineMap lines,
        ClientFeatures client
    )
    {
        // The filter can keep no items at all; the buffer still needs room for an empty list.
        var buffer = new ArrayBufferWriter<byte>(Math.Max(items.Count, 1) * 128);
        using (var json = new Utf8JsonWriter(buffer, s_writerOptions))
        {
            json.WriteStartObject();
            json.WriteBoolean("isIncomplete", incomplete);
            if (client.CompletionDefaultEditRange || client.CompletionDefaultCommitCharacters)
            {
                json.WriteStartObject("itemDefaults");
                if (client.CompletionDefaultEditRange)
                    WriteRange(json, "editRange", lines.GetRange(defaultSpan));
                if (client.CompletionDefaultCommitCharacters)
                    WriteStrings(json, "commitCharacters", defaultCommitCharacters);
                json.WriteEndObject();
            }
            json.WriteStartArray("items");
            foreach (Item item in items)
            {
                json.WriteStartObject();
                json.WriteString("label", item.Label);
                if (item.Description is not null)
                {
                    json.WriteStartObject("labelDetails");
                    json.WriteString("description", item.Description);
                    json.WriteEndObject();
                }
                json.WriteNumber("kind", (int)item.Kind);
                json.WriteString("sortText", item.SortText);
                if (item.FilterText is not null)
                    json.WriteString("filterText", item.FilterText);
                if (item.Preselect)
                    json.WriteBoolean("preselect", true);
                if (client.CompletionDefaultEditRange && item.Span == defaultSpan)
                {
                    if (item.NewText != item.Label)
                        json.WriteString("textEditText", item.NewText);
                }
                else
                {
                    json.WriteStartObject("textEdit");
                    WriteRange(json, "range", lines.GetRange(item.Span));
                    json.WriteString("newText", item.NewText);
                    json.WriteEndObject();
                }
                if (item.CommitCharacters is not null)
                    WriteStrings(json, "commitCharacters", item.CommitCharacters);
                json.WriteString("data", item.Data);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return new CompletionListJson(buffer.WrittenMemory);
    }

    private static void WriteRange(Utf8JsonWriter json, string name, DocumentRange range)
    {
        json.WriteStartObject(name);
        WritePosition(json, "start", range.Start);
        WritePosition(json, "end", range.End);
        json.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter json, string name, Position position)
    {
        json.WriteStartObject(name);
        json.WriteNumber("line", position.Line);
        json.WriteNumber("character", position.Character);
        json.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, List<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
            json.WriteStringValue(value);
        json.WriteEndArray();
    }

    private static List<string> GetCommitCharacters(
        CompletionRules listRules,
        CompletionItemRules itemRules,
        Dictionary<ImmutableArray<CharacterSetModificationRule>, List<string>> cache
    )
    {
        if (cache.TryGetValue(itemRules.CommitCharacterRules, out List<string>? cached))
            return cached;
        var characters = new HashSet<char>(listRules.DefaultCommitCharacters);
        foreach (CharacterSetModificationRule rule in itemRules.CommitCharacterRules)
        {
            switch (rule.Kind)
            {
                case CharacterSetModificationKind.Add:
                    characters.UnionWith(rule.Characters);
                    break;
                case CharacterSetModificationKind.Remove:
                    characters.ExceptWith(rule.Characters);
                    break;
                case CharacterSetModificationKind.Replace:
                    characters = [.. rule.Characters];
                    break;
            }
        }
        List<string> result = [.. characters.Order().Select(c => c.ToString())];
        cache.Add(itemRules.CommitCharacterRules, result);
        return result;
    }

    private static CompletionService GetService(Document document) =>
        CompletionService.GetService(document)
        ?? throw new InvalidOperationException("Roslyn did not provide a completion service.");

    private static bool TryParseData(string data, out long resultId, out int index)
    {
        int separator = data.IndexOf(':', StringComparison.Ordinal);
        index = 0;
        resultId = 0;
        return separator > 0
            && long.TryParse(data.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out resultId)
            && int.TryParse(data.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static string EscapeSnippet(string text) =>
        new StringBuilder(text).Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}").ToString();

    private static List<string> ToStrings(ImmutableArray<char> characters) => [.. characters.Select(c => c.ToString())];

    private static CompletionItemKind GetKind(ImmutableArray<string> tags)
    {
        foreach (string tag in tags)
        {
            CompletionItemKind? kind = tag switch
            {
                WellKnownTags.Keyword or WellKnownTags.Intrinsic => CompletionItemKind.Keyword,
                WellKnownTags.Snippet => CompletionItemKind.Snippet,
                WellKnownTags.Namespace or WellKnownTags.Module => CompletionItemKind.Module,
                WellKnownTags.Class => CompletionItemKind.Class,
                WellKnownTags.Structure => CompletionItemKind.Struct,
                WellKnownTags.Interface => CompletionItemKind.Interface,
                WellKnownTags.Enum => CompletionItemKind.Enum,
                WellKnownTags.EnumMember => CompletionItemKind.EnumMember,
                WellKnownTags.Delegate => CompletionItemKind.Function,
                WellKnownTags.TypeParameter => CompletionItemKind.TypeParameter,
                WellKnownTags.Method or WellKnownTags.ExtensionMethod => CompletionItemKind.Method,
                WellKnownTags.Property => CompletionItemKind.Property,
                WellKnownTags.Field => CompletionItemKind.Field,
                WellKnownTags.Event => CompletionItemKind.Event,
                WellKnownTags.Constant => CompletionItemKind.Constant,
                WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable =>
                    CompletionItemKind.Variable,
                WellKnownTags.Operator => CompletionItemKind.Operator,
                WellKnownTags.Label => CompletionItemKind.Reference,
                _ => null,
            };
            if (kind is { } found)
                return found;
        }
        return CompletionItemKind.Text;
    }

    private enum EditKind
    {
        /// <summary>The edit comes from Roslyn's change for the item.</summary>
        Change,

        /// <summary>The item inserts its name; resolving adds the rest of Roslyn's change as additional edits.</summary>
        ResolvedAdditionalEdits,

        /// <summary>The item inserts its name; resolving replaces the edit with the expanded snippet.</summary>
        ResolvedSnippet,
    }

    /// <param name="Description">Shown next to the label, such as the namespace of a type that is not imported.</param>
    /// <param name="SortText">The item's rank in Roslyn's order.</param>
    /// <param name="CommitCharacters">Null when the item uses the list's or the server's default.</param>
    /// <param name="Data">Identifies the item for resolve.</param>
    private sealed record Item(
        string Label,
        string? Description,
        CompletionItemKind Kind,
        string SortText,
        string? FilterText,
        bool Preselect,
        TextSpan Span,
        string NewText,
        List<string>? CommitCharacters,
        string Data
    );
}
