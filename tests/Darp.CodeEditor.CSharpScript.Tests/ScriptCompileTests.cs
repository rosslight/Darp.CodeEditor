using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

[Collection(ScriptServerFixture.Collection)]
public sealed class ScriptCompileTests(ScriptServerFixture fixture)
{
    private static readonly CSharpScriptEnvironment<ScriptTestGlobals> s_environment = new();

    private readonly ScriptServerFixture _fixture = fixture;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Runs a compiled script as the runner will: through its entry point, with its globals first.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Only runs on the JIT: the test skips otherwise."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "Only runs on the JIT: the test skips otherwise."
    )]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only runs on the JIT: the test skips otherwise.")]
    private static async Task<object?> RunAsync(CSharpScriptAssembly<ScriptTestGlobals> script)
    {
        // The test project's NativeAOT settings turn the dynamic code switch off in its JIT build too.
        Assert.SkipWhen(
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is null,
            "NativeAOT apps run scripts in the runner."
        );
        var context = new AssemblyLoadContext(script.Image.Name, isCollectible: true);
        Assembly assembly = context.LoadFromStream(new MemoryStream(script.Image.Assembly));
        MethodInfo entry = assembly
            .GetType(script.Image.EntryType, throwOnError: true)!
            .GetMethod(script.Image.EntryMethod)!;
        object?[] submission = [new ScriptTestGlobals(), null];
        return await (Task<object>)entry.Invoke(null, [submission])!;
    }

    private static void SkipWithoutServer() =>
        Assert.SkipUnless(ScriptServerFixture.HasServer, "This app has no C# script server next to it.");

    [Fact]
    public async Task A_compilation_keeps_its_text_through_edits_and_a_reopen_at_its_path()
    {
        SkipWithoutServer();
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csx");
        var document = _fixture.Server.CreateDocument(s_environment, "Add(1, 2)");
        document.FilePath = path;
        TextSnapshot compiled = document.CurrentSnapshot;

        Task<CSharpScriptCompilation<ScriptTestGlobals>> compiling = document.CompileAsync(Token);
        document.Text = "Add(10, 20)";
        document.Dispose();
        using var reopened = _fixture.Server.CreateDocument(s_environment, "Add(100, 200)");
        reopened.FilePath = path;
        CSharpScriptCompilation<ScriptTestGlobals> compilation = await compiling;

        compilation.Snapshot.ShouldBeSameAs(compiled);
        compilation.Succeeded.ShouldBeTrue();
        (await RunAsync(compilation.Script)).ShouldBe(3);
        CSharpScriptCompilation<ScriptTestGlobals> latest = await reopened.CompileAsync(Token);
        (await RunAsync(latest.Script.ShouldNotBeNull())).ShouldBe(300);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n// only a comment\n")]
    [InlineData("using System.Text;\n")]
    public async Task A_script_without_statements_gives_an_assembly_that_runs(string text)
    {
        SkipWithoutServer();
        CSharpScriptCompilation<ScriptTestGlobals> compilation = await _fixture.Server.CompileAsync(
            s_environment,
            text,
            Token
        );

        compilation.Diagnostics.ShouldBeEmpty();
        compilation.Succeeded.ShouldBeTrue();
        await RunAsync(compilation.Script);
    }

    [Fact]
    public async Task Each_compilation_has_its_own_identity()
    {
        SkipWithoutServer();
        var first = await _fixture.Server.CompileAsync(s_environment, "1", Token);
        var second = await _fixture.Server.CompileAsync(s_environment, "1", Token);

        first.Script.ShouldNotBeNull().Image.Name.ShouldNotBe(second.Script.ShouldNotBeNull().Image.Name);
    }

    [Fact]
    public async Task A_script_with_errors_has_diagnostics_in_its_text_and_no_assembly()
    {
        SkipWithoutServer();
        var compilation = await _fixture.Server.CompileAsync(s_environment, "var a = 1;\nmissing();", Token);

        compilation.Succeeded.ShouldBeFalse();
        compilation.Script.ShouldBeNull();
        Diagnostic error = compilation.Diagnostics.ShouldHaveSingleItem();
        error.Snapshot.ShouldBeSameAs(compilation.Snapshot);
        error.Start.ShouldBe(new TextPosition(2, 1));
    }

    [Fact]
    public async Task An_unmarked_reference_is_rejected()
    {
        SkipWithoutServer();
        await Should.ThrowAsync<ArgumentException>(() =>
            _fixture.Server.CompileAsync(
                new CSharpScriptEnvironment<ScriptTestGlobals> { References = ["Not.Marked"] },
                "1",
                Token
            )
        );
    }

    [Fact]
    public async Task A_compile_waits_for_a_server_that_restarts()
    {
        SkipWithoutServer();
        await using var server = new CSharpScriptServer();
        await server.StartAsync(Token);
        Process.GetProcessById(server.ProcessId.ShouldNotBeNull()).Kill();

        var compilation = await server.CompileAsync(s_environment, "Add(2, 3)", Token);

        (await RunAsync(compilation.Script.ShouldNotBeNull())).ShouldBe(5);
    }

    [Fact]
    public async Task A_compile_on_a_disposed_server_says_so()
    {
        SkipWithoutServer();
        var server = new CSharpScriptServer();
        await server.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() => server.CompileAsync(s_environment, "1", Token));
    }

    [Fact]
    public async Task Compiling_leaves_Avalonias_dispatcher_alone_before_Avalonia_is_set_up()
    {
        SkipWithoutServer();
        string fixture = Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.CSharpScript.Tests.BeforeSetup");
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? $"{fixture}.exe" : fixture);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process process =
            Process.Start(start) ?? throw new InvalidOperationException("The fixture did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        process.ExitCode.ShouldBe(0, $"{await output}\n{await errors}");
    }
}
