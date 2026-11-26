using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Darp.CodeEditor.Lsp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace Darp.CodeEditor.CSharpScript.Server;

/// <summary>
/// One answer to <c>textDocument/completion</c>, from Roslyn's completion service. It holds its version of the script,
/// so its items resolve after the user typed on.
/// </summary>
internal sealed class ScriptCompletions
{
    /// <summary>
    /// The characters that commit an item as they are typed, unless its rules change them: Roslyn's, which its own server
    /// gives clients too.
    /// </summary>
    public static readonly string[] CommitCharacters =
    [
        .. CompletionRules.Default.DefaultCommitCharacters.Select(c => c.ToString()),
    ];

    /// <summary>Roslyn's tag of an obsolete symbol's item, which its public <see cref="WellKnownTags"/> lacks.</summary>
    private const string DeprecatedTag = "Deprecated";

    private static readonly int[] s_deprecated = [CompletionItemTag.Deprecated];

    /// <summary>
    /// The keywords of the directives that scripts can't use (<see cref="ScriptDiagnostics"/>). No other context has
    /// keywords of these names.
    /// </summary>
    private static readonly FrozenSet<string> s_unsupportedDirectives = FrozenSet.Create("r", "load", "line");

    private readonly ScriptSnapshot _script;
    private readonly Task<Answer?> _answer;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Starts asking for the list that <paramref name="parameters"/> ask for.</summary>
    public ScriptCompletions(
        int id,
        ScriptSnapshot script,
        CompletionParams parameters,
        CancellationToken cancellationToken
    )
    {
        Id = id;
        _script = script;
        _cancellationToken = cancellationToken;
        int offset = script.Lines.ToOffset(parameters.Position);
        _answer = Task.Run(
            () => AnswerAsync(offset, Trigger(parameters.Context), cancellationToken),
            cancellationToken
        );
        Items = ItemsAsync();
    }

    /// <summary>Names this list among the server's: its items carry it, to resolve in it.</summary>
    public int Id { get; }

    /// <summary>The answer.</summary>
    public Task<LspCompletionList?> Items { get; }

    /// <summary>Whether the client cancelled the request: its answer never reaches the client.</summary>
    public bool IsCancelled => _cancellationToken.IsCancellationRequested;

    /// <summary>
    /// <paramref name="item"/> with its signature and documentation, and for a type or extension method from a namespace
    /// that the script does not import, the <c>using</c> that it adds.
    /// </summary>
    public async Task<LspCompletionItem?> ResolveAsync(
        LspCompletionItem item,
        int index,
        CancellationToken cancellationToken
    )
    {
        if (await _answer.ConfigureAwait(false) is not { } answer || index >= answer.List.ItemsList.Count)
            return item;
        CompletionItem roslynItem = answer.List.ItemsList[index];
        if (answer.Imports.TryGetValue(index, out CompletionItem? import))
            item = item with { AdditionalTextEdits = await ImportAsync(answer.Service, import, cancellationToken) };
        CompletionDescription? description = await answer
            .Service.GetDescriptionAsync(_script.Document, roslynItem, cancellationToken)
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
            Detail = detail.Length == 0 ? null : detail,
            Documentation =
                documentation.Length == 0 ? null : new LspMarkupContent("markdown", Markdown.Escape(documentation)),
        };
    }

    /// <summary>
    /// Roslyn's trigger for the client's: a trigger character as typed, so that Roslyn answers only where it means
    /// something, and anything else as asked for.
    /// </summary>
    private static CompletionTrigger Trigger(Lsp.CompletionContext context) =>
        context is { TriggerKind: Lsp.CompletionTriggerKind.TriggerCharacter, TriggerCharacter: [char typed] }
            ? CompletionTrigger.CreateInsertionTrigger(typed)
            : CompletionTrigger.Invoke;

    private async Task<LspCompletionList?> ItemsAsync() => (await _answer.ConfigureAwait(false))?.Items;

    private async Task<Answer?> AnswerAsync(int offset, CompletionTrigger trigger, CancellationToken cancellationToken)
    {
        if (CompletionService.GetService(_script.Document) is not { } service)
            return null;
        CompletionList list = await service
            .GetCompletionsAsync(_script.Document, offset, trigger, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        // An empty list has no span of its own to take the typed text from.
        if (list.ItemsList.Count == 0)
            return new Answer(
                service,
                list,
                new LspCompletionList(IsIncomplete: false, []),
                FrozenDictionary<int, CompletionItem>.Empty
            );
        string typed = _script.Text.ToString(TextSpan.FromBounds(list.Span.Start, offset));
        // Without the directives that scripts can't use, another can match what was typed, as "region" matches "#r":
        // only a key commits it then, so that "#r " stays as typed.
        var commit = new CommitRules(list, typed, onlyByKey: list.ItemsList.Any(IsUnsupportedDirective));
        var items = new List<LspCompletionItem>(list.ItemsList.Count);
        var imports = new List<int>();
        bool? inDocumentation = null;
        // Nearly every item replaces the same word: one range, written as often, is built once.
        (TextSpan Span, LspRange? Range) last = default;
        for (int index = 0; index < list.ItemsList.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompletionItem item = list.ItemsList[index];
            if (IsUnsupportedDirective(item))
                continue;
            // Edits elsewhere in the text, such as an override's, are out of scope: the client takes no such edit. Those
            // of a documentation comment's tags are complex only as they go over the "<" before the word or put the
            // caret inside the tag, and go in whole. An import's using comes with resolve.
            if (
                item.IsComplexTextEdit && !(inDocumentation ??= await IsInDocumentationAsync(offset, cancellationToken))
            )
            {
                if (ScriptImports.MayBe(item))
                    imports.Add(index);
                continue;
            }
            CompletionChange change = await service
                .GetChangeAsync(_script.Document, item, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (change.TextChanges.Length > 1)
                continue;
            TextSpan span = change.TextChange.Span;
            string label = Label(item);
            // Roslyn's edit goes over the @ of an escaped word, and the client matches what was typed from the edit's start.
            // A keyword keeps its filter text: committing it would drop the @, so an escaped word doesn't match it.
            bool isEscaped =
                span.Length > 0 && _script.Text[span.Start] == '@' && !item.Tags.Contains(WellKnownTags.Keyword);
            string filterText = isEscaped ? "@" + item.FilterText.TrimStart('@') : item.FilterText;
            // So too over a tag's "<".
            if (span.Start < list.Span.Start)
                filterText = _script.Text.ToString(TextSpan.FromBounds(span.Start, list.Span.Start)) + filterText;
            string newText = change.TextChange.NewText ?? "";
            int caret = change.NewPosition is { } position ? position - span.Start : newText.Length;
            bool isSnippet = caret >= 0 && caret < newText.Length;
            if (isSnippet)
                newText = Snippets.Escape(newText[..caret]) + "$0" + Snippets.Escape(newText[caret..]);
            // Roslyn puts in a named argument's name alone, for the colon to be typed. The item puts in the colon too,
            // and goes over one that follows on the line, so that one stays, also when it is typed: the typed colon
            // commits the item and goes in once. A colon on a later line stays where it is, after the name alone.
            if (item.DisplayTextSuffix == ":" && newText == item.DisplayText)
            {
                (int Offset, bool OnLaterLine)? colon = ColonAfter(span.End);
                if (colon is not { OnLaterLine: true })
                {
                    newText = label;
                    if (colon is { Offset: var end })
                        span = TextSpan.FromBounds(span.Start, end + 1);
                }
            }
            items.Add(ToLsp(item, index, span, newText, filterText, isSnippet));
        }
        bool asksAgain = commit.AsksAgain;
        var sent = new Dictionary<int, CompletionItem>();
        if (imports.Count > 0)
        {
            // Without a typed name, the thousands of them would all be sent; typing one asks again.
            if (typed.Length == 0)
                asksAgain = true;
            else if (
                await _script.Document.Project.GetCompilationAsync(cancellationToken).ConfigureAwait(false) is
                { } compilation
            )
            {
                (IReadOnlyList<int> best, bool more) = ScriptImports.Best(
                    list.ItemsList,
                    imports,
                    typed,
                    compilation,
                    await ReceiverAsync(list.Span, cancellationToken).ConfigureAwait(false),
                    cancellationToken
                );
                foreach (int index in best)
                {
                    CompletionItem item = list.ItemsList[index];
                    // Roslyn's lists share these items, and each list moves them to its span: a copy keeps this one's,
                    // which resolve needs.
                    sent[index] = item.AddProperty(nameof(ScriptCompletions), "");
                    items.Add(ToLsp(item, index, list.Span, item.DisplayText, item.FilterText, isSnippet: false));
                }
                asksAgain |= more;
            }
        }
        return new Answer(service, list, new LspCompletionList(asksAgain, items), sent.ToFrozenDictionary());

        LspCompletionItem ToLsp(
            CompletionItem item,
            int index,
            TextSpan span,
            string newText,
            string filterText,
            bool isSnippet
        )
        {
            if (last.Range is null || last.Span != span)
                last = (span, Range(span));
            string label = Label(item);
            return new LspCompletionItem(
                label,
                Kind(item),
                new LspTextEdit(last.Range, newText),
                new JsonArray(Id, index),
                item.SortText == label ? null : item.SortText,
                filterText == label ? null : filterText,
                item.Rules.MatchPriority == MatchPriority.Preselect ? true : null,
                Tags: item.Tags.Contains(DeprecatedTag) ? s_deprecated : null,
                CommitCharacters: commit.Of(item),
                InsertTextFormat: isSnippet ? Snippets.Format : null
            );
        }
    }

    /// <summary>
    /// The edits besides its name that <paramref name="item"/>, from a namespace that the script does not import, makes:
    /// the <c>using</c>, where Roslyn puts it among the script's, in the version that the list was asked for.
    /// </summary>
    private async Task<LspTextEdit[]> ImportAsync(
        CompletionService service,
        CompletionItem item,
        CancellationToken cancellationToken
    )
    {
        CompletionChange change = await service
            .GetChangeAsync(_script.Document, item, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return
        [
            .. change
                .TextChanges.Where(edit => edit.Span != item.Span)
                .Select(edit => new LspTextEdit(Range(edit.Span), edit.NewText ?? "")),
        ];
    }

    private static bool IsUnsupportedDirective(CompletionItem item) =>
        item.Tags.Contains(WellKnownTags.Keyword) && s_unsupportedDirectives.Contains(item.DisplayText);

    private static string Label(CompletionItem item) =>
        item.DisplayTextPrefix + item.DisplayText + item.DisplayTextSuffix;

    private LspRange Range(TextSpan span) =>
        new(_script.Lines.ToPosition(span.Start), _script.Lines.ToPosition(span.End));

    /// <summary>Whether <paramref name="offset"/> is in a documentation comment.</summary>
    private async Task<bool> IsInDocumentationAsync(int offset, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await _script.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return root
            ?.FindToken(Math.Max(0, offset - 1), findInsideTrivia: true)
            .Parent?.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>()
            is not null;
    }

    /// <summary>
    /// The type of the receiver whose member the word at <paramref name="word"/> is
    /// (<see cref="SignatureCandidates.ReceiverTypeOf"/>); null without a receiver.
    /// </summary>
    private async Task<ITypeSymbol?> ReceiverAsync(TextSpan word, CancellationToken cancellationToken)
    {
        if (await _script.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
            return null;
        SyntaxNode root = await model.SyntaxTree.GetRootAsync(cancellationToken).ConfigureAwait(false);
        SyntaxNode? access = root.FindToken(word.Start).GetPreviousToken().Parent;
        return SignatureCandidates.ReceiverTypeOf(model, access, cancellationToken);
    }

    /// <summary>
    /// The colon that follows <paramref name="offset"/>, past white space, and whether a line ends before it; null for
    /// none.
    /// </summary>
    private (int Offset, bool OnLaterLine)? ColonAfter(int offset)
    {
        SourceText text = _script.Text;
        bool onLaterLine = false;
        while (offset < text.Length && char.IsWhiteSpace(text[offset]))
            onLaterLine |= text[offset++] is '\n' or '\r';
        return offset < text.Length && text[offset] == ':' ? (offset, onLaterLine) : null;
    }

    /// <summary>Roslyn's list, as the client gets it.</summary>
    /// <param name="Service">The service that answered, which resolves the items.</param>
    /// <param name="List">Roslyn's list, whose items the client's carry the index of.</param>
    /// <param name="Items">The list that the client gets.</param>
    /// <param name="Imports">
    /// The items sent from namespaces that the script does not import, by their index, each a copy of this list's own.
    /// </param>
    private sealed record Answer(
        CompletionService Service,
        CompletionList List,
        LspCompletionList Items,
        FrozenDictionary<int, CompletionItem> Imports
    );

    /// <summary>The protocol's snippets, as the client reads them: plain text, with <c>$0</c> where the caret goes.</summary>
    private static class Snippets
    {
        /// <summary>The <c>InsertTextFormat</c> of a snippet.</summary>
        public const int Format = 2;

        /// <summary><paramref name="text"/> as a snippet's text that reads as the same text.</summary>
        public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");
    }

    /// <summary>
    /// Which characters commit the items of a list, as Roslyn's own server decides for clients that always select an
    /// item hard. Nothing commits in a list that only suggests, as where a lambda's parameter may be named, or after
    /// typed punctuation. Before a name is typed, only the items that Roslyn selects hard commit, and typing the name
    /// asks again, for the list whose items commit.
    /// </summary>
    /// <param name="list">The list.</param>
    /// <param name="typed">The text from where the items' word starts up to the caret.</param>
    /// <param name="onlyByKey">Whether nothing in the list commits as it is typed.</param>
    private sealed class CommitRules(CompletionList list, string typed, bool onlyByKey)
    {
        private readonly bool _suggests = onlyByKey || list.SuggestionModeItem is not null || IsPunctuation(typed);
        private readonly bool _typed = typed.Length > 0;

        /// <summary>The characters of each set of an item's rules, worked out once: items share their rules.</summary>
        private readonly Dictionary<ImmutableArray<CharacterSetModificationRule>, string[]> _characters = [];

        /// <summary>Whether the list must be asked for again as the user types: the client asks on each key then.</summary>
        public bool AsksAgain { get; } =
            IsPunctuation(typed)
            || (
                typed.Length == 0
                && list.SuggestionModeItem is null
                && list.ItemsList.Any(item =>
                    item.Rules.SelectionBehavior != CompletionItemSelectionBehavior.HardSelection
                )
            );

        /// <summary>The characters that commit <paramref name="item"/>; null for <see cref="CommitCharacters"/>.</summary>
        public string[]? Of(CompletionItem item)
        {
            if (_suggests || (!_typed && item.Rules.SelectionBehavior != CompletionItemSelectionBehavior.HardSelection))
                return [];
            ImmutableArray<CharacterSetModificationRule> rules = item.Rules.CommitCharacterRules;
            if (rules.IsEmpty)
                return null;
            if (!_characters.TryGetValue(rules, out string[]? characters))
                _characters[rules] = characters = Apply(rules);
            return characters;
        }

        private static string[] Apply(ImmutableArray<CharacterSetModificationRule> rules)
        {
            var characters = new HashSet<char>(CompletionRules.Default.DefaultCommitCharacters);
            foreach (CharacterSetModificationRule rule in rules)
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
            return [.. characters.Select(c => c.ToString())];
        }

        private static bool IsPunctuation(string typed) => typed.Length > 0 && typed.All(char.IsPunctuation);
    }

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
