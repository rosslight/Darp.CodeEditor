using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>
/// The globals assembly of the environment must have the identity of the assembly the host loads the globals type
/// from. It may be a reference assembly.
/// </summary>
public sealed class GlobalsIdentityTests
{
    private const string Code = """
        var answer = Answer + 1;
        Log(Describe(answer));
        Answer.Missing();
        """;

    private static readonly Lazy<Contracts> s_contracts = new(Contracts.Create);

    [Fact]
    public async Task A_reference_assembly_with_the_same_identity_compiles_like_the_implementation()
    {
        Contracts contracts = s_contracts.Value;
        ResolvedScriptEnvironment environment = Resolve(contracts.ReferencePath);
        Script<object> script = environment.CreateScript(
            Code,
            contracts.GlobalsType,
            environment.CreateScriptOptions()
        );

        using ScriptWorkspace workspace = ScriptWorkspace.Load(environment);
        ImmutableArray<Diagnostic> server = await ScriptDiagnostics.GetAsync(
            workspace.CreateDocument("reference").WithText(SourceText.From(Code)),
            TestContext.Current.CancellationToken
        );

        string[] host =
        [
            .. script
                .GetCompilation()
                .GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                .Select(d => $"{d.Id} {d.Location.SourceSpan}"),
        ];
        server.Select(d => $"{d.Id} {d.Location.SourceSpan}").ShouldBe(host);
        host.ShouldHaveSingleItem().ShouldStartWith("CS1061");
    }

    [Fact]
    public void A_globals_assembly_of_another_version_is_rejected()
    {
        Contracts contracts = s_contracts.Value;
        ResolvedScriptEnvironment environment = Resolve(contracts.OtherVersionPath);

        ArgumentException error = Should.Throw<ArgumentException>(() =>
            environment.CreateScript(Code, contracts.GlobalsType, environment.CreateScriptOptions())
        );

        error.Message.ShouldContain("Version=1.0.0.0");
        error.Message.ShouldContain("Version=2.0.0.0");
    }

    private static ResolvedScriptEnvironment Resolve(string globalsPath) =>
        ResolvedScriptEnvironment.Resolve(
            new CSharpScriptEnvironment
            {
                Globals = new CSharpScriptGlobals(globalsPath, "Fixture.Globals"),
                Imports = ["System"],
            }
        );

    /// <summary>
    /// A contract assembly "Fixture.Contract" compiled three times: version 1.0.0.0, loaded in this process; a
    /// reference assembly of it; and version 2.0.0.0.
    /// </summary>
    private sealed record Contracts(Type GlobalsType, string ReferencePath, string OtherVersionPath)
    {
        private const string Source = """
            [assembly: System.Reflection.AssemblyVersion("VERSION")]

            namespace Fixture
            {
                public sealed class Globals
                {
                    public int Answer => 42;

                    public string Describe(int value) => value.ToString();

                    public void Log(string message) { }
                }
            }
            """;

        public static Contracts Create()
        {
            string directory = Path.Combine(Path.GetTempPath(), "darp-codeeditor-tests", Guid.NewGuid().ToString("N"));
            string implementation = Emit(directory, "implementation", "1.0.0.0", metadataOnly: false);
            string reference = Emit(directory, "reference", "1.0.0.0", metadataOnly: true);
            string otherVersion = Emit(directory, "other-version", "2.0.0.0", metadataOnly: false);
            Type globals =
                Assembly.LoadFrom(implementation).GetType("Fixture.Globals")
                ?? throw new InvalidOperationException("The fixture has no globals type.");
            return new Contracts(globals, reference, otherVersion);
        }

        private static string Emit(string directory, string name, string version, bool metadataOnly)
        {
            string runtime = RuntimeEnvironment.GetRuntimeDirectory();
            CSharpCompilation compilation = CSharpCompilation.Create(
                "Fixture.Contract",
                [CSharpSyntaxTree.ParseText(Source.Replace("VERSION", version, StringComparison.Ordinal))],
                [
                    MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Private.CoreLib.dll")),
                    MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
                ],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            string path = Path.Combine(directory, name, "Fixture.Contract.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? directory);
            using FileStream stream = File.Create(path);
            EmitResult result = compilation.Emit(stream, options: new EmitOptions(metadataOnly: metadataOnly));
            if (!result.Success)
                throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
            return path;
        }
    }
}
