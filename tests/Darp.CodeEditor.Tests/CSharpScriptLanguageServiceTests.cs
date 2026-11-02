using Darp.CodeEditor.CSharp;
using Darp.CodeEditor.LanguageServices;
using Shouldly;

namespace Darp.CodeEditor.Tests;

public sealed class CSharpScriptLanguageServiceTests
{
    private static readonly Lazy<Task<CSharpScriptLanguageService>> s_service = new(() =>
        CSharpScriptLanguageService.CreateAsync(
            new CSharpScriptOptions(typeof(TestGlobals), [typeof(Enumerable).Assembly], ["System", "System.Linq"])
        )
    );

    [Fact]
    public async Task Completion_offers_members_of_the_globals_type()
    {
        CSharpScriptLanguageService service = await s_service.Value;
        const string text = "var t = Device.";

        CompletionList? list = await service.GetCompletionsAsync(
            new DocumentSnapshot(nameof(Completion_offers_members_of_the_globals_type), 1, text),
            text.Length,
            new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.'),
            TestContext.Current.CancellationToken
        );

        list.ShouldNotBeNull();
        list.ApplicableRange.ShouldBe(new TextRange(text.Length, 0));
        list.Items.ShouldContain(i => i.DisplayText == "ReadTemperature" && i.Kind == CompletionItemKind.Method);
    }

    [Fact]
    public async Task Completion_triggers_on_the_first_letter_of_a_word()
    {
        CSharpScriptLanguageService service = await s_service.Value;
        const string text = "var x = 1;\nD";

        CompletionList? list = await service.GetCompletionsAsync(
            new DocumentSnapshot(nameof(Completion_triggers_on_the_first_letter_of_a_word), 1, text),
            text.Length,
            new CompletionTrigger(CompletionTriggerKind.Insertion, 'D'),
            TestContext.Current.CancellationToken
        );

        list.ShouldNotBeNull();
        list.ApplicableRange.ShouldBe(new TextRange(text.Length - 1, 1));
        list.Items.ShouldContain(i => i.DisplayText == "Device");
    }

    [Fact]
    public async Task Completion_descriptions_load_for_items_of_the_last_list()
    {
        CSharpScriptLanguageService service = await s_service.Value;
        const string text = "Dev";
        string documentId = nameof(Completion_descriptions_load_for_items_of_the_last_list);

        CompletionList? list = await service.GetCompletionsAsync(
            new DocumentSnapshot(documentId, 1, text),
            text.Length,
            new CompletionTrigger(CompletionTriggerKind.Invoke),
            TestContext.Current.CancellationToken
        );
        list.ShouldNotBeNull();
        int index = list.Items.ToList().FindIndex(i => i.DisplayText == "Device");
        string? description = await service.GetCompletionDescriptionAsync(
            new CompletionItemReference(documentId, list.ResultId, index),
            TestContext.Current.CancellationToken
        );

        description.ShouldNotBeNull();
        description.ShouldContain("TestDevice");
    }

    [Fact]
    public async Task Diagnostics_are_computed_for_the_text_of_the_requested_version()
    {
        CSharpScriptLanguageService service = await s_service.Value;
        string documentId = nameof(Diagnostics_are_computed_for_the_text_of_the_requested_version);
        const string valid = "int x = 1;";
        const string invalid = "int x = \"one\";";

        // Version 2 arrives after version 3, as a late request would.
        IReadOnlyList<DiagnosticInfo> newest = await service.GetDiagnosticsAsync(
            new DocumentSnapshot(documentId, 3, valid),
            TestContext.Current.CancellationToken
        );
        IReadOnlyList<DiagnosticInfo> older = await service.GetDiagnosticsAsync(
            new DocumentSnapshot(documentId, 2, invalid),
            TestContext.Current.CancellationToken
        );
        IReadOnlyList<DiagnosticInfo> newestAgain = await service.GetDiagnosticsAsync(
            new DocumentSnapshot(documentId, 3, valid),
            TestContext.Current.CancellationToken
        );

        newest.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
        DiagnosticInfo error = older.ShouldHaveSingleItem();
        error.Id.ShouldBe("CS0029");
        error.Range.ShouldBe(new TextRange(invalid.IndexOf('"'), "\"one\"".Length));
        newestAgain.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Classifications_report_semantic_names_only()
    {
        CSharpScriptLanguageService service = await s_service.Value;
        const string text =
            "var list = new System.Collections.Generic.List<int>(); list.ForEach(item => list.Add(item)); // comment";

        IReadOnlyList<ClassifiedRange> ranges = await service.GetClassificationsAsync(
            new DocumentSnapshot(nameof(Classifications_report_semantic_names_only), 1, text),
            new TextRange(0, text.Length),
            TestContext.Current.CancellationToken
        );

        (string, ClassificationKind)[] named =
        [
            .. ranges.Select(r => (text.Substring(r.Range.Start, r.Range.Length), r.Kind)),
        ];
        named.ShouldContain(("List", ClassificationKind.Class));
        named.ShouldContain(("Add", ClassificationKind.Method));
        // Top-level script variables are fields of the script class.
        named.ShouldContain(("list", ClassificationKind.Field));
        named.ShouldContain(("item", ClassificationKind.Parameter));
        named.ShouldNotContain(n => n.Item1.Contains("comment"));
    }
}

public sealed class CSharpSignatureHelpTests
{
    [Fact]
    public async Task Signature_help_shows_overloads_while_typing_a_call_at_the_end_of_the_document()
    {
        using CSharpScriptLanguageService service = await CSharpScriptLanguageService.CreateAsync(
            new CSharpScriptOptions(null, [], ["System"]),
            TestContext.Current.CancellationToken
        );
        const string text = "var x = Math.Max(1, ";

        SignatureHelp? help = await service.GetSignatureHelpAsync(
            new DocumentSnapshot("signature", 1, text),
            text.Length,
            TestContext.Current.CancellationToken
        );

        help.ShouldNotBeNull();
        help.Signatures.ShouldContain(s => s.Label.Contains("Max(int val1, int val2)"));
        help.ActiveParameter.ShouldBe(1);
    }
}

public sealed class TestGlobals
{
    public TestDevice Device { get; } = new();
}

public sealed class TestDevice
{
    public double ReadTemperature() => 21;
}
