using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// One answer to <c>textDocument/completion</c>, from Roslyn's completion service. It holds its version of the script,
/// so its items resolve after the user typed on.
/// </summary>
internal sealed class ScriptCompletions
{
    /// <summary>Roslyn's tag of an obsolete symbol's item, which its public <see cref="WellKnownTags"/> lacks.</summary>
    private const string DeprecatedTag = "Deprecated";

    private static readonly int[] s_deprecated = [CompletionItemTag.Deprecated];

    private readonly ScriptSnapshot _script;
    private readonly Task<Answered?> _answered;

    /// <summary>Starts asking for the list at <paramref name="position"/>.</summary>
    public ScriptCompletions(int id, ScriptSnapshot script, LspPosition position, CancellationToken cancellationToken)
    {
        Id = id;
        _script = script;
        _answered = Task.Run(() => AskAsync(position, cancellationToken), cancellationToken);
        Items = ToLspAsync(cancellationToken);
    }

    /// <summary>Names this list among the server's: its items carry it, to resolve in it.</summary>
    public int Id { get; }

    /// <summary>The answer.</summary>
    public Task<LspCompletionList?> Items { get; }

    /// <summary><paramref name="item"/> with its signature and documentation.</summary>
    public async Task<LspCompletionItem?> ResolveAsync(
        LspCompletionItem item,
        int index,
        CancellationToken cancellationToken
    )
    {
        if (await _answered.ConfigureAwait(false) is not { } answered || index >= answered.List.ItemsList.Count)
            return item;
        CompletionDescription? description = await answered
            .Service.GetDescriptionAsync(_script.Document, answered.List.ItemsList[index], cancellationToken)
            .ConfigureAwait(false);
        if (description is null)
            return item;
        // Roslyn's description is the signature, then a line break, then what the XML docs say.
        int lineBreak = description.TaggedParts.ToList().FindIndex(part => part.Tag == TextTags.LineBreak);
        string detail = string.Concat(
            (lineBreak < 0 ? description.TaggedParts : description.TaggedParts[..lineBreak]).Select(part => part.Text)
        );
        string documentation =
            lineBreak < 0
                ? ""
                : string.Concat(description.TaggedParts[(lineBreak + 1)..].Select(part => part.Text)).Trim();
        return item with
        {
            Detail = detail,
            Documentation =
                documentation.Length == 0 ? null : new LspMarkupContent("markdown", Markdown.Escape(documentation)),
        };
    }

    private async Task<Answered?> AskAsync(LspPosition position, CancellationToken cancellationToken)
    {
        if (CompletionService.GetService(_script.Document) is not { } service)
            return null;
        int offset = _script.Lines.ToOffset(position);
        CompletionList list = await service
            .GetCompletionsAsync(_script.Document, offset, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Answered(service, list);
    }

    private async Task<LspCompletionList?> ToLspAsync(CancellationToken cancellationToken)
    {
        if (await _answered.ConfigureAwait(false) is not (var service, var list))
            return null;
        var items = new List<LspCompletionItem>(list.ItemsList.Count);
        // Nearly every item replaces the same word: one range, written as often, is built once.
        (TextSpan Span, LspRange? Range) last = default;
        for (int index = 0; index < list.ItemsList.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletionItem item = list.ItemsList[index];
            // Edits elsewhere in the text, such as an override's, are out of scope: the client takes no such edit.
            if (item.IsComplexTextEdit)
                continue;
            CompletionChange change = await service
                .GetChangeAsync(_script.Document, item, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            TextSpan span = change.TextChange.Span;
            if (last.Range is null || last.Span != span)
                last = (span, new LspRange(_script.Lines.ToPosition(span.Start), _script.Lines.ToPosition(span.End)));
            string label = item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;
            // Roslyn's edit goes over the @ of an escaped word, and the client matches what was typed from the edit's start.
            // A keyword keeps its filter text: committing it would drop the @, so an escaped word doesn't match it.
            bool isEscaped =
                span.Length > 0 && _script.Text[span.Start] == '@' && !item.Tags.Contains(WellKnownTags.Keyword);
            string filterText = isEscaped ? "@" + item.FilterText.TrimStart('@') : item.FilterText;
            items.Add(
                new LspCompletionItem(
                    label,
                    Kind(item),
                    new LspTextEdit(last.Range, change.TextChange.NewText ?? ""),
                    new JsonArray(Id, index),
                    item.SortText == label ? null : item.SortText,
                    filterText == label ? null : filterText,
                    item.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
                    Tags: item.Tags.Contains(DeprecatedTag) ? s_deprecated : null
                )
            );
        }
        return new LspCompletionList(IsIncomplete: false, items);
    }

    private sealed record Answered(CompletionService Service, CompletionList List);

    /// <summary>The protocol's <c>CompletionItemKind</c> of Roslyn's first tag that names a kind.</summary>
    private static int Kind(CompletionItem item)
    {
        foreach (string tag in item.Tags)
        {
            int kind = tag switch
            {
                WellKnownTags.Method or WellKnownTags.ExtensionMethod => 2,
                WellKnownTags.Delegate => 3,
                WellKnownTags.Field => 5,
                WellKnownTags.Local or WellKnownTags.Parameter or WellKnownTags.RangeVariable => 6,
                WellKnownTags.Class => 7,
                WellKnownTags.Interface => 8,
                WellKnownTags.Namespace or WellKnownTags.Module => 9,
                WellKnownTags.Property => 10,
                WellKnownTags.Enum => 13,
                WellKnownTags.Keyword or WellKnownTags.Intrinsic => 14,
                WellKnownTags.Snippet => 15,
                WellKnownTags.EnumMember => 20,
                WellKnownTags.Constant => 21,
                WellKnownTags.Structure => 22,
                WellKnownTags.Event => 23,
                WellKnownTags.Operator => 24,
                WellKnownTags.TypeParameter => 25,
                _ => 0,
            };
            if (kind != 0)
                return kind;
        }
        return 1;
    }
}
