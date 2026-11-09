namespace Darp.CodeEditor.Tests;

/// <summary>
/// The documents of a language server: open in it from creation, their edits reproduced there in order, closed when
/// disposed, one per file.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class ServerDocumentTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    /// <summary>Scenarios of edits, by name: the initial text and the edits, each sent and checked on its own.</summary>
    public static TheoryData<string> Scenarios => [.. s_scenarios.Keys];

    private static readonly Dictionary<string, (string Text, Action<CodeDocument>[] Edits)> s_scenarios = new()
    {
        ["LF"] = (
            "one\ntwo\nthree",
            [
                d => d.Replace(new TextRange(4, 3), "2"),
                d => d.Replace(new TextRange(5, 0), "\nnew line"),
                d => d.Replace(new TextRange(3, 1), ""),
                d => d.Replace(new TextRange(d.CurrentSnapshot.Length, 0), "\n"),
            ]
        ),
        ["CRLF"] = (
            "one\r\ntwo\r\nthree",
            [
                // Between \r and \n.
                d => d.Replace(new TextRange(4, 0), "x"),
                d => d.Replace(new TextRange(9, 1), ""),
                d => d.Replace(new TextRange(3, 1), ""),
                d => d.Replace(new TextRange(6, 3), "\r\n"),
                // Up to between \r and \n.
                d => d.Replace(new TextRange(2, 5), "\r"),
            ]
        ),
        ["CR"] = (
            "one\rtwo\rthree",
            [
                // A \n after a \r makes one line break of two.
                d => d.Replace(new TextRange(4, 0), "\n"),
                d => d.Replace(new TextRange(9, 0), "2\r"),
                d => d.Replace(new TextRange(0, 5), ""),
            ]
        ),
        ["Surrogate pairs"] = (
            "a😀b\nc😀d",
            [
                d => d.Replace(new TextRange(3, 0), "😀"),
                d => d.Replace(new TextRange(8, 2), ""),
                d => d.Replace(new TextRange(1, 2), "é"),
                d => d.Replace(new TextRange(d.CurrentSnapshot.Length, 0), "\n𝄞"),
            ]
        ),
        ["Undo and redo"] = (
            "one\r\ntwo\rthree\nfour",
            [
                d => d.Replace(new TextRange(0, 3), "first"),
                d => d.Text = "all new",
                d => d.Replace(new TextRange(3, 0), "\r"),
                d => d.Undo(),
                d => d.Undo(),
                d => d.Undo(),
                d => d.Redo(),
                d => d.Redo(),
            ]
        ),
    };

    [Fact]
    public Task A_document_is_open_in_the_server_from_its_creation() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string path = Path.Combine(AppContext.BaseDirectory, "settings.yaml");

            using CodeDocument document = server.CreateDocument(CodeLanguage.Yaml, "key: value", path);
            using CodeDocument untitled = server.CreateDocument(CodeLanguage.Json, "{}");

            document.FilePath.ShouldBe(path);
            await fake.WaitForAsync("open ", 2);
            fake.LinesStartingWith("open ")[0].ShouldBe($"open {new Uri(path).AbsoluteUri} yaml 0");
            fake.LinesStartingWith("open ")[1].ShouldStartWith("open untitled:");
            fake.LinesStartingWith("open ")[1].ShouldEndWith(" json 0");
            await Wait.UntilAsync(() => document.Diagnostics is [{ Message: "key: value" }]);
        });

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task Edits_reach_the_server_as_incremental_changes_in_order(string scenario) =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            (string text, Action<CodeDocument>[] edits) = s_scenarios[scenario];
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            await Wait.UntilAsync(() => ServerText(document) == text);

            for (int i = 0; i < edits.Length; i++)
            {
                edits[i](document);
                await fake.WaitForAsync("change ", i + 1);
                await Wait.UntilAsync(() => ServerText(document) == document.Text);
            }

            fake.LinesStartingWith("change ").ShouldAllBe(line => line.EndsWith(" full 0"));
        });

    [Fact]
    public Task Edits_made_together_reach_the_server_in_order() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "a\r\nb");

            for (int i = 0; i < 200; i++)
                document.Replace(new TextRange(i % 3 == 0 ? 2 : document.CurrentSnapshot.Length, 0), $"{i}\r");

            await Wait.UntilAsync(() => ServerText(document) == document.Text);
        });

    [Fact]
    public Task A_server_that_wants_the_full_text_gets_it_with_every_change() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--full-sync");
            await using var server = new LanguageServer(fake.Options);
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one");
            await fake.WaitForAsync("open ");

            document.Replace(new TextRange(3, 0), " two");

            (await fake.WaitForAsync("change ")).ShouldEndWith(" ranged 0 full 1");
            await Wait.UntilAsync(() => ServerText(document) == "one two");
        });

    [Fact]
    public Task A_disposed_document_is_closed_in_the_server_and_keeps_its_text() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string path = Path.Combine(AppContext.BaseDirectory, "closed.json");
            CodeDocument document = server.CreateDocument(CodeLanguage.Json, "{}", path);
            await fake.WaitForAsync("open ");

            document.Dispose();

            await fake.WaitForAsync($"close {new Uri(path).AbsoluteUri}");
            document.Text.ShouldBe("{}");
        });

    [Fact]
    public Task Two_open_documents_of_a_server_cannot_have_one_file_path() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string path = Path.Combine(AppContext.BaseDirectory, "one.yaml");
            string otherPath = Path.Combine(AppContext.BaseDirectory, "other.yaml");
            CodeDocument first = server.CreateDocument(CodeLanguage.Yaml, "a: 1", path);
            using CodeDocument other = server.CreateDocument(CodeLanguage.Yaml, "b: 2", otherPath);
            await fake.WaitForAsync($"open {new Uri(path).AbsoluteUri}");

            Should
                .Throw<InvalidOperationException>(() => server.CreateDocument(CodeLanguage.Yaml, "c: 3", path))
                .Message.ShouldContain(path);
            Should.Throw<InvalidOperationException>(() => other.FilePath = path);
            other.FilePath.ShouldBe(otherPath);

            first.Dispose();
            using CodeDocument second = server.CreateDocument(CodeLanguage.Yaml, "c: 3", path);
            await fake.WaitForAsync($"open {new Uri(path).AbsoluteUri}", 2);
            fake.Lines.ShouldContain($"close {new Uri(path).AbsoluteUri}");
        });

    [Fact]
    public Task A_new_file_path_moves_the_document_in_the_server() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer();
            await using var server = new LanguageServer(fake.Options);
            string path = Path.Combine(AppContext.BaseDirectory, "before.yaml");
            string newPath = Path.Combine(AppContext.BaseDirectory, "after.yaml");
            using CodeDocument document = server.CreateDocument(CodeLanguage.Yaml, "a: 1", path);
            await fake.WaitForAsync("open ");
            document.Replace(new TextRange(3, 1), "2");

            document.FilePath = newPath;

            await fake.WaitForAsync($"close {new Uri(path).AbsoluteUri}");
            await fake.WaitForAsync($"open {new Uri(newPath).AbsoluteUri} yaml 1");
            await Wait.UntilAsync(() => ServerText(document) == "a: 2");
        });

    /// <summary>The fake server's text of the document, from its echo diagnostic, once it is about the current text.</summary>
    private static string? ServerText(CodeDocument document) =>
        document.Diagnostics is [var echo] && echo.Snapshot.Version == document.CurrentSnapshot.Version
            ? echo.Message
            : null;
}
