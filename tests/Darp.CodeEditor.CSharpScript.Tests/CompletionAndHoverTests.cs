using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;
using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class CompletionAndHoverTests(ScriptServerFixture fixture)
{
    private const int MethodKind = 2;
    private const int PropertyKind = 10;
    private const int KeywordKind = 14;
    private const int ClassKind = 7;

    private readonly ScriptServerFixture _fixture = fixture;

    private static string HoverText(HoverInfo hover) => string.Join("\n", hover.Contents.Select(c => c.Text));

    [Fact]
    public async Task Globals_members_complete_and_hover_with_their_xml_docs()
    {
        using var script = await _fixture.OpenAsync("var text = Greeting|;");
        CompletionList list = await script.CompleteAsync();
        CompletionItem greeting = list.Items.Single(item => item.Label == "Greeting");
        greeting.Kind.ShouldBe(PropertyKind);
        CompletionItem resolved = (
            await list.ResolveAsync(greeting, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        resolved.Detail.ShouldNotBeNull().ShouldContain("Greeting");
        resolved.Documentation.ShouldNotBeNull().Text.ShouldContain("The greeting that scripts read.");

        string hover = HoverText((await script.HoverAsync(script.Caret - 1)).ShouldNotBeNull());
        hover.ShouldContain("```csharp");
        hover.ShouldContain("string Greeting { get; }");
        hover.ShouldContain("The greeting that scripts read.");
    }

    [Fact]
    public async Task Dotnet_members_hover_with_their_xml_docs()
    {
        // At runtime string is in System.Private.CoreLib, and its docs are in the reference pack's System.Runtime.xml.
        using var script = await _fixture.OpenAsync("var text = string.Con|cat(\"a\", \"b\");");
        string hover = HoverText((await script.HoverAsync()).ShouldNotBeNull());
        hover.ShouldContain("string.Concat(");
        hover.ShouldContain("Concatenates two specified instances of");
    }

    [Fact]
    public async Task Dotnet_docs_show_code_as_code_spans()
    {
        // Its returns: An <c>IEnumerable<IGrouping<TKey, TSource>></c> in C# or ... where each <see cref="..."/> ...
        using var script = await _fixture.OpenAsync("var groups = new[] { 1 }.Group|By(n => n % 2);");
        string hover = HoverText((await script.HoverAsync()).ShouldNotBeNull());
        hover.ShouldContain("**Returns:** An `IEnumerable<IGrouping<TKey, TSource>>` in C\\# or");
        hover.ShouldContain("each `IGrouping<out TKey, out TElement>` object");
        hover.ShouldContain("**Exceptions:** `ArgumentNullException`");
    }

    [Fact]
    public async Task Docs_name_code_as_code_spans_and_label_sections_in_bold()
    {
        using var script = await _fixture.OpenAsync("var copies = Repeat|(\"a\", 2);");
        string hover = HoverText((await script.HoverAsync(script.Caret - 1)).ShouldNotBeNull());
        hover.ShouldContain("Repeats `text`, or returns `null` for none.\n\nEach copy follows the last.");
        hover.ShouldContain("**Returns:** The copies.");

        CompletionList list = await script.CompleteAsync();
        CompletionItem resolved = (
            await list.ResolveAsync(
                list.Items.Single(item => item.Label == "Repeat"),
                TestContext.Current.CancellationToken
            )
        ).ShouldNotBeNull();
        resolved.Documentation.ShouldNotBeNull().Text.ShouldContain("Repeats `text`, or returns `null` for none.");
    }

    [Fact]
    public async Task Inherited_globals_members_complete_and_hover()
    {
        using var script = await _fixture.OpenAsync("var value = InheritedValue|;");
        CompletionList list = await script.CompleteAsync();
        CompletionItem item = list.Items.Single(i => i.Label == "InheritedValue");
        CompletionItem resolved = (
            await list.ResolveAsync(item, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        resolved.Documentation.ShouldNotBeNull().Text.ShouldContain("A value that the globals inherit.");
        HoverText((await script.HoverAsync(script.Caret - 1)).ShouldNotBeNull()).ShouldContain("InheritedValue");
    }

    [Fact]
    public async Task A_list_has_keywords_and_kinds_of_items()
    {
        using var script = await _fixture.OpenAsync("|");
        CompletionList list = await script.CompleteAsync();
        list.Items.Single(item => item.Label == "await").Kind.ShouldBe(KeywordKind);
        list.Items.First(item => item.Label == "Add").Kind.ShouldBe(MethodKind);
        list.Items.First(item => item.Label == "Greeting").Kind.ShouldBe(PropertyKind);
        list.Items.First(item => item.Label == "Task").Kind.ShouldBe(ClassKind);
    }

    [Fact]
    public async Task The_scripts_variables_come_first_then_members_then_objects_then_types_and_keywords()
    {
        using var script = await _fixture.OpenAsync("var zebra = 1;\nforeach (var apple in new[] { 1 })\n{\n    |\n}");
        CompletionList list = await script.CompleteAsync();

        // As the client orders them before anything is typed.
        CompletionItem[] items = [.. list.Items.OrderBy(item => item.SortKey)];
        items.Take(2).Select(item => item.Label).ShouldBe(["apple", "zebra"]);
        int rest = Array.FindIndex(items, item => item.Kind is KeywordKind or ClassKind);
        string[] members = [.. items[2..rest].Select(item => item.Label)];
        string[] objects = ["Equals", "GetHashCode", "GetType", "ReferenceEquals", "ToString"];
        members[^objects.Length..].ShouldBe(objects);
        members.ShouldContain("Greeting");
        members.ShouldContain("Add");
        items[rest..].ShouldAllBe(item => item.Kind != PropertyKind && item.Kind != MethodKind);
    }

    [Fact]
    public async Task Named_arguments_come_after_the_variables_and_before_members()
    {
        using var script = await _fixture.OpenAsync("var zebra = 1;\nNamed(|)");
        CompletionList list = await script.CompleteAsync();

        CompletionItem[] items = [.. list.Items.OrderBy(item => item.SortKey)];
        items.Take(3).Select(item => item.Label).ShouldBe(["zebra", "name:", "value:"]);
        items[3].Kind.ShouldBeOneOf([PropertyKind, MethodKind]);
        // A named argument shows no type.
        items[1].LabelDetails.ShouldBeNull();
    }

    [Fact]
    public async Task Variables_and_members_show_their_type_and_methods_their_return_type()
    {
        using var script = await _fixture.OpenAsync(
            "var zebra = 1;\nforeach (var apple in new[] { \"a\" })\n{\n    |\n}"
        );
        CompletionList list = await script.CompleteAsync();
        string? TypeOf(string label) => list.Items.First(item => item.Label == label).LabelDetails?.Description;
        // A var of a reference type is nullable, as its hover says.
        TypeOf("apple").ShouldBe("string?");
        TypeOf("zebra").ShouldBe("int");
        TypeOf("Greeting").ShouldBe("string");
        TypeOf("Add").ShouldBe("int");
        TypeOf("Write").ShouldBe("void");
        TypeOf("Repeat").ShouldBe("string?");
        TypeOf("Pick<>").ShouldBe("T");
        TypeOf("@class").ShouldBe("int");
        // As .NET declares object's.
        TypeOf("ToString").ShouldBe("string?");
        TypeOf("Task").ShouldBeNull();
        TypeOf("await").ShouldBeNull();

        // After a dot, the receiver's members, also extension methods.
        using var access = await _fixture.OpenAsync("using System.Linq;\nGreeting.|");
        CompletionList members = await access.CompleteAsync();
        string? MemberTypeOf(string label) =>
            members.Items.First(item => item.Label == label).LabelDetails?.Description;
        MemberTypeOf("Length").ShouldBe("int");
        MemberTypeOf("ToUpper").ShouldBe("string");
        MemberTypeOf("Count<>").ShouldBe("int");
    }

    [Fact]
    public async Task A_deprecated_item_shows_as_deprecated()
    {
        using var script = await _fixture.OpenAsync("[Obsolete(\"Use New.\")] void Old() {}\nvoid New() {}\nOl|");
        CompletionList list = await script.CompleteAsync();
        list.Items.Single(item => item.Label == "Old").IsDeprecated.ShouldBeTrue();
        list.Items.Single(item => item.Label == "New").IsDeprecated.ShouldBeFalse();
    }

    [Fact]
    public async Task Directives_that_scripts_cannot_use_are_not_suggested()
    {
        using var script = await _fixture.OpenAsync("int line = 1;\n#|");
        string[] labels = [.. (await script.CompleteAsync()).Items.Select(item => item.Label)];
        labels.ShouldContain("region");
        labels.ShouldContain("nullable");
        labels.ShouldNotContain("r");
        labels.ShouldNotContain("load");
        labels.ShouldNotContain("line");

        // Without "r", "region" matches "#r": typing on, as with a space, must not commit it.
        using var typed = await _fixture.OpenAsync("#r|");
        CompletionList list = await typed.CompleteAsync();
        list.Items.ShouldContain(item => item.Label == "region");
        list.Items.ShouldAllBe(item => item.CommitCharacters.Length == 0);
    }

    [Fact]
    public async Task Punctuation_commits_a_member_once_its_name_is_typed()
    {
        using var script = await _fixture.OpenAsync("var length = Greeting.|");
        // Right after the dot, punctuation goes in as it is, and typing a name asks again.
        CompletionList afterDot = await script.CompleteAsync(
            new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.')
        );
        afterDot.IsIncomplete.ShouldBeTrue();
        afterDot.Items.Single(item => item.Label == "Length").CommitCharacters.ShouldBeEmpty();

        script.Document.Replace(new TextRange(script.Caret, 0), "Le");
        script.Caret += 2;
        CompletionList typed = await script.CompleteAsync(
            new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions)
        );
        typed.IsIncomplete.ShouldBeFalse();
        string commit = typed.Items.Single(item => item.Label == "Length").CommitCharacters;
        commit.ShouldContain('.');
        commit.ShouldContain(';');
        commit.ShouldContain('(');
    }

    [Fact]
    public async Task Nothing_commits_by_punctuation_where_a_new_name_may_be_typed()
    {
        using var script = await _fixture.OpenAsync("var upper = Greeting.Where(c|");
        CompletionList list = await script.CompleteAsync();
        list.Items.ShouldNotBeEmpty();
        list.Items.ShouldAllBe(item => item.CommitCharacters.Length == 0);
    }

    [Fact]
    public async Task A_named_argument_puts_in_its_colon_and_over_one_that_follows()
    {
        using var script = await _fixture.OpenAsync("var sum = Add(le|");
        CompletionItem left = (await script.CompleteAsync()).Items.Single(item => item.Label == "left:");
        left.Edit.NewText.ShouldBe("left:");
        // Typed, the colon commits it, and goes in once.
        left.CommitCharacters.ShouldContain(':');

        // One colon stays, also when it is typed.
        const string text = "var sum = Add(le : 1, right: 2);";
        using var followed = await _fixture.OpenAsync(text.Insert(text.IndexOf(" :", StringComparison.Ordinal), "|"));
        left = (await followed.CompleteAsync()).Items.Single(item => item.Label == "left:");
        left.Edit.ShouldBe(
            new CompletionEdit(
                TextRange.FromBounds(text.IndexOf("le", StringComparison.Ordinal), text.IndexOf(':') + 1),
                "left:"
            )
        );
        left.CommitCharacters.ShouldContain(':');

        // After a line break or a comment, the colon stays where it is, and typing another commits nothing.
        foreach (string apart in new[] { "var sum = Add(le|\n: 1, right: 2);", "var sum = Add(le| /* l */ : 1);" })
        {
            using var separated = await _fixture.OpenAsync(apart);
            left = (await separated.CompleteAsync()).Items.Single(item => item.Label == "left:");
            left.Edit.ShouldBe(new CompletionEdit(TextRange.FromBounds(separated.Caret - 2, separated.Caret), "left"));
            left.CommitCharacters.ShouldNotContain(':');
        }
    }

    [Fact]
    public async Task A_colon_opens_a_list_only_in_a_format()
    {
        var colon = new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, ':');
        using var format = await _fixture.OpenAsync("var now = $\"{DateTime.Now:|}\";");
        format.Document.Features.ShouldNotBeNull().Completion.ShouldNotBeNull().TriggerCharacters.ShouldContain(':');
        (await format.CompleteAsync(colon)).Items.ShouldContain(item => item.Label == "d");

        using var argument = await _fixture.OpenAsync("var sum = Add(left:|");
        (await argument.CompleteAsync(colon)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_documentation_comment_completes_its_tags()
    {
        using var script = await _fixture.OpenAsync("/// <|\nvoid Report() {}");
        script.Document.Features.ShouldNotBeNull().Completion.ShouldNotBeNull().TriggerCharacters.ShouldContain('<');
        CompletionList list = await script.CompleteAsync(
            new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '<')
        );

        CompletionItem summary = list.Items.Single(item => item.Label == "summary");
        summary.Edit.NewText.ShouldBe("summary");
        // Roslyn describes a tag with nothing: it resolves without a detail to show.
        CompletionItem resolved = (
            await list.ResolveAsync(summary, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        resolved.Detail.ShouldBeNull();
        resolved.Documentation.ShouldBeNull();
        // An element without content goes in whole, over the typed "<", with the caret between its quotes.
        CompletionItem see = list.Items.Single(item => item.Label == "see");
        see.Edit.ShouldBe(new CompletionEdit(new TextRange(4, 1), "<see cref=\"$0\"/>"));
        see.Snippet.ShouldNotBeNull();
        see.FilterText.ShouldBe("<see");
    }

    [Fact]
    public async Task A_member_list_completes_the_type_of_the_receiver()
    {
        using var script = await _fixture.OpenAsync("Greeting.|");
        CompletionList list = await script.CompleteAsync();
        list.Items.ShouldContain(item => item.Label == "Length");
        list.Items.ShouldNotContain(item => item.Label == "await");
    }

    [Fact]
    public async Task An_item_resolves_after_the_text_changed()
    {
        using var script = await _fixture.OpenAsync("var text = Gre|");
        CompletionList list = await script.CompleteAsync();
        CompletionItem greeting = list.Items.Single(item => item.Label == "Greeting");
        script.Document.Replace(new TextRange(script.Caret, 0), "e");
        CompletionItem resolved = (
            await list.ResolveAsync(greeting, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        resolved.Documentation.ShouldNotBeNull().Text.ShouldContain("The greeting that scripts read.");
    }

    [Fact]
    public async Task An_escaped_identifier_completes_with_one_at_sign()
    {
        using var script = await _fixture.OpenAsync("var value = @ev|");
        CompletionList list = await script.CompleteAsync();
        TextRange typed = TextRange.FromBounds(script.Caret - 3, script.Caret);

        CompletionItem escaped = list.Items.Single(item => item.Edit.NewText == "@event");
        escaped.Edit.Range.ShouldBe(typed);
        escaped.FilterText.ShouldBe("@event");

        CompletionItem plain = list.Items.Single(item => item.Label == "Everything");
        plain.Edit.Range.ShouldBe(typed);
        plain.Edit.NewText.ShouldBe("Everything");
        plain.FilterText.ShouldBe("@Everything");
    }

    [Fact]
    public async Task An_escaped_word_matches_identifiers_and_no_keyword()
    {
        using var script = await _fixture.OpenAsync("@ev|");
        CompletionList list = await script.CompleteAsync();
        list.Items.Single(item => item.Edit.NewText == "@event").FilterText.ShouldBe("@event");
        list.Items.Single(item => item.Kind == KeywordKind && item.Label == "event").FilterText.ShouldBe("event");
    }

    [Fact]
    public async Task There_is_no_list_after_a_lone_at_sign()
    {
        using var script = await _fixture.OpenAsync("var value = @|");
        (await script.CompleteAsync()).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hover_shows_an_escaped_identifier()
    {
        using var script = await _fixture.OpenAsync("var value = @class|;");
        HoverText((await script.HoverAsync(script.Caret - 2)).ShouldNotBeNull()).ShouldContain("An identifier");
    }

    [Fact]
    public async Task Semantic_tokens_cover_the_lines_asked_for()
    {
        using var script = await _fixture.OpenAsync(
            "int first = 1;\nvar sum = Add(first, Greeting.Length);\nint last = 2;"
        );
        TextSnapshot snapshot = script.Document.CurrentSnapshot;
        SemanticTokenLines tokens = (
            await script.Document.SemanticTokensAsync(
                new TextPosition(2, 1),
                new TextPosition(2, 38),
                3,
                TestContext.Current.CancellationToken
            )
        ).ShouldNotBeNull();
        tokens.FirstLine.ShouldBe(1);
        int[] line = tokens.Lines.ShouldHaveSingleItem();
        string[] types = [.. line.Chunk(SemanticTokenLines.Stride).Select(t => tokens.Legend.TokenTypes[t[2]])];
        string[] names =
        [
            .. line.Chunk(SemanticTokenLines.Stride)
                .Select(t => snapshot.GetText().Split('\n')[1].Substring(t[0], t[1])),
        ];
        names.ShouldBe(["sum", "Add", "first", "Greeting", "Length"]);
        types.ShouldBe(["variable", "method", "variable", "property", "property"]);
    }

    [Fact]
    public async Task Semantic_tokens_mark_static_members_and_readonly_fields()
    {
        using var script = await _fixture.OpenAsync(
            "class C { public static readonly int Field = 1; public readonly int Inst = 2; public static int M() => 1; }\n"
                + "const int K = 2;\n"
                + "var sum = C.M() + C.Field + new C().Inst + K;"
        );
        SemanticTokenLines tokens = (
            await script.Document.SemanticTokensAsync(
                new TextPosition(3, 1),
                new TextPosition(3, 46),
                3,
                TestContext.Current.CancellationToken
            )
        ).ShouldNotBeNull();
        string line = script.Document.CurrentSnapshot.GetText().Split('\n')[2];
        string[] described =
        [
            .. tokens
                .Lines.ShouldHaveSingleItem()
                .Chunk(SemanticTokenLines.Stride)
                .Select(t =>
                    string.Join(
                        ' ',
                        [
                            line.Substring(t[0], t[1]),
                            tokens.Legend.TokenTypes[t[2]],
                            .. tokens.Legend.TokenModifiers.Where((_, bit) => (t[3] & (1 << bit)) != 0),
                        ]
                    )
                ),
        ];
        described.ShouldBe([
            "sum variable",
            "C class",
            "M method static",
            "C class",
            "Field variable readonly static",
            "C class",
            "Inst variable readonly",
            "K variable readonly static",
        ]);
    }

    [Fact]
    public async Task Documents_of_two_kinds_of_script_see_their_own_globals()
    {
        using var other = await Script<OtherGlobals>.OpenAsync(
            _fixture.Server,
            new CSharpScriptEnvironment<OtherGlobals>(),
            "OtherValue|"
        );
        CompletionList list = await other.CompleteAsync();
        list.Items.ShouldContain(item => item.Label == "OtherValue");
        list.Items.ShouldNotContain(item => item.Label == "Greeting");
    }
}
