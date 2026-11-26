using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class OccurrenceTests(ScriptServerFixture fixture)
{
    private const string Text = "int x = 0;\nvoid F() { int x = 1; Console.WriteLine(x); x = 2; }\nx++;";

    private readonly ScriptServerFixture _fixture = fixture;

    [Fact]
    public async Task A_local_has_its_own_occurrences_with_reads_and_writes()
    {
        using var script = await _fixture.OpenAsync(Text);
        int read = Text.IndexOf("(x)", StringComparison.Ordinal) + 1;
        (await OccurrencesAsync(script, read + 1)).ShouldBe([
            (Text.IndexOf("x = 1", StringComparison.Ordinal), OccurrenceKind.Text),
            (read, OccurrenceKind.Read),
            (Text.IndexOf("x = 2", StringComparison.Ordinal), OccurrenceKind.Write),
        ]);
    }

    [Fact]
    public async Task A_top_level_variable_is_apart_from_a_local_of_its_name()
    {
        using var script = await _fixture.OpenAsync(Text);
        int increment = Text.IndexOf("x++", StringComparison.Ordinal);
        (await OccurrencesAsync(script, increment)).ShouldBe([
            (Text.IndexOf('x', StringComparison.Ordinal), OccurrenceKind.Text),
            (increment, OccurrenceKind.Write),
        ]);
    }

    [Fact]
    public async Task A_member_written_through_access_deconstruction_or_out_is_written()
    {
        const string text =
            "class P { public int Value; }\nvar p = new P();\np.Value = 1;\n(p.Value, var y) = (2, 3);\n"
            + "int.TryParse(\"4\", out p.Value);\nConsole.WriteLine(p.Value);";
        using var script = await _fixture.OpenAsync(text);
        int[] values = [.. Enumerable.Range(0, text.Length).Where(i => text.AsSpan(i).StartsWith("Value"))];
        (await OccurrencesAsync(script, values[^1])).ShouldBe([
            (values[0], OccurrenceKind.Text),
            (values[1], OccurrenceKind.Write),
            (values[2], OccurrenceKind.Write),
            (values[3], OccurrenceKind.Write),
            (values[4], OccurrenceKind.Read),
        ]);
    }

    [Fact]
    public async Task Text_without_a_symbol_has_no_occurrences()
    {
        using var script = await _fixture.OpenAsync("// x marks the spot\nint x = 0;");
        (await OccurrencesAsync(script, 3)).ShouldBeEmpty();
    }

    /// <summary>Where the occurrences at <paramref name="offset"/> start, each over the same name.</summary>
    private static async Task<(int Start, OccurrenceKind Kind)[]> OccurrencesAsync(
        Script<ScriptTests.Api.ScriptTestGlobals> script,
        int offset
    )
    {
        TextSnapshot snapshot = script.Document.CurrentSnapshot;
        IReadOnlyList<Occurrence> occurrences = (
            await script.Document.DocumentHighlightsAsync(snapshot, offset, TestContext.Current.CancellationToken)
        ).ShouldNotBeNull();
        occurrences.Select(occurrence => snapshot.GetText(occurrence.Range)).Distinct().Count().ShouldBeLessThan(2);
        return [.. occurrences.Select(occurrence => (occurrence.Range.Start, occurrence.Kind))];
    }
}
