using EmmyLua.LanguageServer.Framework.Protocol.Message.Completion;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynCompletionTrigger = Microsoft.CodeAnalysis.Completion.CompletionTrigger;

namespace Darp.CodeEditor.CSharp.Server;

/// <summary>
/// Runs every feature once on a small script after the environment loaded, so the first real requests find Roslyn's
/// code compiled and its caches filled. Real requests do not wait for it.
/// </summary>
internal static class WarmUp
{
    private const string Text = """
        var numbers = new System.Collections.Generic.List<int> { 1, 2 };
        numbers.Add(3);
        System.Console.WriteLine(numbers.Count);
        numbers.
        """;

    public static async Task RunAsync(Task<ScriptWorkspace> workspaceTask, ClientFeatures client)
    {
        ScriptWorkspace workspace = await workspaceTask.ConfigureAwait(false);
        SourceText text = SourceText.From(Text);
        Document document = workspace.CreateDocument("<warm-up>").WithText(text);
        LineMap lines = LineMap.Create(text);
        var results = new CompletionResults();
        CancellationToken none = CancellationToken.None;

        await CompletionHandler
            .GetCompletionsAsync(
                document,
                lines,
                Text.Length,
                RoslynCompletionTrigger.CreateInsertionTrigger('.'),
                client,
                results,
                "",
                none
            )
            .ConfigureAwait(false);
        await CompletionHandler
            .GetCompletionsAsync(
                document,
                lines,
                Text.IndexOf("numbers.Add", StringComparison.Ordinal) + 1,
                RoslynCompletionTrigger.Invoke,
                client,
                results,
                "",
                none
            )
            .ConfigureAwait(false);
        if (results.TryGetLatest("", out CompletionResult? result))
        {
            await CompletionHandler
                .ResolveAsync(new CompletionItem { Label = "" }, result, result.Items[0], client, none)
                .ConfigureAwait(false);
        }
        await SemanticTokensHandler
            .GetTokensAsync(document, lines, new TextSpan(0, Text.Length), none)
            .ConfigureAwait(false);
        await ScriptDiagnostics.GetAsync(document, none).ConfigureAwait(false);
        await HoverHandler
            .GetHoverAsync(document, lines, Text.IndexOf("Add", StringComparison.Ordinal), client.HoverMarkdown, none)
            .ConfigureAwait(false);
        await SignatureHelpHandler
            .GetSignatureHelpAsync(document, Text.IndexOf("Add(", StringComparison.Ordinal) + 4, null, client, none)
            .ConfigureAwait(false);
    }
}
