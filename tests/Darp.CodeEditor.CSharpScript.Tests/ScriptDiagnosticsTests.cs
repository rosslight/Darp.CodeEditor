using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class ScriptDiagnosticsTests(ScriptServerFixture fixture)
{
    private const string End = Script<ScriptTestGlobals>.EndMarker;

    private readonly ScriptServerFixture _fixture = fixture;

    [Theory]
    [InlineData("#r \"System.Text.Json\"", "#r")]
    [InlineData("#load \"other.csx\"", "#load")]
    [InlineData("#line 100", "#line")]
    public async Task A_directive_that_scripts_cannot_use_is_an_error(string directive, string name)
    {
        using var script = await _fixture.OpenAsync($"{directive}\n{End}");
        IReadOnlyList<Diagnostic> diagnostics = await script.DiagnosticsAsync();
        Diagnostic error = diagnostics.First(d => d.Message.Contains(name, StringComparison.Ordinal));
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.Range.Start.ShouldBe(0);
    }

    [Fact]
    public async Task Incremental_edits_reach_the_compiler()
    {
        using var script = await _fixture.OpenAsync($"int value = \"text\";\n{End}");
        (await script.DiagnosticsAsync()).ShouldHaveSingleItem().Code.ShouldBe("CS0029");

        script.Document.Replace(new TextRange(12, 6), "42");
        (await script.DiagnosticsAsync()).ShouldBeEmpty();

        script.Document.Replace(new TextRange(0, 0), "string first = 1;\n");
        Diagnostic error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        error.Code.ShouldBe("CS0029");
        error.Range.Start.ShouldBe(15);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task Positions_follow_the_clients_lines_with_unicode_breaks_in_the_text(string lineBreak)
    {
        // Roslyn ends lines at these three as well; for the editor they are characters of a line.
        string text = $"/* a\u2028b\u2029c\u0085d */{lineBreak}/* \u2028 */ int x = \"text\";{lineBreak}{End}";
        using var script = await _fixture.OpenAsync(text);
        Diagnostic error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        int expected = text.IndexOf("\"text\"", StringComparison.Ordinal);
        error.Range.Start.ShouldBe(expected);
        error.Range.Length.ShouldBe(6);

        int hovered = text.IndexOf(" x ", StringComparison.Ordinal) + 1;
        var hover = (await script.HoverAsync(hovered)).ShouldNotBeNull();
        hover.Range.ShouldBe(new TextRange(hovered, 1));
    }

    [Fact]
    public async Task An_edit_with_unicode_breaks_keeps_positions_after_it()
    {
        using var script = await _fixture.OpenAsync($"var a = 1;\nint x = \"text\";\n{End}");
        await script.DiagnosticsAsync();
        script.Document.Replace(new TextRange(0, 0), "// \u2028\u2029\n");
        Diagnostic error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        error.Range.Start.ShouldBe(
            script.Document.CurrentSnapshot.GetText().IndexOf("\"text\"", StringComparison.Ordinal)
        );

        script.Document.Replace(new TextRange(3, 1), "");
        error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        error.Range.Start.ShouldBe(
            script.Document.CurrentSnapshot.GetText().IndexOf("\"text\"", StringComparison.Ordinal)
        );
    }
}
