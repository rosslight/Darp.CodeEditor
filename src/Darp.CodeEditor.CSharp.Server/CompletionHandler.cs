using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using EmmyLua.LanguageServer.Framework;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server.Options;
using EmmyLua.LanguageServer.Framework.Protocol.JsonRpc;
using EmmyLua.LanguageServer.Framework.Protocol.Message.Completion;
using EmmyLua.LanguageServer.Framework.Protocol.Model;
using EmmyLua.LanguageServer.Framework.Protocol.Model.Kind;
using EmmyLua.LanguageServer.Framework.Protocol.Model.TextEdit;
using EmmyLua.LanguageServer.Framework.Protocol.Model.Union;
using EmmyLua.LanguageServer.Framework.Server.Handler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using CompletionItem = EmmyLua.LanguageServer.Framework.Protocol.Message.Completion.CompletionItem;
using CompletionItemKind = EmmyLua.LanguageServer.Framework.Protocol.Message.Completion.CompletionItemKind;
using LanguageServer = EmmyLua.LanguageServer.Framework.Server.LanguageServer;
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
/// Lists are written straight to JSON: word lists have thousands of items, and the framework's model writes fields
/// the server does not use. Items are sorted as Roslyn sorts them; <c>sortText</c> is the item's rank.
/// </para>
/// <para>
/// A completion request is not cancelled when the text changes: the client can still use the list while the user
/// keeps typing the same word.
/// </para>
/// </remarks>
internal sealed class CompletionHandler : IJsonHandler
{
    private static readonly List<string> s_defaultCommitCharacters = ToStrings(
        CompletionRules.Default.DefaultCommitCharacters
    );

    /// <summary>The result of a request with no completion list. A null document would leave out the result.</summary>
    private static readonly JsonDocument s_noList = JsonDocument.Parse("null");

    /// <summary>
    /// Serializes Roslyn's completion lists with computing the changes of their items. Roslyn shares some item objects,
    /// such as types from namespaces that are not imported, between lists of all documents and sets their span for
    /// each new list; a change computed with another list's span is wrong or fails.
    /// </summary>
    private static readonly SemaphoreSlim s_sharedItems = new(1, 1);

    private readonly ScriptSession _session;

    public CompletionHandler(ScriptSession session) => _session = session;

    public void RegisterHandler(LSPCommunicationBase lspCommunication)
    {
        JsonSerializerOptions options = lspCommunication.JsonSerializerOptions;
        lspCommunication.AddRequestHandler(
            "textDocument/completion",
            (message, token) => Handle(ReadParams<CompletionParams>(message, options), token)
        );
        lspCommunication.AddRequestHandler(
            "completionItem/resolve",
            async (message, token) =>
                JsonSerializer.SerializeToDocument(
                    await Resolve(ReadParams<CompletionItem>(message, options), token).ConfigureAwait(false),
                    options
                )
        );
    }

    public void RegisterCapability(ServerCapabilities serverCapabilities, ClientCapabilities clientCapabilities) =>
        serverCapabilities.CompletionProvider = new CompletionOptions
        {
            TriggerCharacters = ["."],
            AllCommitCharacters = s_defaultCommitCharacters,
            ResolveProvider = true,
        };

    public void RegisterDynamicCapability(LanguageServer server, ClientCapabilities clientCapabilities) { }

    /// <summary>Computes the completion list at <paramref name="position"/> as the JSON of an LSP <c>CompletionList</c>.</summary>
    /// <returns>Null when nothing applies.</returns>
    public static async Task<JsonDocument?> GetCompletionsAsync(
        Document document,
        LineMap lines,
        int position,
        RoslynCompletionTrigger trigger,
        ClientFeatures client,
        CompletionResults results,
        string documentKey,
        CancellationToken cancellationToken
    )
    {
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
                    lines,
                    position,
                    trigger,
                    client,
                    results,
                    documentKey,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        finally
        {
            s_sharedItems.Release();
        }
    }

    private static async Task<JsonDocument?> CreateListAsync(
        CompletionService service,
        Document document,
        LineMap lines,
        int position,
        RoslynCompletionTrigger trigger,
        ClientFeatures client,
        CompletionResults results,
        string documentKey,
        CancellationToken cancellationToken
    )
    {
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

        long resultId = results.NextId();
        // In suggestion mode, such as when naming a new variable, typing must not commit an item.
        bool suggestionMode = roslynList.SuggestionModeItem is not null;
        List<string> defaultCommitCharacters = suggestionMode
            ? []
            : ToStrings(roslynList.Rules.DefaultCommitCharacters);
        var commitCharacters = new Dictionary<ImmutableArray<CharacterSetModificationRule>, List<string>>();
        string rankFormat = "D" + roslynItems.Length.ToString(CultureInfo.InvariantCulture).Length;
        var items = new List<Item>(roslynItems.Length);
        for (int rank = 0; rank < roslynItems.Length; rank++)
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
                    string.Create(CultureInfo.InvariantCulture, $"{resultId}:{rank}")
                )
            );
        }
        results.Set(
            documentKey,
            new CompletionResult(resultId, document, lines, position, trigger, roslynList.Span, [.. roslynItems])
        );
        return Write(items, roslynList.Span, defaultCommitCharacters, lines, client);
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
            item.Documentation = Markup.FromDescription(description.TaggedParts, client.CompletionMarkdown);

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
            if (client.CompletionSnippets)
            {
                int caret = Math.Clamp(
                    (change.NewPosition ?? textChange.Span.End) - textChange.Span.Start,
                    0,
                    newText.Length
                );
                newText = EscapeSnippet(newText[..caret]) + "$0" + EscapeSnippet(newText[caret..]);
                item.InsertTextFormat = InsertTextFormat.Snippet;
            }
            item.TextEditText = null;
            item.TextEdit = new TextEditOrInsertReplaceEdit(
                new TextEdit { Range = result.Lines.GetRange(textChange.Span), NewText = newText }
            );
            return item;
        }
        // The item inserted its display text; everything else in the change, such as a using directive, is extra.
        List<AnnotatedTextEdit> additional =
        [
            .. change
                .TextChanges.Where(c => c.NewText != roslynItem.DisplayText)
                .Select(c => new AnnotatedTextEdit
                {
                    Range = result.Lines.GetRange(c.Span),
                    NewText = c.NewText ?? "",
                }),
        ];
        if (additional.Count < change.TextChanges.Length)
            item.AdditionalTextEdits = additional;
        else
            Console.Error.WriteLine(
                $"Completion item {roslynItem.DisplayText}: its change does not insert its display text."
            );
        return item;
    }

    private Task<JsonDocument?> Handle(CompletionParams request, CancellationToken token)
    {
        if (!_session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return Task.FromResult<JsonDocument?>(s_noList);
        int position = snapshot.Lines.GetOffset(request.Position);
        RoslynCompletionTrigger trigger = request.Context
            is {
                TriggerKind.Value: 2, // TriggerCharacter
                TriggerCharacter: [char character],
            }
            ? RoslynCompletionTrigger.CreateInsertionTrigger(character)
            : RoslynCompletionTrigger.Invoke;
        ScriptSession session = _session;
        return Task.Run<JsonDocument?>(
            async () =>
            {
                ScriptWorkspace workspace = await session.Workspace.WaitAsync(token).ConfigureAwait(false);
                return await GetCompletionsAsync(
                            snapshot.Owner.GetDocument(workspace, snapshot),
                            snapshot.Lines,
                            position,
                            trigger,
                            session.Client,
                            session.Completions,
                            snapshot.Owner.Key,
                            token
                        )
                        .ConfigureAwait(false)
                    ?? s_noList;
            },
            token
        );
    }

    private Task<CompletionItem> Resolve(CompletionItem item, CancellationToken token)
    {
        if (
            item.Data?.Value is not string data
            || !TryParseData(data, out long resultId, out int index)
            || !_session.Completions.TryGet(resultId, out CompletionResult? result)
            || index >= result.Items.Length
        )
        {
            return Task.FromResult(item);
        }
        ClientFeatures client = _session.Client;
        return Task.Run(() => ResolveAsync(item, result, result.Items[index], client, token), token);
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

    private static JsonDocument Write(
        List<Item> items,
        TextSpan defaultSpan,
        List<string> defaultCommitCharacters,
        LineMap lines,
        ClientFeatures client
    )
    {
        var buffer = new ArrayBufferWriter<byte>(items.Count * 128);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("isIncomplete", false);
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
        return JsonDocument.Parse(buffer.WrittenMemory);
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

    private static T ReadParams<T>(RequestMessage message, JsonSerializerOptions options) =>
        message.Params is { } parameters
            ? parameters.Deserialize<T>(options) ?? throw new JsonException($"{message.Method} has null parameters.")
            : throw new JsonException($"{message.Method} has no parameters.");

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
