using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class ScriptEnvironmentTests(ScriptServerFixture fixture)
{
    private readonly ScriptServerFixture _fixture = fixture;

    [Fact]
    public async Task A_marked_assembly_is_visible_by_default()
    {
        using var script = await _fixture.OpenAsync(
            "var twice = ScriptTests.Extra.ExtraTools.Twice(2);\n" + Script<ScriptTestGlobals>.EndMarker
        );
        (await script.DiagnosticsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task References_narrow_the_marked_assemblies_and_keep_the_globals_assembly()
    {
        using var script = await _fixture.OpenAsync(
            "var api = ScriptTests.Api.ApiType.Value;\nvar twice = ScriptTests.Extra.ExtraTools.Twice(2);\n"
                + Script<ScriptTestGlobals>.EndMarker,
            new CSharpScriptEnvironment<ScriptTestGlobals> { References = [] }
        );
        Diagnostic error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.Message.ShouldContain("Extra");
    }

    [Fact]
    public async Task A_framework_assembly_may_be_named_in_references()
    {
        using var script = await _fixture.OpenAsync(
            "var json = System.Text.Json.JsonSerializer.Serialize(1);\n" + Script<ScriptTestGlobals>.EndMarker,
            new CSharpScriptEnvironment<ScriptTestGlobals> { References = ["System.Text.Json"] }
        );
        (await script.DiagnosticsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void A_reference_that_is_not_marked_is_rejected()
    {
        var environment = new CSharpScriptEnvironment<ScriptTestGlobals>
        {
            References = ["Darp.CodeEditor.CSharpScript.Tests.Dependency"],
        };
        Should
            .Throw<ArgumentException>(() => _fixture.Server.CreateDocument(environment))
            .Message.ShouldContain("CSharpScriptReference");
    }

    [Fact]
    public void Globals_in_an_assembly_that_is_not_marked_are_rejected()
    {
        Should
            .Throw<ArgumentException>(() =>
                _fixture.Server.CreateDocument(new CSharpScriptEnvironment<ScriptTests.Dependency.DependencyGlobals>())
            )
            .Message.ShouldContain("CSharpScriptReference");
    }

    [Fact]
    public async Task A_dependency_binds_signatures_without_exposing_its_types()
    {
        using var script = await _fixture.OpenAsync(
            "var value = InheritedValue;\nvar unmarked = new ScriptTests.Dependency.UnmarkedType();\n"
                + Script<ScriptTestGlobals>.EndMarker
        );
        Diagnostic error = (await script.DiagnosticsAsync()).ShouldHaveSingleItem();
        error.Code.ShouldBe("CS0234");
        error.Message.ShouldContain("Dependency");
    }

    [Fact]
    public async Task Imports_add_namespaces_to_the_implicit_ones()
    {
        using var script = await _fixture.OpenAsync(
            "var api = ApiType.Value;\nvar list = new List<int>();\nawait Task.Yield();\n"
                + Script<ScriptTestGlobals>.EndMarker,
            new CSharpScriptEnvironment<ScriptTestGlobals> { Imports = ["ScriptTests.Api"] }
        );
        (await script.DiagnosticsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_language_version_applies_to_the_script()
    {
        using var script = await _fixture.OpenAsync(
            "int[] numbers = [1, 2];\n" + Script<ScriptTestGlobals>.EndMarker,
            new CSharpScriptEnvironment<ScriptTestGlobals> { LanguageVersion = "11" }
        );
        (await script.DiagnosticsAsync()).ShouldContain(d => d.Code == "CS9058" || d.Message.Contains("collection"));
    }

    [Fact]
    public async Task An_unknown_language_version_is_an_error_on_the_script()
    {
        using var script = await _fixture.OpenAsync(
            Script<ScriptTestGlobals>.EndMarker,
            new CSharpScriptEnvironment<ScriptTestGlobals> { LanguageVersion = "nope" }
        );
        (await script.DiagnosticsAsync()).ShouldContain(d => d.Message.Contains("nope"));
    }

    [Fact]
    public void A_document_keeps_a_copy_of_the_environment()
    {
        var imports = new List<string> { "ScriptTests.Api" };
        using var document = _fixture.Server.CreateDocument(
            new CSharpScriptEnvironment<ScriptTestGlobals> { Imports = imports }
        );
        imports.Clear();
        document.Environment.Imports.ShouldHaveSingleItem().ShouldBe("ScriptTests.Api");
    }

    [Fact]
    public void Environments_compare_by_content()
    {
        var one = new CSharpScriptEnvironment<ScriptTestGlobals> { Imports = ["A"], References = ["B"] };
        var same = new CSharpScriptEnvironment<ScriptTestGlobals> { Imports = ["A"], References = ["B"] };
        one.ShouldBe(same);
        one.GetHashCode().ShouldBe(same.GetHashCode());
        one.ShouldNotBe(same with { References = null });
        one.ShouldNotBe(same with { Imports = [] });
    }
}
