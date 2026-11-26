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
    public async Task An_unfinished_conditional_call_keeps_its_receiver()
    {
        using var script = await _fixture.OpenAsync("string? s = \"\";\ns?.Replace(\"a\", |)");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("Replace(string oldValue, string? newValue)");
        ActiveParameter(Active(help)).ShouldBe("string? newValue");
    }

    [Fact]
    public async Task An_unfinished_target_typed_new_keeps_its_type()
    {
        using var script = await _fixture.OpenAsync("System.Text.StringBuilder sb = new(10, |");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("StringBuilder(int capacity, int maxCapacity)");
        ActiveParameter(Active(help)).ShouldBe("int maxCapacity");
    }

    [Fact]
    public async Task An_unfinished_call_selects_among_the_scripts_own_overloads()
    {
        using var script = await _fixture.OpenAsync(
            "int Local(int number, int other) => other;\nint Local(string text, int other) => other;\nLocal(\"a\", |"
        );
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.Count.ShouldBe(2);
        Active(help).Label.ShouldContain("Local(string text, int other)");
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

    [Fact]
    public async Task A_type_receiver_shows_its_static_methods_and_no_extension_methods()
    {
        using var script = await _fixture.OpenAsync("string.Concat(|1);");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.ShouldAllBe(signature => signature.Label.StartsWith("string string.Concat"));
    }

    [Fact]
    public async Task A_value_receiver_shows_no_static_methods()
    {
        using var script = await _fixture.OpenAsync("var text = \"a\";\ntext.Equals(|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.ShouldContain(signature => signature.Label.Contains("Equals(string? value)"));
        help.Signatures.ShouldNotContain(signature => signature.Label.Contains("Equals(string? a, string? b)"));
    }

    [Fact]
    public async Task A_protected_method_shows_only_through_a_receiver_of_the_calling_type()
    {
        using var script = await _fixture.OpenAsync(
            """
            class Base
            {
                protected void Run(int number) { }
                public void Run(string text) { }
            }
            class Derived : Base
            {
                void Call(Base other) => other.Run(|);
            }
            """
        );
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.ShouldHaveSingleItem().Label.ShouldContain("Run(string text)");
    }

    [Fact]
    public async Task A_method_that_a_more_specific_overload_hides_and_one_hidden_from_editors_do_not_show()
    {
        using var script = await _fixture.OpenAsync(
            """
            class Box<T>
            {
                public void Put(T value) { }
                public void Put(int value) { }
            }
            new Box<int>().Put(|);
            """
        );
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        help.Signatures.ShouldHaveSingleItem();

        using var globals = await _fixture.OpenAsync("Write(|");
        SignatureHelpInfo written = (await globals.SignatureHelpAsync()).ShouldNotBeNull();
        written.Signatures.ShouldNotContain(signature => signature.Label.Contains("double"));
        written.Signatures.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_constructor_initializer_shows_the_other_constructors()
    {
        using var script = await _fixture.OpenAsync(
            """
            class Base
            {
                public Base(string name) { }
            }
            class Derived : Base
            {
                Derived(int number) : base("") { }
                Derived() : this(|) { }
            }
            """
        );
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("Derived(int number)");
        help.Signatures.ShouldHaveSingleItem();
        ActiveParameter(Active(help)).ShouldBe("int number");

        script.Caret = script.Document.Text.IndexOf("base(", StringComparison.Ordinal) + 5;
        help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("Base(string name)");
    }

    [Fact]
    public async Task An_attribute_shows_its_constructors_and_settable_members()
    {
        using var script = await _fixture.OpenAsync("[Obsolete(\"old\", |)] class Old { }");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("ObsoleteAttribute(string? message, bool error");
        ActiveParameter(Active(help)).ShouldBe("bool error");

        script.Document.Replace(new TextRange(script.Caret, 0), "DiagnosticId = ");
        script.Caret += "DiagnosticId = ".Length;
        help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("DiagnosticId = string?");

        // An argument by place goes to no property.
        using var empty = await _fixture.OpenAsync("[Obsolete(|)] class Old { }");
        help = (await empty.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldStartWith("ObsoleteAttribute(Properties: ");
        ActiveParameter(Active(help)).ShouldBe("");
    }

    [Fact]
    public async Task An_indexer_shows_its_parameters()
    {
        using var script = await _fixture.OpenAsync("var numbers = new Dictionary<string, int>();\nvar n = numbers[|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldBe("int Dictionary<string, int>[string key]");
        ActiveParameter(Active(help)).ShouldBe("string key");
    }

    [Fact]
    public async Task A_type_argument_list_shows_the_type_parameters()
    {
        using var script = await _fixture.OpenAsync("T Echo<T>(T value) => value;\nEcho<|");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldBe("T Echo<T>(T value)");
        ActiveParameter(Active(help)).ShouldBe("T");

        using var type = await _fixture.OpenAsync("var numbers = new Dictionary<string, |");
        help = (await type.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldBe("Dictionary<TKey, TValue> where TKey : notnull");
        ActiveParameter(Active(help)).ShouldBe("TValue");
    }

    [Fact]
    public async Task A_comparison_has_no_type_argument_list_and_keeps_the_call_around_it()
    {
        using var script = await _fixture.OpenAsync("int a = 1, b = 2;\nvar less = a <|");
        (await script.SignatureHelpAsync()).ShouldBeNull();

        using var call = await _fixture.OpenAsync("int a = 1, b = 2;\nNamed(a <|");
        SignatureHelpInfo help = (await call.SignatureHelpAsync()).ShouldNotBeNull();
        ActiveParameter(Active(help)).ShouldBe("int value");
    }

    [Fact]
    public async Task A_collection_initializer_entry_shows_its_Add_method()
    {
        using var script = await _fixture.OpenAsync("var names = new Dictionary<int, string> { { 1, |\"a\" } };");
        SignatureHelpInfo help = (await script.SignatureHelpAsync()).ShouldNotBeNull();
        Active(help).Label.ShouldContain("Add(int key, string value)");
        ActiveParameter(Active(help)).ShouldBe("string value");
    }

    [Fact]
    public async Task A_signature_shows_its_summary_and_the_active_parameters_description_as_text()
    {
        using var script = await _fixture.OpenAsync("Repeat(|");
        SignatureInfo signature = Active((await script.SignatureHelpAsync()).ShouldNotBeNull());
        signature
            .Documentation.ShouldNotBeNull()
            .Text.ShouldBe("Repeats text, or returns null for none.\n\nEach copy follows the last.");
        signature.Parameters[0].Documentation.ShouldNotBeNull().Text.ShouldBe("The text, as Greeting reads.");
        signature.Parameters[1].Documentation.ShouldNotBeNull().Text.ShouldBe("How often.");
    }

    [Fact]
    public async Task Docs_that_inherit_come_from_the_member_they_implement()
    {
        using var script = await _fixture.OpenAsync(
            """
            interface IRunner
            {
                /// <summary>Runs <paramref name="times"/> times.</summary>
                /// <param name="times">How often.</param>
                void Run(int times);
            }
            class Runner : IRunner
            {
                /// <inheritdoc />
                public void Run(int times) { }
            }
            new Runner().Run(|
            """
        );
        SignatureInfo signature = Active((await script.SignatureHelpAsync()).ShouldNotBeNull());
        signature.Documentation.ShouldNotBeNull().Text.ShouldBe("Runs times times.");
        signature.Parameters[0].Documentation.ShouldNotBeNull().Text.ShouldBe("How often.");
    }
}
