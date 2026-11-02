using System.Collections.Immutable;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.CSharp.Scripting;
using Darp.CodeEditor.CSharp.TestGlobals;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using Shouldly;

namespace Darp.CodeEditor.CSharp.Server.Tests;

/// <summary>
/// The server reports exactly the compiler diagnostics the host gets from <c>CSharpScript</c> when both are built from
/// the same <see cref="CSharpScriptEnvironment"/>.
/// </summary>
public sealed class CompilerParityTests
{
    private static readonly Dictionary<string, ParityCase> s_cases = new[]
    {
        new ParityCase(
            "sealed globals",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            var t = Device.ReadTemperature();
            Counter++;
            Log($"{t}");
            Device.Calibrate();
            """,
            ["CS1061"]
        ),
        new ParityCase(
            "overloaded methods",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            Log("text");
            Log("{0} {1}", 1, 2);
            Log(3);
            Log(1.5);
            """,
            ["CS1503"]
        ),
        new ParityCase(
            "generic methods",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            int n = Get<int>("count");
            string? s = Get<string>("name");
            Keep("text");
            Keep(5);
            """,
            ["CS8632", "CS0452"]
        ),
        new ParityCase(
            "inherited members",
            TestEnvironments.For<DerivedGlobals>(),
            typeof(DerivedGlobals),
            """
            Trace("start");
            int limit = Limit;
            string name = Name;
            int secret = Secret;
            var t = Device.ReadTemperature();
            """,
            ["CS0029", "CS0122"]
        ),
        new ParityCase(
            "shadowing globals",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            var Device = 5;
            int Counter = Device * 2;
            void Log(string message) => Console.WriteLine(message);
            Log(Counter.ToString());
            Device.ReadTemperature();
            """,
            ["CS1061"]
        ),
        new ParityCase(
            "async code",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            byte[] block = await Device.ReadAsync(register: 0x10, length: 4);
            int sample = await NextSampleAsync();
            int[] all = await Task.WhenAll(NextSampleAsync(), NextSampleAsync());
            await Task.Delay(1);
            async Task<int> TwiceAsync() => 2 * await NextSampleAsync();
            int wrong = await Device.ReadAsync(1, 2);
            """,
            ["CS0029"]
        ),
        new ParityCase(
            "declarations",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            enum Mode { Off, On }
            record Reading(double Value, Mode Mode);
            class Filter
            {
                private readonly List<double> _values = new();
                public double Add(double value) { _values.Add(value); return _values.Average(); }
            }
            static double ToFahrenheit(double celsius) => celsius * 9 / 5 + 32;
            var filter = new Filter();
            var reading = new Reading(filter.Add(Device.ReadTemperature()), Mode.On);
            int value = 1;
            int value = 2;
            T First<T>(IEnumerable<T> items) => items.First();
            """,
            ["CS0102"]
        ),
        new ParityCase(
            "return expressions",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            if (Counter > 10)
                return "too often";
            if (Counter < 0)
                return;
            if (Counter == 5)
                return Log("five");
            var t = Device.ReadTemperature();
            t * 2
            """,
            ["CS0029"]
        ),
        new ParityCase(
            "missing reference",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """var match = new System.Text.RegularExpressions.Regex("a+").IsMatch("aa");""",
            ["CS0234"]
        ),
        new ParityCase(
            "added reference",
            TestEnvironments.Device with
            {
                FrameworkReferences =
                [
                    .. TestEnvironments.DefaultFrameworkReferences,
                    "System.Text.RegularExpressions",
                ],
            },
            typeof(DeviceGlobals),
            """var match = new System.Text.RegularExpressions.Regex("a+").IsMatch("aa");""",
            []
        ),
        new ParityCase(
            "file reference without globals",
            new CSharpScriptEnvironment
            {
                References = [typeof(TestDevice).Assembly.Location],
                FrameworkReferences = ["System.Console"],
                Imports = ["System"],
            },
            null,
            """
            var device = new Darp.CodeEditor.CSharp.TestGlobals.TestDevice();
            Console.WriteLine(device.ReadTemperature());
            Device.ReadTemperature();
            """,
            ["CS0103"]
        ),
        new ParityCase(
            "missing import",
            TestEnvironments.Device with
            {
                Imports = ["System"],
            },
            typeof(DeviceGlobals),
            """
            var values = new[] { 1, 2, 3 }.Where(v => v > 1);
            var list = new List<int>();
            """,
            ["CS1061", "CS0246"]
        ),
        new ParityCase(
            "added import",
            TestEnvironments.Device with
            {
                Imports = [.. TestEnvironments.DefaultImports, "System.Text"],
            },
            typeof(DeviceGlobals),
            """
            var values = new[] { 1, 2, 3 }.Where(v => v > 1);
            var builder = new StringBuilder().Append(values.Count());
            """,
            []
        ),
        new ParityCase(
            "directives are not supported",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            #r "System.Xml.dll"
            #load "other.csx"
            Log("x");
            """,
            ["CS7099", "CS8099"]
        ),
        new ParityCase(
            "nullable directive",
            TestEnvironments.Device,
            typeof(DeviceGlobals),
            """
            #nullable enable
            string? name = Get<string>("name");
            Log(name);
            """,
            ["CS8604"]
        ),
        new ParityCase(
            "prototype sample",
            TestEnvironments.Sample,
            typeof(Darp.CodeEditor.Demo.ScriptGlobals),
            Darp.CodeEditor.Samples.SampleDocuments.CSharpScript,
            ["CS0029"]
        ),
        new ParityCase(
            "language version",
            TestEnvironments.Device with
            {
                LanguageVersion = "11",
            },
            typeof(DeviceGlobals),
            """
            int[] values = [1, 2, 3];
            Log(values.Length);
            """,
            ["CS9058"]
        ),
    }.ToDictionary(c => c.Name);

    public static TheoryData<string> CaseNames => [.. s_cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Server_reports_the_diagnostics_of_the_host_compilation(string name)
    {
        ParityCase parity = s_cases[name];
        ResolvedScriptEnvironment environment = ResolvedScriptEnvironment.Resolve(parity.Environment);
        Script<object> script = environment.CreateScript(
            parity.Code,
            parity.GlobalsType,
            environment.CreateScriptOptions()
        );
        ImmutableArray<Diagnostic> host =
        [
            .. script
                .GetCompilation()
                .GetDiagnostics(TestContext.Current.CancellationToken)
                .Where(d => d.Severity != DiagnosticSeverity.Hidden),
        ];

        using ScriptWorkspace workspace = ScriptWorkspace.Load(environment);
        Document document = workspace.CreateDocument(name).WithText(SourceText.From(parity.Code));
        ImmutableArray<Diagnostic> server = await ScriptDiagnostics.GetAsync(
            document,
            TestContext.Current.CancellationToken
        );

        Describe(server).ShouldBe(Describe(host));
        // The case covers what its name says.
        host.Select(d => d.Id).ShouldBe(parity.ExpectedIds, ignoreOrder: true);
        // The host can run the script exactly when the editor shows no errors.
        script
            .Compile(TestContext.Current.CancellationToken)
            .Any(d => d.Severity == DiagnosticSeverity.Error)
            .ShouldBe(server.Any(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task Host_and_server_compile_with_the_same_options_and_references()
    {
        ResolvedScriptEnvironment environment = ResolvedScriptEnvironment.Resolve(TestEnvironments.Device);
        Compilation host = environment
            .CreateScript("Log(\"x\");", typeof(DeviceGlobals), environment.CreateScriptOptions())
            .GetCompilation();

        using ScriptWorkspace workspace = ScriptWorkspace.Load(environment);
        Compilation server =
            await workspace
                .CreateDocument("options")
                .WithText(SourceText.From("Log(\"x\");"))
                .Project.GetCompilationAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("No compilation.");

        server.SyntaxTrees.Single().Options.ShouldBe(host.SyntaxTrees.Single().Options);
        var hostOptions = (CSharpCompilationOptions)host.Options;
        var serverOptions = (CSharpCompilationOptions)server.Options;
        serverOptions.ScriptClassName.ShouldBe(hostOptions.ScriptClassName);
        serverOptions.Usings.ShouldBe(hostOptions.Usings);
        serverOptions.NullableContextOptions.ShouldBe(hostOptions.NullableContextOptions);
        serverOptions.CheckOverflow.ShouldBe(hostOptions.CheckOverflow);
        serverOptions.AllowUnsafe.ShouldBe(hostOptions.AllowUnsafe);
        serverOptions.WarningLevel.ShouldBe(hostOptions.WarningLevel);
        serverOptions.OptimizationLevel.ShouldBe(hostOptions.OptimizationLevel);
        serverOptions.Platform.ShouldBe(hostOptions.Platform);
        serverOptions.OutputKind.ShouldBe(hostOptions.OutputKind);
        serverOptions.AssemblyIdentityComparer.ShouldBe(hostOptions.AssemblyIdentityComparer);
        serverOptions.MetadataReferenceResolver.ShouldBe(hostOptions.MetadataReferenceResolver);
        serverOptions.SourceReferenceResolver.ShouldBe(hostOptions.SourceReferenceResolver);
        ReferencedFiles(server).ShouldBe(ReferencedFiles(host));
        server.ScriptCompilationInfo?.GlobalsType?.FullName.ShouldBe(typeof(DeviceGlobals).FullName);
    }

    [Fact]
    public void Script_options_reject_a_nullable_context_that_CSharpScript_cannot_express()
    {
        ResolvedScriptEnvironment environment = ResolvedScriptEnvironment.Resolve(
            TestEnvironments.Device with
            {
                Nullable = CSharpScriptNullable.Enable,
            }
        );

        Should.Throw<NotSupportedException>(environment.CreateScriptOptions);
    }

    private static string[] Describe(IEnumerable<Diagnostic> diagnostics) =>
        [
            .. diagnostics
                .OrderBy(d => d.Location.SourceSpan.Start)
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => $"{d.Id} {d.Severity} {d.Location.SourceSpan} {d.GetMessage()}"),
        ];

    private static string[] ReferencedFiles(Compilation compilation) =>
        [
            .. compilation
                .References.OfType<PortableExecutableReference>()
                .Select(r => r.FilePath ?? "")
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

    private sealed record ParityCase(
        string Name,
        CSharpScriptEnvironment Environment,
        Type? GlobalsType,
        string Code,
        string[] ExpectedIds
    );
}
