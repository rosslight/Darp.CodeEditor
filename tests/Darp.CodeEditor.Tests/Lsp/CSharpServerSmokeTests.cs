using System.Diagnostics;
using System.Threading.Channels;
using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.CSharp.Hosting;
using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>The C# script language server process, started next to the tests as an app starts it.</summary>
public sealed class CSharpServerSmokeTests
{
    private static readonly CSharpScriptEnvironment s_environment = new()
    {
        Globals = new CSharpScriptGlobals(
            typeof(Darp.CodeEditor.Demo.ScriptGlobals).Assembly.Location,
            typeof(Darp.CodeEditor.Demo.ScriptGlobals).FullName ?? ""
        ),
        FrameworkReferences = ["System.Linq"],
        Imports = ["System", "System.Linq"],
    };

    [Fact]
    public async Task The_server_completes_resolves_hovers_helps_with_signatures_classifies_and_reports_errors()
    {
        const string text = """
            var t = Device.ReadTemperature();
            Device.SetLed(true, 1);
            var b = new StringBui
            """;
        var diagnostics = Channel.CreateUnbounded<DiagnosticsChangedEventArgs>();
        LspLanguageService service = CSharpLanguageServer.CreateService(s_environment);
        service.DiagnosticsChanged += (_, e) => diagnostics.Writer.TryWrite(e);
        await service.PrewarmAsync(Wait.Timeout);
        int server = service.ServerProcessId.ShouldNotBeNull();
        service.OpenDocument(new DocumentSnapshot("script.csx", 1, text));
        var document = new DocumentVersion("script.csx", 1);
        try
        {
            CompletionList? members = await service.GetCompletionsAsync(
                document,
                text.IndexOf("Device.", StringComparison.Ordinal) + "Device.".Length,
                new CompletionTrigger(CompletionTriggerKind.Invoke),
                Wait.Timeout
            );
            CompletionList types = (
                await service.GetCompletionsAsync(
                    document,
                    text.Length,
                    new CompletionTrigger(CompletionTriggerKind.Invoke),
                    Wait.Timeout
                )
            ).ShouldNotBeNull();
            int builder = types.Items.ToList().FindIndex(i => i.DisplayText == "StringBuilder");
            CompletionItemDetails? resolved = await service.ResolveCompletionItemAsync(
                new CompletionItemReference("script.csx", types.ResultId, builder),
                Wait.Timeout
            );
            QuickInfo? hover = await service.GetQuickInfoAsync(
                document,
                text.IndexOf("ReadTemperature", StringComparison.Ordinal) + 2,
                Wait.Timeout
            );
            SignatureHelp? signature = await service.GetSignatureHelpAsync(
                document,
                text.IndexOf("true", StringComparison.Ordinal),
                new SignatureHelpTrigger(SignatureHelpTriggerKind.Invoke),
                Wait.Timeout
            );
            IReadOnlyList<ClassifiedRange> classified = await service.GetClassificationsAsync(
                document,
                new TextRange(0, text.Length),
                Wait.Timeout
            );
            DiagnosticsChangedEventArgs published = await diagnostics.Reader.ReadAsync(Wait.Timeout);

            members
                .ShouldNotBeNull()
                .Items.ShouldContain(i => i.DisplayText == "ReadTemperature" && i.Kind == CompletionItemKind.Method);
            // Types of namespaces that are not imported insert their name; resolve adds the using directive.
            builder.ShouldBeGreaterThanOrEqualTo(0);
            resolved
                .ShouldNotBeNull()
                .AdditionalEdits.ShouldNotBeNull()
                .ShouldContain(e => e.NewText.Contains("using System.Text;"));
            hover.ShouldNotBeNull().Content[0].Kind.ShouldBe(MarkupBlockKind.Code);
            hover.Text.ShouldContain("Reads the current temperature");
            SignatureInfo setLed = signature.ShouldNotBeNull().Signatures[signature.ActiveSignature];
            TextRange on = setLed.Parameters[0].LabelRange.ShouldNotBeNull();
            setLed.Label.Substring(on.Start, on.Length).ShouldBe("bool on");
            classified.ShouldContain(c =>
                c.Kind == ClassificationKind.Method
                && text.Substring(c.Range.Start, c.Range.Length) == "ReadTemperature"
            );
            // SetLed takes one argument, and StringBui is no type.
            published.Version.ShouldBe(1);
            published.Diagnostics.Select(d => d.Id).ShouldContain("CS1501");
        }
        finally
        {
            await service.DisposeAsync();
        }

        await WaitForExitAsync(server);
    }

    private static async Task WaitForExitAsync(int id)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            await process.WaitForExitAsync(Wait.Timeout);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
