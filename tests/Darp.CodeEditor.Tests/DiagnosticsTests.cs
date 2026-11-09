using Avalonia.Threading;

namespace Darp.CodeEditor.Tests;

/// <summary>A server's diagnostics: about the text it analyzed, set on the UI thread, clamped to the text.</summary>
[Collection(UiFixture.Collection)]
public sealed class DiagnosticsTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Diagnostics_carry_the_snapshot_the_server_analyzed_and_map_to_the_current_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "first line\r\nsecond line");
            TextSnapshot opened = document.CurrentSnapshot;

            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            Diagnostic diagnostic = document.Diagnostics[0];
            diagnostic.Snapshot.ShouldBeSameAs(opened);
            diagnostic.Range.ShouldBe(new TextRange(0, opened.Length));
            diagnostic.Start.ShouldBe(new TextPosition(1, 1));
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.Code.ShouldBe("echo");

            document.Replace(new TextRange(0, 0), "new ");

            document.MapToCurrent(diagnostic.Snapshot, diagnostic.Range).ShouldBe(new TextRange(4, opened.Length));
            await Wait.UntilAsync(() => document.Diagnostics[0].Snapshot == document.CurrentSnapshot);
            document.Diagnostics[0].Message.ShouldBe(document.Text);
        });

    [Fact]
    public Task Diagnostics_without_a_version_are_about_the_text_last_written() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--unversioned");
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one");
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            document.Replace(new TextRange(3, 0), " two");

            await Wait.UntilAsync(() => document.Diagnostics[0].Snapshot == document.CurrentSnapshot);
            document.Diagnostics[0].Message.ShouldBe("one two");
        });

    [Fact]
    public Task Diagnostics_without_a_version_are_not_about_a_text_still_being_written() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "written");
            TextSnapshot written = document.CurrentSnapshot;
            var analyzed = new List<TextSnapshot>();
            document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CodeDocument.Diagnostics) && document.Diagnostics is [var diagnostic])
                    analyzed.Add(diagnostic.Snapshot);
            };
            await Wait.UntilAsync(() => analyzed.Count == 1);
            // The server publishes without a version once it read the next message's header, and reads its body only
            // once the signal file exists.
            string signal = Path.Combine(Path.GetTempPath(), $"peek-{Guid.NewGuid():N}");
            using CodeDocument command = server.CreateDocument(FakeServer.Commands, $"peek {signal}");
            await fake.WaitForAsync("peeking");

            try
            {
                // More than a pipe holds: the client is still writing it when the diagnostics arrive.
                document.Text = new string('x', 4_000_000);

                await fake.WaitForAsync("peeked into a message");
                await Wait.UntilAsync(() => analyzed.Count == 2);
                analyzed[1].ShouldBeSameAs(written);
            }
            finally
            {
                await File.WriteAllTextAsync(signal, "", TestContext.Current.CancellationToken);
            }
            await Wait.UntilAsync(() => analyzed[^1] == document.CurrentSnapshot);
            File.Delete(signal);
        });

    [Fact]
    public Task Diagnostics_arrive_on_the_UI_thread_with_PropertyChanged() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            // Created on the thread pool, the document still belongs to the UI thread.
            CodeDocument document = await Task.Run(() => server.CreateDocument(CodeLanguage.PlainText, "text"));
            var raised = new List<(bool OnUiThread, int Count)>();
            document.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CodeDocument.Diagnostics))
                    raised.Add((Dispatcher.UIThread.CheckAccess(), document.Diagnostics.Count));
            };

            await Wait.UntilAsync(() => raised.Count > 0);
            document.Replace(new TextRange(0, 0), "more ");
            await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "more text" }]);
            document.Dispose();

            raised.ShouldAllBe(r => r.OnUiThread && r.Count == 1);
        });

    [Fact]
    public Task Positions_outside_the_text_are_clamped_to_it() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "ab\ncd");
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);

            using CodeDocument command = server.CreateDocument(FakeServer.Commands, "out-of-range");

            await Wait.UntilAsync(() => document.Diagnostics.Count == 4);
            document
                .Diagnostics.Select(d => (d.Message, d.Range))
                .ShouldBe([
                    ("before the start", new TextRange(0, 0)),
                    ("past the end of the first line", new TextRange(2, 1)),
                    ("past the last line", new TextRange(3, 2)),
                    ("end before start", new TextRange(2, 0)),
                ]);
        });
}
