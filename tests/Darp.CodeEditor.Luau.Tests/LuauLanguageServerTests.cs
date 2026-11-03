using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Luau.Tests;

/// <summary>Luau editing through the editor's LSP client and the real luau-lsp, with the test API as host API.</summary>
public sealed class LuauLanguageServerTests
{
    private const string Script = """
        log("Starting " .. device.name)
        local temperature = device:readTemperature()
        device:setLed(temperature > limits.maxTemperature)

        """;

    [Fact]
    public async Task Completion_lists_the_host_globals_and_no_Roblox_ones()
    {
        await using LuauSession luau = await LuauSession.StartAsync();
        const string text = Script + "d";
        DocumentVersion document = luau.Open("main.luau", text);

        CompletionList? words = await luau.Service.GetCompletionsAsync(
            document,
            text.Length,
            new CompletionTrigger(CompletionTriggerKind.Insertion, 'd'),
            LuauSession.Timeout
        );

        string[] labels = [.. words.ShouldNotBeNull().Items.Select(i => i.DisplayText)];
        labels.ShouldContain("device");
        labels.ShouldContain("limits");
        labels.ShouldContain("log");
        labels.ShouldContain("temperature");
        // The standard platform: no Roblox globals.
        labels.ShouldNotContain("game");
        labels.ShouldNotContain("workspace");
        labels.ShouldNotContain("Instance");
        // Names only: "(" brings signature help.
        words.Items.Single(i => i.DisplayText == "log").InsertText.ShouldBe("log");
    }

    [Fact]
    public async Task Member_completion_describes_host_methods_with_their_documentation()
    {
        await using LuauSession luau = await LuauSession.StartAsync();
        const string text = Script + "device:";
        DocumentVersion document = luau.Open("main.luau", text);

        CompletionList? members = await luau.Service.GetCompletionsAsync(
            document,
            text.Length,
            new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, ':'),
            LuauSession.Timeout
        );
        int index = members.ShouldNotBeNull().Items.ToList().FindIndex(i => i.DisplayText == "readTemperature");
        index.ShouldBeGreaterThanOrEqualTo(0);
        CompletionItemDetails? details = await luau.Service.ResolveCompletionItemAsync(
            new CompletionItemReference("main.luau", members.ResultId, index),
            LuauSession.Timeout
        );

        members.Items.Select(i => i.DisplayText).ShouldContain("setLed");
        string description = MarkupBlock.ToPlainText(details.ShouldNotBeNull().Description);
        description.ShouldContain("Reads the current temperature in degrees Celsius.");
    }

    [Fact]
    public async Task Hover_shows_the_signature_and_documentation_of_a_host_method()
    {
        await using LuauSession luau = await LuauSession.StartAsync();
        DocumentVersion document = luau.Open("main.luau", Script);

        QuickInfo? hover = await luau.Service.GetQuickInfoAsync(
            document,
            Script.IndexOf("setLed", StringComparison.Ordinal) + 2,
            LuauSession.Timeout
        );

        hover.ShouldNotBeNull();
        hover.Content[0].Kind.ShouldBe(MarkupBlockKind.Code);
        hover.Content[0].Text.ShouldContain("Device:setLed(on: boolean)");
        hover.Text.ShouldContain("Switches the status LED.");
    }

    [Fact]
    public async Task Signature_help_shows_the_parameters_of_a_host_method_with_their_documentation()
    {
        await using LuauSession luau = await LuauSession.StartAsync();
        // As typed: "(" closes itself and the caret is between the parentheses.
        const string text = Script + "device:setLed()";
        DocumentVersion document = luau.Open("main.luau", text);
        int inside = text.Length - 1;

        SignatureHelp? help = await luau.Service.GetSignatureHelpAsync(
            document,
            inside,
            new SignatureHelpTrigger(SignatureHelpTriggerKind.TriggerCharacter, '('),
            LuauSession.Timeout
        );

        SignatureInfo signature = help.ShouldNotBeNull().Signatures.ShouldHaveSingleItem();
        signature.Label.ShouldContain("setLed(on: boolean)");
        signature.Documentation.ShouldNotBeNull().ShouldContain("Switches the status LED.");
        ParameterInfo parameter = signature.Parameters.ShouldHaveSingleItem();
        parameter.Label.ShouldBe("on: boolean");
        parameter.Documentation.ShouldBe("True to switch the LED on.");
        (signature.ActiveParameter ?? help.ActiveParameter).ShouldBe(0);
    }

    [Fact]
    public async Task Semantic_colors_cover_the_whole_document()
    {
        await using LuauSession luau = await LuauSession.StartAsync();
        DocumentVersion document = luau.Open("main.luau", Script);

        IReadOnlyList<ClassifiedRange> classified = await luau.Service.GetClassificationsAsync(
            document,
            new TextRange(0, Script.Length),
            LuauSession.Timeout
        );

        ClassificationKind? KindOf(string word)
        {
            var range = new TextRange(Script.IndexOf(word, StringComparison.Ordinal), word.Length);
            return classified.Where(c => c.Range == range).Select(c => (ClassificationKind?)c.Kind).FirstOrDefault();
        }
        KindOf("log").ShouldBe(ClassificationKind.Method);
        KindOf("device").ShouldBe(ClassificationKind.Local);
        KindOf("name").ShouldBe(ClassificationKind.Property);
        KindOf("readTemperature").ShouldBe(ClassificationKind.Method);
        KindOf("setLed").ShouldBe(ClassificationKind.Method);
        KindOf("maxTemperature").ShouldBe(ClassificationKind.Property);
        // Locals keep their TextMate colors: luau-lsp classifies globals, members and types.
        KindOf("temperature").ShouldBeNull();
        // luau-lsp classifies whole documents only, and the service cuts out the requested range.
        IReadOnlyList<ClassifiedRange> firstLine = await luau.Service.GetClassificationsAsync(
            document,
            new TextRange(0, Script.IndexOf('\n')),
            LuauSession.Timeout
        );
        firstLine.ShouldAllBe(c => c.Range.Start < Script.IndexOf('\n'));
    }

    [Fact]
    public async Task Strict_mode_reports_type_errors_in_scripts_without_a_mode_comment()
    {
        const string text = """
            local count: number = "three"
            log(tostring(count))

            """;
        await using LuauSession strict = await LuauSession.StartAsync(LuauSession.Environment(LuauMode.Strict));
        await using LuauSession noCheck = await LuauSession.StartAsync(LuauSession.Environment(LuauMode.NoCheck));

        IReadOnlyList<DiagnosticInfo> strictDiagnostics = await strict.NextDiagnosticsAsync(
            strict.Open("main.luau", text)
        );
        IReadOnlyList<DiagnosticInfo> noCheckDiagnostics = await noCheck.NextDiagnosticsAsync(
            noCheck.Open("main.luau", text)
        );

        DiagnosticInfo error = strictDiagnostics.ShouldHaveSingleItem();
        error.Severity.ShouldBe(DiagnosticSeverity.Error);
        error.Message.ShouldContain("number");
        error.Range.Start.ShouldBe(0);
        error.Id.ShouldBe("1000");
        noCheckDiagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Diagnostics_reach_the_editor_from_a_workspace_whose_path_luau_lsp_escapes()
    {
        // luau-lsp writes the URIs of published diagnostics its own way: spaces, parentheses and drive letters too.
        DirectoryInfo parent = Directory.CreateTempSubdirectory("luau-");
        DirectoryInfo workspace = parent.CreateSubdirectory("My Scripts (1)");
        try
        {
            await using LuauSession luau = await LuauSession.StartAsync(
                options: new LuauLanguageServerOptions { WorkspaceDirectory = workspace.FullName }
            );

            IReadOnlyList<DiagnosticInfo> diagnostics = await luau.NextDiagnosticsAsync(
                luau.Open("main.luau", "local count: number = \"three\"\nlog(tostring(count))\n")
            );

            diagnostics.ShouldHaveSingleItem().Message.ShouldContain("number");
        }
        finally
        {
            parent.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_luau_lsp_without_execute_permissions_gets_them_before_it_starts()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no execute permissions.");
            return;
        }
        DirectoryInfo directory = Directory.CreateTempSubdirectory("luau-");
        try
        {
            string server = Path.Combine(directory.FullName, LuauLanguageServer.ServerFileName);
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "helpers", "luau-lsp", LuauLanguageServer.ServerFileName),
                server
            );
            File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            await using LuauSession luau = await LuauSession.StartAsync(
                options: new LuauLanguageServerOptions { ServerPath = server }
            );

            luau.Service.State.ShouldBe(LanguageServerState.Running);
            File.GetUnixFileMode(server).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
