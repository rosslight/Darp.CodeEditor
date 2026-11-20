using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Darp.CodeEditor.Diagnostics;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Popups;
using Darp.CodeEditor.Search;
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>
/// Diagnostics in the hover, as VS Code shows them: their icon, message, source, code link and related places, the most
/// severe first, read a part at a time however many there are.
/// </summary>
[Collection(UiFixture.Collection)]
public sealed class HoverDiagnosticTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_diagnostic_shows_its_icon_message_source_code_link_and_related_places_as_in_VS_Code() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--diagnostic-details");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            string path = Path.Combine(AppContext.BaseDirectory, "details.txt");
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text, path);
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            Diagnostic diagnostic = document.Diagnostics[0];
            diagnostic.Source.ShouldBe("fake");
            diagnostic.CodeDescription.ShouldBe(new Uri("https://example.com/echo"));
            diagnostic
                .RelatedInformation.Select(r => (r.Start, r.Message))
                .ShouldBe([(new TextPosition(1, 2), "related here"), (new TextPosition(3, 4), "elsewhere")]);

            PopupFrame popup = await HoverAsync(window, 4, "two is a word in", time);

            // The diagnostic, then each related place below it.
            Control[] blocks = Blocks(popup);
            blocks[0].GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            TextBlock[] texts =
            [
                .. blocks.Take(3).SelectMany(block => block.GetVisualDescendants().Prepend(block)).OfType<TextBlock>(),
            ];
            texts
                .Select(TextOf)
                .ShouldBe([$"{text} fake(echo)", "details.txt(1, 2): related here", "other.txt(3, 4): elsewhere"]);
            texts.ShouldAllBe(block => block.FontFamily == editor.FontFamily);
            // The code, underlined in the link color, at the 60% of its source.
            Run code = texts[0].Inlines.ShouldNotBeNull().OfType<Run>().Single(run => run.Text == "echo");
            code.TextDecorations.ShouldBe(TextDecorations.Underline);
            code.Foreground.ShouldBeAssignableTo<ISolidColorBrush>().ShouldNotBeNull().Opacity.ShouldBe(0.6);
            texts[0]
                .Inlines.ShouldNotBeNull()
                .OfType<Run>()
                .Single(run => run.Text == " fake")
                .Foreground.ShouldNotBeNull()
                .Opacity.ShouldBe(0.6);

            // A place in the document is a link there, in the link color; one elsewhere, which the editor cannot show,
            // is not (Q121).
            Run[] places = [.. texts.Skip(1).Select(block => block.Inlines.ShouldNotBeNull().OfType<Run>().First())];
            places[0].Foreground.ShouldBe(PopupColors.Default.Link);
            places[1].IsSet(TextElement.ForegroundProperty).ShouldBeFalse();
            window.Click(TextPointAt(texts[2], 2), MouseButton.Left);
            Popup(window).ShouldNotBeNull();
            window.Click(TextPointAt(texts[1], 2), MouseButton.Left);
            Popup(window).ShouldBeNull();
            editor.Selection.ShouldBe(new TextSelection(1));
        });

    [Fact]
    public Task A_related_place_goes_no_further_once_the_app_shows_another_document_as_the_caret_moves() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--diagnostic-details");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            string path = Path.Combine(AppContext.BaseDirectory, "details.txt");
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, "one two", path);
            using CodeDocument other = server.CreateDocument(CodeLanguage.PlainText, "other text");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            // A selection, which going to the place clears first.
            editor.Selection = new TextSelection(4, 7);
            PopupFrame popup = await HoverAsync(window, 4, "two is a word in", time);
            TextBlock place = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(text => TextOf(text).StartsWith("details.txt", StringComparison.Ordinal));
            // An app that shows another document as the selection changes: the caret's move is not for that one.
            editor.PropertyChanged += (_, e) =>
            {
                if (e.Property == CodeEditor.SelectionProperty)
                    editor.Document = other;
            };

            window.Click(TextPointAt(place, 2), MouseButton.Left);
            window.Render();

            editor.Document.ShouldBeSameAs(other);
            Popup(window).ShouldBeNull();
            editor.Selection.ShouldBe(new TextSelection(0));
        });

    [Fact]
    public Task A_diagnostics_thousands_of_related_places_are_made_a_part_at_a_time() =>
        _ui.Run(() =>
        {
            const int count = 10_000;
            var diagnostic = new Diagnostic(
                TextSnapshot.FromString(""),
                default,
                DiagnosticSeverity.Error,
                null,
                "many places"
            )
            {
                RelatedInformation =
                [
                    .. Enumerable
                        .Range(0, count)
                        .Select(i => new DiagnosticRelatedInformation(new Uri("file:///a.py"), new(i + 1, 1), $"{i}")),
                ],
            };
            int made = 0;

            HoverContent content = HoverView.Create(
                HoverBlocks.Of(
                    [diagnostic],
                    (_, _) =>
                    {
                        made++;
                        return null;
                    }
                ),
                Look()
            );

            // As it opens, the places of the first part, and the one that did not fit it, besides the diagnostic.
            made.ShouldBe(content.Children.Count);
            content.AddNextPart();
            made.ShouldBe(content.Children.Count);
            while (!content.IsComplete)
                content.AddNextPart();
            made.ShouldBe(count);
        });

    [Fact]
    public Task The_most_severe_diagnostics_are_found_among_however_many_there_are() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "word");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            TextSnapshot snapshot = document.CurrentSnapshot;
            // 10,000 on one word, their severities taking turns from hints to errors.
            document.SetDiagnostics([
                .. Enumerable
                    .Range(0, 10_000)
                    .Select(i => new Diagnostic(
                        snapshot,
                        new TextRange(0, 4),
                        (DiagnosticSeverity)(i % 4),
                        null,
                        $"{i}"
                    )),
            ]);
            window.Layout();
            using var squiggles = new SquiggleLayer(EditorWindow.PartOf(editor).TextArea, document);

            IReadOnlyList<Diagnostic> shown = [.. squiggles.At(1).Take(120)];

            // First the errors, which come every fourth, in the server's order.
            shown.Select(d => d.Message).ShouldBe(Enumerable.Range(0, 120).Select(i => $"{4 * i + 3}"));
        });

    [Fact]
    public Task A_hover_over_thousands_of_diagnostics_reads_them_a_part_at_a_time_the_most_severe_first() =>
        _ui.Run(() =>
        {
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, "a word here");
            var editor = new CodeEditor { Document = document };
            using var window = new EditorWindow(editor);
            TextSnapshot snapshot = document.CurrentSnapshot;
            // 10,000 on one word, their severities taking turns from hints to errors.
            const int count = 10_000;
            document.SetDiagnostics([
                .. Enumerable
                    .Range(0, count)
                    .Select(i => new Diagnostic(
                        snapshot,
                        new TextRange(2, 4),
                        (DiagnosticSeverity)(i % 4),
                        null,
                        $"{i}"
                    )),
            ]);
            window.Layout();
            using var squiggles = new SquiggleLayer(EditorWindow.PartOf(editor).TextArea, document);
            var read = new ReadCount(squiggles.At(3));
            read.Count.ShouldBe(count);

            HoverContent content = HoverView.Create(HoverBlocks.Of(read), Look());

            // As it opens, the first part, and the one that did not fit it.
            content.IsComplete.ShouldBeFalse();
            read.Read.ShouldBe(content.Children.Count + 1);
            TextsOf(content).Take(2).ShouldBe(["3", "7"]);
            // Each next part reads its own.
            content.AddNextPart();
            read.Read.ShouldBe(content.Children.Count + 1);
            while (!content.IsComplete)
                content.AddNextPart();
            read.Read.ShouldBe(count);
            TextsOf(content).Skip(count / 4).First().ShouldBe("2");
            TextsOf(content).Last().ShouldBe($"{count - 4}");
        });

    [Fact]
    public Task A_diagnostic_without_a_message_shows_its_code_and_a_related_place_longer_than_a_part_in_parts() =>
        _ui.Run(() =>
        {
            string related = string.Join(' ', Enumerable.Repeat("word", 1_020));
            var diagnostic = new Diagnostic(TextSnapshot.FromString(""), default, DiagnosticSeverity.Warning, "W1", "")
            {
                RelatedInformation = [new DiagnosticRelatedInformation(new Uri("file:///a.py"), new(2, 4), related)],
            };
            IReadOnlyCollection<HoverBlock> blocks = HoverBlocks.Of([diagnostic]);
            blocks.SelectMany(HoverParts.Split).ShouldAllBe(piece => HoverParts.Fits(HoverParts.Size(piece)));
            var host = new Border();
            using var window = new EditorWindow(new CodeEditor(), host);

            HoverContent content = HoverView.Create(blocks, Look());
            host.Child = content;
            while (!content.IsComplete)
                content.AddNextPart();
            window.Render();

            content.GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            string[] texts = [.. TextsOf(content)];
            texts[0].ShouldBe(" (W1)");
            string.Concat(texts.Skip(1)).ShouldBe("a.py(2, 4): " + related);
        });

    /// <summary>Diagnostics that count how many of them were read.</summary>
    private sealed class ReadCount(IReadOnlyCollection<Diagnostic> diagnostics) : IReadOnlyCollection<Diagnostic>
    {
        private readonly IReadOnlyCollection<Diagnostic> _diagnostics = diagnostics;

        public int Read { get; private set; }

        public int Count => _diagnostics.Count;

        public IEnumerator<Diagnostic> GetEnumerator()
        {
            foreach (Diagnostic diagnostic in _diagnostics)
            {
                Read++;
                yield return diagnostic;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
