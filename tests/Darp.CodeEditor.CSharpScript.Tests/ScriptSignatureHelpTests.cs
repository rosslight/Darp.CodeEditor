using Darp.CodeEditor.LanguageClient;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class ScriptSignatureHelpTests(ScriptServerFixture fixture)
{
    private readonly ScriptServerFixture _fixture = fixture;

    private static SignatureInfo Active(SignatureHelpInfo help) => help.Signatures[help.ActiveSignature];

    private static string ActiveParameter(SignatureInfo signature) =>
        signature.ActiveParameter < 0
            ? ""
            : signature.Label.Substring(
                signature.Parameters[signature.ActiveParameter].Start,
                signature.Parameters[signature.ActiveParameter].Length
            );

    [Fact]
    public async Task An_unfinished_call_shows_the_overload_its_arguments_select()
    {
        using var script = await _fixture.OpenAsync("await Task.Delay(1000, |");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.Count.ShouldBeGreaterThan(1);
        Active(help).Label.ShouldContain("Delay(int millisecondsDelay, CancellationToken cancellationToken)");
        ActiveParameter(Active(help)).ShouldBe("CancellationToken cancellationToken");
    }

    [Fact]
    public async Task An_unfinished_generic_call_infers_its_type_arguments()
    {
        using var script = await _fixture.OpenAsync("var picked = Pick(new[] { 1, 2 }|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("Pick<int>(int[] values, int index)");
        ActiveParameter(Active(help)).ShouldBe("int[] values");
    }

    [Fact]
    public async Task An_unfinished_generic_array_call_selects_the_generic_overload_before_the_comma()
    {
        using var script = await _fixture.OpenAsync("int[] numbers = [1];\nvar index = Array.IndexOf(numbers|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("IndexOf<int>(int[] array, int value)");
    }

    [Fact]
    public async Task A_named_argument_activates_its_parameter()
    {
        using var script = await _fixture.OpenAsync("Named(name: |");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("string name");
    }

    [Fact]
    public async Task Further_arguments_stay_on_a_params_parameter()
    {
        using var script = await _fixture.OpenAsync("Join(\",\", 1, 2, 3|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("params int[] values");
    }

    [Fact]
    public async Task An_indented_continuation_line_keeps_the_call()
    {
        using var script = await _fixture.OpenAsync("Add(1,\n        |");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("int right");
    }

    [Fact]
    public async Task A_retrigger_follows_an_arguments_new_type()
    {
        using var script = await _fixture.OpenAsync("Write(1|)");
        SignatureHelpInfo first = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(first).Label.ShouldContain("Write(int value)");

        script.Document.Replace(new TextRange(6, 1), "\"text\"");
        script.Caret = 12;
        SignatureHelpInfo second = (await script.SignatureHelpAsync(first)).ShouldNotBeNull();
        Active(second).Label.ShouldContain("Write(string value)");
    }

    [Fact]
    public async Task A_retrigger_keeps_the_signature_the_user_chose()
    {
        using var script = await _fixture.OpenAsync("Write(1|)");
        SignatureHelpInfo first = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        int other = first.ActiveSignature == 0 ? 1 : 0;
        SignatureHelpInfo chosen = first with { ActiveSignature = other };

        script.Document.Replace(new TextRange(7, 0), "2");
        script.Caret = 8;
        SignatureHelpInfo second = (await script.SignatureHelpAsync(chosen)).ShouldNotBeNull();
        Active(second).Label.ShouldBe(chosen.Signatures[other].Label);
    }

    [Fact]
    public async Task Outside_a_call_there_is_no_signature_help()
    {
        using var script = await _fixture.OpenAsync("Add(1, 2);|");
        (await script.SignatureHelpAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task The_inner_call_of_nested_calls_shows()
    {
        using var script = await _fixture.OpenAsync("Add(1, Add(2, |");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("int right");
    }

    [Fact]
    public async Task A_constructor_shows_its_overloads()
    {
        using var script = await _fixture.OpenAsync("var builder = new System.Text.StringBuilder(|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.Count.ShouldBeGreaterThan(1);
    }
}
