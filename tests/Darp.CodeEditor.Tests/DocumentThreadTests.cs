using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>A document belongs to one UI thread: it changes there and raises its events there; any thread reads it.</summary>
public sealed class DocumentThreadTests
{
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

    [AvaloniaTheory]
    [MemberData(nameof(Changes))]
    public async Task Changes_on_another_thread_throw(string change)
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "text");
        document.Replace(new TextRange(0, 0), "a");
        document.Undo();

        InvalidOperationException exception = await Task.Run(() =>
            Should.Throw<InvalidOperationException>(() => s_changes[change](document))
        );

        exception.Message.ShouldContain("belongs to the UI thread");
        document.Text.ShouldBe("text");
    }

    [AvaloniaFact]
    public async Task Any_thread_reads_the_text()
    {
        CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "line 1\nline 2");

        (string text, TextPosition position) = await Task.Run(() =>
            (document.Text, document.CurrentSnapshot.GetPosition(9))
        );

        text.ShouldBe("line 1\nline 2");
        position.ShouldBe(new TextPosition(2, 3));
    }

    [AvaloniaFact]
    public async Task A_document_created_on_the_thread_pool_belongs_to_the_main_UI_thread()
    {
        CodeDocument document = await Task.Run(() => CodeDocument.Create(CodeLanguage.PlainText, "a"));
        Thread? raisedOn = null;
        document.TextChanged += (_, _) => raisedOn = Thread.CurrentThread;

        document.Replace(new TextRange(1, 0), "b");

        raisedOn.ShouldBe(Thread.CurrentThread);
        await Task.Run(() => Should.Throw<InvalidOperationException>(() => document.Undo()));
    }

    [AvaloniaFact]
    public async Task A_document_created_on_a_second_UI_thread_belongs_to_that_thread()
    {
        using var stop = new CancellationTokenSource();
        var created = new TaskCompletionSource<(Dispatcher, CodeDocument)>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var secondThread = new Thread(() =>
        {
            // Avalonia 12 gives any thread a dispatcher of its own, with a main loop.
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            created.SetResult((dispatcher, CodeDocument.Create(CodeLanguage.PlainText, "a")));
            dispatcher.MainLoop(stop.Token);
        })
        {
            IsBackground = true,
            Name = "Second UI thread",
        };
        secondThread.Start();
        (Dispatcher second, CodeDocument document) = await created.Task;
        Thread? raisedOn = null;
        document.TextChanged += (_, _) => raisedOn = Thread.CurrentThread;

        Should
            .Throw<InvalidOperationException>(() => document.Replace(new TextRange(0, 0), "x"))
            .Message.ShouldContain("'Second UI thread'");
        await second.InvokeAsync(() => document.Replace(new TextRange(1, 0), "b"));

        raisedOn.ShouldBe(secondThread);
        document.Text.ShouldBe("ab");
        stop.Cancel();
        secondThread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }

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

        process.ExitCode.ShouldBe(0, $"{await output}\n{await errors}");
    }
}
