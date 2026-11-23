using Darp.CodeEditor.LanguageClient;
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
