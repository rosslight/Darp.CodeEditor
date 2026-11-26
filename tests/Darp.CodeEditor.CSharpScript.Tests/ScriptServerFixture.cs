using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Lsp;
using ScriptTests.Api;

namespace Darp.CodeEditor.CSharpScript.Tests;

/// <summary>One real server process, which the tests share: starting Roslyn takes seconds.</summary>
public sealed class ScriptServerFixture : IAsyncLifetime
{
    /// <summary>The name of the collection whose tests share the fixture.</summary>
    public const string Collection = "Script server";

    public CSharpScriptServer Server { get; } = new();

    /// <summary>
    /// Whether the app has a server next to it. A NativeAOT test app gets one once publishing deploys the helpers; until
    /// then it runs only the tests that need no server.
    /// </summary>
    internal static bool HasServer
    {
        get
        {
            (string fileName, string[] arguments) = ScriptAppFiles.ServerCommand();
            return File.Exists(arguments.FirstOrDefault() ?? fileName);
        }
    }

    public async ValueTask InitializeAsync()
    {
        if (HasServer)
            await Server.StartAsync();
    }

    public ValueTask DisposeAsync() => Server.DisposeAsync();

    /// <summary>A script open in the server, with the caret where its text has a <c>|</c>.</summary>
    internal Task<Script<ScriptTestGlobals>> OpenAsync(
        string markedText,
        CSharpScriptEnvironment<ScriptTestGlobals>? environment = null
    ) => Script<ScriptTestGlobals>.OpenAsync(Server, environment ?? new(), markedText);
}

/// <summary>The tests that share a real server.</summary>
[CollectionDefinition(ScriptServerFixture.Collection)]
public sealed class ScriptServerCollection : ICollectionFixture<ScriptServerFixture>;

/// <summary>A script document, asked what an editor asks at its caret.</summary>
internal sealed class Script<TGlobals> : IDisposable
    where TGlobals : class
{
    private Script(CSharpScriptDocument<TGlobals> document, int caret)
    {
        Document = document;
        Caret = caret;
    }

    /// <summary>An undefined name that a text ends with, for <see cref="DiagnosticsAsync"/>.</summary>
    public const string EndMarker = "__end";

    public CSharpScriptDocument<TGlobals> Document { get; }

    /// <summary>The offset of the <c>|</c> in the text.</summary>
    public int Caret { get; set; }

    public static async Task<Script<TGlobals>> OpenAsync(
        CSharpScriptServer server,
        CSharpScriptEnvironment<TGlobals> environment,
        string markedText
    )
    {
        Assert.SkipUnless(ScriptServerFixture.HasServer, "This app has no C# script server next to it.");
        int caret = Math.Max(0, markedText.IndexOf('|'));
        var script = new Script<TGlobals>(server.CreateDocument(environment, markedText.Replace("|", "")), caret);
        await Wait.UntilAsync(() => script.Document.Features is not null);
        return script;
    }

    /// <summary>
    /// The list at the caret, with the word before it as an editor passes it, asked for as <paramref name="trigger"/>
    /// says, or by the user.
    /// </summary>
    public async Task<CompletionList> CompleteAsync(CompletionTrigger? trigger = null)
    {
        TextSnapshot snapshot = Document.CurrentSnapshot;
        string text = snapshot.GetText();
        int start = Caret;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '@'))
            start--;
        CompletionList? list = await Document.CompletionAsync(
            snapshot,
            TextRange.FromBounds(start, Caret),
            trigger ?? new CompletionTrigger(CompletionTriggerKind.Invoked),
            TestContext.Current.CancellationToken
        );
        return list.ShouldNotBeNull();
    }

    /// <summary>The hover at <paramref name="offset"/>, or at the caret.</summary>
    public async Task<HoverInfo?> HoverAsync(int? offset = null)
    {
        TextSnapshot snapshot = Document.CurrentSnapshot;
        return await Document.HoverAsync(
            snapshot,
            snapshot.GetPosition(offset ?? Caret),
            TestContext.Current.CancellationToken
        );
    }

    /// <summary>The signature help at the caret, as a retrigger of <paramref name="showing"/> when given.</summary>
    public Task<SignatureHelpInfo?> SignatureHelpAsync(SignatureHelpInfo? showing = null) =>
        Document.SignatureHelpAsync(
            Document.CurrentSnapshot,
            Caret,
            new SignatureHelpTrigger(
                showing is null ? SignatureHelpTriggerKind.Invoked : SignatureHelpTriggerKind.ContentChange,
                null,
                showing
            ),
            TestContext.Current.CancellationToken
        );

    /// <summary>
    /// The diagnostics of the current text, besides the error of the <see cref="EndMarker"/> that the text ends with: a
    /// server publishes nothing to tell that a text is clean, so that error tells that it has analyzed this text.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync()
    {
        TextSnapshot snapshot = Document.CurrentSnapshot;
        await Wait.UntilAsync(() =>
            Document.Diagnostics.Any(d =>
                d.Snapshot == snapshot && d.Message.Contains(EndMarker, StringComparison.Ordinal)
            )
        );
        return [.. Document.Diagnostics.Where(d => !d.Message.Contains(EndMarker, StringComparison.Ordinal))];
    }

    public void Dispose() => Document.Dispose();
}

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> for 60 seconds at most: a cap, not a latency.</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    /// <summary>Awaits <paramref name="task"/> for 60 seconds at most: a cap, not a latency.</summary>
    public static Task ForAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

    /// <inheritdoc cref="ForAsync(Task)"/>
    public static Task<T> ForAsync<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
}
