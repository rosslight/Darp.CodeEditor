using System.Diagnostics;
using System.Threading.Channels;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Lsp.Hosting;
using Shouldly;

namespace Darp.CodeEditor.Tests.Lsp;

/// <summary>
/// Luau through luau-lsp: whole-document semantic tokens and the requests it sends its client. Skipped unless
/// <c>tests/Darp.CodeEditor.Lsp.Tests/fetch-luau-lsp.sh</c> ran.
/// </summary>
public sealed class LuauLspTests
{
    private const string Script = """
        local function greet(name: string): string
            return "Hello, " .. name
        end

        local s = string.
        print(greet("you"))

        """;

    [Fact]
    public async Task Completion_hover_semantic_colors_and_diagnostics_work_through_luau_lsp()
    {
        string executable =
            FindLuauLsp()
            ?? throw Xunit.Sdk.SkipException.ForSkip(
                "luau-lsp is missing; run tests/Darp.CodeEditor.Lsp.Tests/fetch-luau-lsp.sh."
            );
        DirectoryInfo root = Directory.CreateTempSubdirectory("luau-lsp-");
        var diagnostics = Channel.CreateUnbounded<DiagnosticsChangedEventArgs>();
        await using var service = new LspLanguageService(
            new LspLanguageServiceOptions(
                _ =>
                {
                    var start = new ProcessStartInfo(executable) { ArgumentList = { "lsp", "--stdio" } };
                    return LanguageServerTransport.StartProcessAsync(start);
                },
                "luau"
            )
            {
                RootUri = new Uri(root.FullName).AbsoluteUri,
                DocumentUri = id => new Uri(Path.Combine(root.FullName, id)).AbsoluteUri,
            }
        );
        service.DiagnosticsChanged += (_, e) => diagnostics.Writer.TryWrite(e);
        try
        {
            await service.PrewarmAsync(Wait.Timeout);
            service.OpenDocument(new DocumentSnapshot("main.luau", 1, Script));
            var document = new DocumentVersion("main.luau", 1);

            CompletionList? members = await service.GetCompletionsAsync(
                document,
                Script.IndexOf("string.", StringComparison.Ordinal) + "string.".Length,
                new CompletionTrigger(CompletionTriggerKind.TriggerCharacter, '.'),
                Wait.Timeout
            );
            // A typed word start goes out as Invoked.
            CompletionList? words = await service.GetCompletionsAsync(
                document,
                Script.IndexOf("greet(\"", StringComparison.Ordinal) + 3,
                new CompletionTrigger(CompletionTriggerKind.Insertion, 'g'),
                Wait.Timeout
            );
            QuickInfo? hover = await service.GetQuickInfoAsync(
                document,
                Script.IndexOf("greet(\"", StringComparison.Ordinal) + 1,
                Wait.Timeout
            );
            IReadOnlyList<ClassifiedRange> classified = await service.GetClassificationsAsync(
                document,
                new TextRange(0, Script.Length),
                Wait.Timeout
            );
            DiagnosticsChangedEventArgs published = await diagnostics.Reader.ReadAsync(Wait.Timeout);

            members.ShouldNotBeNull().Items.ShouldContain(i => i.DisplayText == "format");
            words.ShouldNotBeNull().Items.ShouldContain(i => i.DisplayText == "greet");
            hover.ShouldNotBeNull().Text.ShouldContain("greet");
            // luau-lsp classifies whole documents only.
            classified.ShouldContain(c => Script.Substring(c.Range.Start, c.Range.Length) == "greet");
            published.DocumentId.ShouldBe("main.luau");
        }
        finally
        {
            await service.DisposeAsync();
            root.Delete(recursive: true);
        }
    }

    private static string? FindLuauLsp()
    {
        for (
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            string candidate = Path.Combine(directory.FullName, ".tools", "luau-lsp", "luau-lsp");
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
