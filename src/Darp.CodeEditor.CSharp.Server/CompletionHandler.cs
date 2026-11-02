using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Client.ClientCapabilities;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server;
using EmmyLua.LanguageServer.Framework.Protocol.Capabilities.Server.Options;
using EmmyLua.LanguageServer.Framework.Protocol.Message.Completion;
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
using CompletionList = EmmyLua.LanguageServer.Framework.Protocol.Message.Completion.CompletionList;
using RoslynCompletionItem = Microsoft.CodeAnalysis.Completion.CompletionItem;
using RoslynCompletionList = Microsoft.CodeAnalysis.Completion.CompletionList;
using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary><c>textDocument/completion</c> and <c>completionItem/resolve</c> from Roslyn's completion service.</summary>
/// <remarks>
/// <para>
/// Each item's edit comes from <see cref="CompletionService.GetChangeAsync"/>. Items whose change goes beyond
/// replacing the typed word, such as types from namespaces that are not imported, insert their name; resolving them
/// adds the rest of the change, such as the <c>using</c> directive, as additional edits. Computing those changes up
/// front would take a quarter of a millisecond per item, and word lists have thousands.
/// </para>
/// <para>
/// A completion request is not cancelled when the text changes: the client can still use the list while the user
/// keeps typing the same word.
/// </para>
/// </remarks>
internal sealed class CompletionHandler : CompletionHandlerBase
{
    private static readonly List<string> s_defaultCommitCharacters = ToStrings(
        CompletionRules.Default.DefaultCommitCharacters
    );

    private readonly ScriptSession _session;

    public CompletionHandler(ScriptSession session) => _session = session;

    public override void RegisterCapability(
        ServerCapabilities serverCapabilities,
        ClientCapabilities clientCapabilities
    ) =>
        serverCapabilities.CompletionProvider = new CompletionOptions
        {
            TriggerCharacters = ["."],
            AllCommitCharacters = s_defaultCommitCharacters,
            ResolveProvider = true,
        };

    protected override Task<CompletionResponse?> Handle(CompletionParams request, CancellationToken token)
    {
        if (!_session.TryGetSnapshot(request.TextDocument.Uri, out DocumentSnapshot? snapshot))
            return Task.FromResult<CompletionResponse?>(null);
        int position = snapshot.Lines.GetOffset(request.Position);
        RoslynCompletionTrigger trigger = request.Context
            is {
                TriggerKind.Value: 2, // TriggerCharacter
                TriggerCharacter: [char character],
            }
            ? RoslynCompletionTrigger.CreateInsertionTrigger(character)
            : RoslynCompletionTrigger.Invoke;
        ScriptSession session = _session;
        return Task.Run(
            async () =>
            {
                ScriptWorkspace workspace = await session.Workspace.WaitAsync(token).ConfigureAwait(false);
                CompletionList? list = await GetCompletionsAsync(
                        snapshot.Owner.GetDocument(workspace, snapshot),
                        snapshot.Lines,
                        position,
                        trigger,
                        session.Client,
                        session.Completions,
                        snapshot.Owner.Key,
                        token
                    )
                    .ConfigureAwait(false);
                return list is null ? null : new CompletionResponse(list);
            },
            token
        );
    }

    protected override Task<CompletionItem> Resolve(CompletionItem item, CancellationToken token)
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

    public static async Task<CompletionList?> GetCompletionsAsync(
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
        CompletionService service =
            CompletionService.GetService(document)
            ?? throw new InvalidOperationException("Roslyn did not provide a completion service.");
        if (trigger.Kind == Microsoft.CodeAnalysis.Completion.CompletionTriggerKind.Insertion)
        {
            SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!service.ShouldTriggerCompletion(text, position, trigger))
                return null;
        }
        RoslynCompletionList roslynList = await service
            .GetCompletionsAsync(document, position, trigger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (roslynList.ItemsList.Count == 0)
            return null;

        long resultId = results.NextId();
        // In suggestion mode, such as when naming a new variable, typing must not commit an item.
        List<string> defaultCommitCharacters = roslynList.SuggestionModeItem is null
            ? ToStrings(roslynList.Rules.DefaultCommitCharacters)
            : [];
        var commitCharacters = new Dictionary<ImmutableArray<CharacterSetModificationRule>, List<string>>();
        var items = new List<CompletionItem>(roslynList.ItemsList.Count);
        var roslynItems = ImmutableArray.CreateBuilder<RoslynCompletionItem>(roslynList.ItemsList.Count);
        foreach (RoslynCompletionItem roslynItem in roslynList.ItemsList)
        {
            bool resolvesEdit = roslynItem.IsComplexTextEdit && !IsSnippet(roslynItem);
            // Without lazily resolved additional edits, the item would insert a name without its using directive.
            if (resolvesEdit && !client.CompletionResolvesAdditionalTextEdits)
                continue;
            string label = roslynItem.DisplayTextPrefix + roslynItem.DisplayText + roslynItem.DisplayTextSuffix;
            var item = new CompletionItem
            {
                Label = label,
                Kind = GetKind(roslynItem.Tags),
                SortText = roslynItem.SortText == label ? null : roslynItem.SortText,
                FilterText = roslynItem.FilterText == label ? null : roslynItem.FilterText,
                Preselect = roslynItem.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
                LabelDetails =
                    client.CompletionLabelDetails && roslynItem.InlineDescription.Length > 0
                        ? new CompletionItemLabelDetails { Description = roslynItem.InlineDescription }
                        : null,
                Data = string.Create(CultureInfo.InvariantCulture, $"{resultId}:{roslynItems.Count}"),
            };

            (TextSpan span, string newText) = resolvesEdit
                ? (roslynItem.Span, roslynItem.DisplayText)
                : await GetEditAsync(service, document, roslynItem, client, item, cancellationToken)
                    .ConfigureAwait(false);
            if (client.CompletionDefaultEditRange && span == roslynList.Span)
                item.TextEditText = newText == label ? null : newText;
            else
                item.TextEdit = new TextEditOrInsertReplaceEdit(
                    new TextEdit { Range = lines.GetRange(span), NewText = newText }
                );

            List<string> itemCommitCharacters =
                roslynItem.Rules.CommitCharacterRules.IsEmpty || roslynList.SuggestionModeItem is not null
                    ? defaultCommitCharacters
                    : GetCommitCharacters(roslynList.Rules, roslynItem.Rules, commitCharacters);
            if (client.CompletionDefaultCommitCharacters)
            {
                if (itemCommitCharacters != defaultCommitCharacters)
                    item.CommitCharacters = itemCommitCharacters;
            }
            else if (
                client.CompletionCommitCharacters && !itemCommitCharacters.SequenceEqual(s_defaultCommitCharacters)
            )
            {
                item.CommitCharacters = itemCommitCharacters;
            }

            items.Add(item);
            roslynItems.Add(roslynItem);
        }

        results.Set(documentKey, new CompletionResult(resultId, document, lines, roslynItems.DrainToImmutable()));
        CompletionListItemDefault? defaults =
            client.CompletionDefaultEditRange || client.CompletionDefaultCommitCharacters
                ? new CompletionListItemDefault
                {
                    EditRange = client.CompletionDefaultEditRange
                        ? new CompletionListItemDefaultEditRange(lines.GetRange(roslynList.Span))
                        : null,
                    CommitCharacters = client.CompletionDefaultCommitCharacters ? defaultCommitCharacters : null,
                }
                : null;
        return new CompletionList
        {
            IsIncomplete = false,
            ItemDefaults = defaults,
            Items = items,
        };
    }

    public static async Task<CompletionItem> ResolveAsync(
        CompletionItem item,
        CompletionResult result,
        RoslynCompletionItem roslynItem,
        ClientFeatures client,
        CancellationToken cancellationToken
    )
    {
        CompletionService service =
            CompletionService.GetService(result.Document)
            ?? throw new InvalidOperationException("Roslyn did not provide a completion service.");
        CompletionDescription? description = await service
            .GetDescriptionAsync(result.Document, roslynItem, cancellationToken)
            .ConfigureAwait(false);
        if (description is { TaggedParts.Length: > 0 })
            item.Documentation = Markup.FromDescription(description.TaggedParts, client.CompletionMarkdown);

        if (roslynItem.IsComplexTextEdit && !IsSnippet(roslynItem))
        {
            CompletionChange change = await service
                .GetChangeAsync(result.Document, roslynItem, commitCharacter: null, cancellationToken)
                .ConfigureAwait(false);
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
        }
        return item;
    }

    /// <summary>The edit of an item from Roslyn: usually the typed word replaced by the item's name.</summary>
    private static async Task<(TextSpan Span, string NewText)> GetEditAsync(
        CompletionService service,
        Document document,
        RoslynCompletionItem roslynItem,
        ClientFeatures client,
        CompletionItem item,
        CancellationToken cancellationToken
    )
    {
        CompletionChange change = await service
            .GetChangeAsync(document, roslynItem, commitCharacter: null, cancellationToken)
            .ConfigureAwait(false);
        TextChange textChange = change.TextChange;
        string newText = textChange.NewText ?? "";
        // Snippets such as "for" expand to several lines; with snippet support the caret lands where Roslyn puts it.
        if (
            client.CompletionSnippets
            && IsSnippet(roslynItem)
            && change.NewPosition is { } caret
            && caret >= textChange.Span.Start
            && caret - textChange.Span.Start <= newText.Length
        )
        {
            int relative = caret - textChange.Span.Start;
            newText = EscapeSnippet(newText[..relative]) + "$0" + EscapeSnippet(newText[relative..]);
            item.InsertTextFormat = InsertTextFormat.Snippet;
        }
        return (textChange.Span, newText);
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

    private static bool IsSnippet(RoslynCompletionItem item) => item.Tags.Contains(WellKnownTags.Snippet);

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
}
