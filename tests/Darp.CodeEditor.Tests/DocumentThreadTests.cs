using System.Diagnostics;

namespace Darp.CodeEditor.Tests;

/// <summary>A document belongs to the app's main UI thread: it changes and raises its events there; any thread reads it.</summary>
[Collection(UiFixture.Collection)]
public sealed class DocumentThreadTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>Every member that changes a document.</summary>
    public static TheoryData<string> Changes => [.. s_changes.Keys];

    private static readonly Dictionary<string, Action<CodeDocument>> s_changes = new()
    {
        ["Text"] = d => d.Text = "changed",
        ["Replace"] = d => d.Replace(new TextRange(0, 0), "x"),
        ["Undo"] = d => d.Undo(),
        ["Redo"] = d => d.Redo(),
        ["MarkUnmodified"] = d => d.MarkUnmodified(d.CurrentSnapshot),
        ["Indentation"] = d => d.Indentation = Indentation.Tabs(),
        ["FilePath"] = d => d.FilePath = "other.txt",
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public Task Changes_on_another_thread_throw(string change) =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "text");
            document.Replace(new TextRange(0, 0), "a");
            document.Undo();

            InvalidOperationException exception = await Task.Run(() =>
                Assert.ThrowsAny<InvalidOperationException>(() => s_changes[change](document))
            );

            Assert.Contains("main UI thread", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("text", document.Text);
        });

    [Fact]
    public Task Any_thread_reads_the_text() =>
        _ui.Run(async () =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "line 1\nline 2");

            (string text, TextPosition position) = await Task.Run(() =>
                (document.Text, document.CurrentSnapshot.GetPosition(9))
            );

            Assert.Equal("line 1\nline 2", text);
            Assert.Equal(new TextPosition(2, 3), position);
        });

    [Fact]
    public Task A_document_created_on_the_thread_pool_belongs_to_the_main_UI_thread() =>
        _ui.Run(async () =>
        {
            CodeDocument document = await Task.Run(() => CodeDocument.Create(CodeLanguage.PlainText, "a"));
            Thread? raisedOn = null;
            document.TextChanged += (_, _) => raisedOn = Thread.CurrentThread;

            document.Replace(new TextRange(1, 0), "b");

            Assert.Equal(Thread.CurrentThread, raisedOn);
            await Task.Run(() => Assert.ThrowsAny<InvalidOperationException>(() => document.Undo()));
        });

    [Fact]
    public async Task Nothing_touches_Avalonias_dispatcher_before_Avalonia_is_set_up()
    {
        // A process of its own: this one sets Avalonia up for the headless tests.
        var start = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet"
        )
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Darp.CodeEditor.Tests.BeforeSetup.dll"));
        using Process process =
            Process.Start(start) ?? throw new InvalidOperationException("The fixture did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, $"Exit code {process.ExitCode}\n{await output}\n{await errors}");
    }
}
