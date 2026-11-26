using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;

namespace Darp.CodeEditor.CSharpScript.Tests;

/// <summary>Completion of types and extension methods from namespaces that a script does not import.</summary>
[Collection(ScriptServerFixture.Collection)]
public sealed class ImportCompletionTests(ScriptServerFixture fixture)
{
    private readonly ScriptServerFixture _fixture = fixture;

    [Fact]
    public async Task A_type_completes_with_its_using_after_the_scripts_header()
    {
        const string header = "// Reads the options.\n";
        using var script = await _fixture.OpenAsync(header + "var options = new JsonSerializerOpt|");
        CompletionList list = await script.CompleteAsync();
        list.IsIncomplete.ShouldBeFalse();
        CompletionItem item = list.Items.Single(i => i.Label == "JsonSerializerOptions");
        item.Edit.NewText.ShouldBe("JsonSerializerOptions");
        // A type shows no type, and no namespace either.
        item.LabelDetails.ShouldBeNull();

        CompletionItem resolved = (
            await list.ResolveAsync(item, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        resolved.Detail.ShouldNotBeNull().ShouldContain("System.Text.Json.JsonSerializerOptions");
        Apply(script, resolved.AdditionalEdits.ShouldNotBeNull())
            .ShouldBe(header + "using System.Text.Json;\n\nvar options = new JsonSerializerOpt");
    }

    [Fact]
    public async Task An_extension_method_completes_with_its_using_among_the_scripts_usings()
    {
        const string code = "var data = await new HttpClient().GetFromJson";
        using var script = await _fixture.OpenAsync("using System.Text;\n" + code + "|");
        CompletionList list = await script.CompleteAsync();
        CompletionItem item = list.Items.First(i => i.Label.StartsWith("GetFromJsonAsync", StringComparison.Ordinal));
        // Each row's return type, of its overloads, as the receiver calls them.
        string? Returns(string label) => list.Items.Single(i => i.Label == label).LabelDetails?.Description;
        Returns("GetFromJsonAsync").ShouldBe("Task<object?>");
        Returns("GetFromJsonAsync<>").ShouldBe("Task<TValue?>");

        CompletionItem resolved = (
            await list.ResolveAsync(item, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        Apply(script, resolved.AdditionalEdits.ShouldNotBeNull())
            .ShouldBe("using System.Net.Http.Json;\nusing System.Text;\n" + code);
    }

    [Fact]
    public async Task An_item_of_a_list_that_a_newer_list_replaces_still_resolves_its_using()
    {
        using var script = await _fixture.OpenAsync("var builder = new Str|");
        CompletionList first = await script.CompleteAsync();
        CompletionItem item = first.Items.Single(i => i.Label == "StringBuilder");
        // The user typed on, and the editor asked again: until it switches lists, it still commits from the first.
        script.Document.Replace(new TextRange(script.Caret, 0), "i");
        script.Caret++;
        await script.CompleteAsync(new CompletionTrigger(CompletionTriggerKind.TriggerForIncompleteCompletions));

        CompletionItem resolved = (
            await first.ResolveAsync(item, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        CompletionEdit edit = resolved.AdditionalEdits.ShouldNotBeNull().ShouldHaveSingleItem();
        edit.Range.ShouldBe(new TextRange(0, 0));
        // In Roslyn's line breaks: the editor puts in the document's.
        edit.NewText.ReplaceLineEndings("\n").ShouldBe("using System.Text;\n\n");
    }

    [Fact]
    public async Task An_escaped_name_completes_a_type_with_its_using_as_other_identifiers()
    {
        using var script = await _fixture.OpenAsync("var json = new @JsonSerializ|");
        CompletionItem item = (await script.CompleteAsync()).Items.Single(i => i.Label == "JsonSerializer");
        item.Edit.Range.ShouldBe(TextRange.FromBounds(script.Caret - "@JsonSerializ".Length, script.Caret));
        item.Edit.NewText.ShouldBe("JsonSerializer");
        item.FilterText.ShouldBe("@JsonSerializer");
    }

    [Fact]
    public async Task A_name_with_two_neighbouring_characters_swapped_completes_a_type_as_the_editor_matches_it()
    {
        using var script = await _fixture.OpenAsync("var json = new JsnoSerializ|");
        (await script.CompleteAsync()).Items.ShouldContain(item => item.Label == "JsonSerializer");
    }

    [Fact]
    public async Task Nothing_comes_from_the_assemblies_that_scripts_may_not_name()
    {
        using var types = await _fixture.OpenAsync("var unmarked = new UnmarkedTy|");
        (await types.CompleteAsync()).Items.ShouldNotContain(item => item.Label == "UnmarkedType");

        // A marked assembly has an extension method of this name in that namespace too, for another receiver.
        using var extensions = await _fixture.OpenAsync("var text = Greeting.Repeat|");
        (await extensions.CompleteAsync()).Items.ShouldNotContain(item => item.Label == "Repeated");

        using var marked = await _fixture.OpenAsync("var number = 1;\nvar same = number.Repeat|");
        (await marked.CompleteAsync()).Items.ShouldContain(item => item.Label == "Repeated");
    }

    [Fact]
    public async Task Imports_wait_for_a_typed_name_and_the_best_of_many_come_first()
    {
        // Nothing typed matches every type: the list leaves them out, and typing a name asks again.
        using var empty = await _fixture.OpenAsync("var options = |");
        CompletionList all = await empty.CompleteAsync();
        all.IsIncomplete.ShouldBeTrue();
        all.Items.ShouldNotContain(item => item.Label == "JsonSerializerOptions");

        // A short name matches too many to send: the best of them come, and typing on asks again.
        using var many = await _fixture.OpenAsync("var builder = new Str|");
        CompletionList best = await many.CompleteAsync();
        best.IsIncomplete.ShouldBeTrue();
        best.Items.ShouldContain(item => item.Label == "StringBuilder");
    }

    /// <summary>The script's text with <paramref name="edits"/> made.</summary>
    private static string Apply(Script<ScriptTests.Api.ScriptTestGlobals> script, IEnumerable<CompletionEdit> edits)
    {
        string text = script.Document.CurrentSnapshot.GetText();
        foreach (CompletionEdit edit in edits.OrderByDescending(edit => edit.Range.Start))
            text = text[..edit.Range.Start] + edit.NewText + text[edit.Range.End..];
        return text;
    }
}
