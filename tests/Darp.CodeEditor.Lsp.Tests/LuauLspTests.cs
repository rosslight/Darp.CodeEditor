using Darp.CodeEditor.Lsp.Protocol;
using Darp.CodeEditor.Lsp.Testing;
using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

/// <summary>The client end of this library against luau-lsp; skipped unless <c>fetch-luau-lsp.sh</c> ran.</summary>
public sealed class LuauLspTests
{
    [Fact]
    public async Task Initialize_open_completion_hover_and_shutdown_work_against_luau_lsp()
    {
        string executable =
            LuauLsp.FindExecutable()
            ?? throw Xunit.Sdk.SkipException.ForSkip(
                "luau-lsp is missing; run tests/Darp.CodeEditor.Lsp.Tests/fetch-luau-lsp.sh."
            );
        DirectoryInfo root = Directory.CreateTempSubdirectory("luau-lsp-");
        string script = Path.Combine(root.FullName, "main.luau");
        await File.WriteAllTextAsync(script, LuauLsp.Script, Wire.Timeout);
        string uri = new Uri(script).AbsoluteUri;
        var document = new TextDocumentIdentifier(uri);
        await using var luau = new LuauLsp(executable);
        JsonRpcConnection rpc = luau.Connection;

        try
        {
            InitializeResult? initialized = await rpc.SendRequestAsync(
                LspMethods.Initialize,
                LuauLsp.InitializeParams(new Uri(root.FullName).AbsoluteUri),
                Wire.Timeout
            );
            await rpc.SendNotificationAsync(LspMethods.Initialized, new InitializedParams());
            await rpc.SendNotificationAsync(
                LspMethods.DidOpen,
                new DidOpenTextDocumentParams(new TextDocumentItem(uri, "luau", 1, LuauLsp.Script))
            );
            CompletionList? completion = await rpc.SendRequestAsync(
                LspMethods.Completion,
                new CompletionParams(
                    document,
                    LuauLsp.MemberAccess,
                    new CompletionContext(CompletionTriggerKind.TriggerCharacter, ".")
                ),
                Wire.Timeout
            );
            Hover? hover = await rpc.SendRequestAsync(
                LspMethods.Hover,
                new HoverParams(document, LuauLsp.Call),
                Wire.Timeout
            );
            await rpc.SendRequestAsync(LspMethods.Shutdown, default, Wire.Timeout);
            await rpc.SendNotificationAsync(LspMethods.Exit, default);

            initialized.ShouldNotBeNull().Capabilities.CompletionProvider.ShouldNotBeNull();
            initialized.Capabilities.HoverProvider.ShouldBe(true);
            completion
                .ShouldNotBeNull()
                .Items.ShouldContain(i => i.Label == "format" && i.Kind == CompletionItemKind.Function);
            hover.ShouldNotBeNull().Contents.Value.ShouldContain("greet");
            (await luau.WaitForExitAsync(Wire.Timeout)).ShouldBe(0);
        }
        catch (Exception exception) when (exception is not Xunit.Sdk.SkipException)
        {
            throw new InvalidOperationException($"luau-lsp check failed. Output:\n{luau.Output}", exception);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
