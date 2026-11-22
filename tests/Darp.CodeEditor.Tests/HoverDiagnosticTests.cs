using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;
using Darp.CodeEditor.Popups;
using static Darp.CodeEditor.Tests.Hovers;

namespace Darp.CodeEditor.Tests;

/// <summary>A diagnostic in the hover: its severity's icon, its message, and its source and code, dimmed.</summary>
[Collection(UiFixture.Collection)]
public sealed class HoverDiagnosticTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task A_diagnostic_shows_its_icon_message_source_and_code_as_a_link() =>
        _ui.Run(async () =>
        {
            var fake = new FakeServer("--diagnostic-details");
            var time = new ManualTime();
            await using var server = new LanguageServer(fake.Options with { TimeProvider = time });
            const string text = "one two";
            using CodeDocument document = server.CreateDocument(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(new CodeEditor { Document = document });
            await Wait.UntilAsync(() => document.Diagnostics.Count == 1);
            Diagnostic diagnostic = document.Diagnostics[0];
            diagnostic.Source.ShouldBe("fake");
            diagnostic.CodeDescription.ShouldBe(new Uri("https://example.com/echo"));

            PopupFrame popup = await HoverAsync(window, 4, "two is a word in", time);

            popup.GetVisualDescendants().OfType<SeverityIcon>().ShouldHaveSingleItem();
            TextBlock line = popup
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .First(block => TextOf(block).StartsWith(text, StringComparison.Ordinal));
            TextOf(line).ShouldBe($"{text} fake(echo)");
            Run[] runs = [.. line.Inlines.ShouldNotBeNull().OfType<Run>()];
            Run code = runs.Single(run => run.Text == "echo");
            code.TextDecorations.ShouldBe(TextDecorations.Underline);
            code.Foreground.ShouldBeAssignableTo<ISolidColorBrush>().ShouldNotBeNull().Opacity.ShouldBe(0.6);
            runs.Single(run => run.Text == " fake(").Foreground.ShouldNotBeNull().Opacity.ShouldBe(0.6);
        });

    [Theory]
    [InlineData(DiagnosticSeverity.Error)]
    [InlineData(DiagnosticSeverity.Warning)]
    [InlineData(DiagnosticSeverity.Information)]
    [InlineData(DiagnosticSeverity.Hint)]
    public void Each_severity_shows_its_icon(DiagnosticSeverity severity)
    {
        var diagnostic = new Diagnostic(TextSnapshot.FromString("x"), new TextRange(0, 1), severity, null, "message");

        HoverBlock block = HoverBlocks.Of([diagnostic], new HoverBudget()).ShouldHaveSingleItem();

        block.ShouldBeOfType<HoverParagraph>().Severity.ShouldBe(severity);
    }

    [Fact]
    public void A_code_that_the_budget_cuts_is_no_link()
    {
        var diagnostic = new Diagnostic(
            TextSnapshot.FromString("x"),
            new TextRange(0, 1),
            DiagnosticSeverity.Error,
            "CS0001",
            new string('m', HoverBudget.MaxCharacters - 5)
        )
        {
            Source = "csharp",
            CodeDescription = new Uri("https://example.com/code"),
        };

        var paragraph = (HoverParagraph)HoverBlocks.Of([diagnostic], new HoverBudget()).Single();

        paragraph.Spans.ShouldAllBe(span => span.Link == null);
        string.Concat(paragraph.Spans.Select(span => span.Text)).ShouldEndWith("…");
    }

    [Fact]
    public void A_code_description_that_is_no_web_page_is_no_link()
    {
        var diagnostic = new Diagnostic(
            TextSnapshot.FromString("x"),
            new TextRange(0, 1),
            DiagnosticSeverity.Error,
            "CS0001",
            "message"
        )
        {
            CodeDescription = new Uri("file:///etc/passwd"),
        };

        var paragraph = (HoverParagraph)HoverBlocks.Of([diagnostic], new HoverBudget()).Single();

        paragraph.Spans.ShouldAllBe(span => span.Link == null);
        string.Concat(paragraph.Spans.Select(span => span.Text)).ShouldBe("message (CS0001)");
    }
}
