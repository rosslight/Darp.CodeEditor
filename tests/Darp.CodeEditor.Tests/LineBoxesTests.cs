using Avalonia;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Darp.CodeEditor.Editing;
using LogicalDirection = AvaloniaEdit.Document.LogicalDirection;

namespace Darp.CodeEditor.Tests;

/// <summary>Where lines in view draw their characters, measured a row at a time, as squiggles find it.</summary>
[Collection(UiFixture.Collection)]
public sealed class LineBoxesTests(UiFixture ui)
{
    private readonly UiFixture _ui = ui;

    [Fact]
    public Task Each_character_is_where_its_line_draws_it_whatever_the_text() =>
        _ui.Run(() =>
        {
            string text = string.Join(
                "\n",
                "plain text,\twith tabs\t\tand more",
                "wide 日本語のテキスト, a pair 😀, an accent é and a control \u0001 character",
                "right to left: שלום עולם, and back",
                // Wrapped into rows, and from 3,000 characters on hidden behind "...".
                string.Concat(Enumerable.Repeat("ab cd ", 1_000))
            );
            CodeDocument document = CodeDocument.Create(CodeLanguage.PlainText, text);
            using var window = new EditorWindow(
                new CodeEditor { Document = document, WordWrap = true },
                width: 400,
                height: 4_000
            );
            window.Render();
            TextView textView = window.TextView;
            textView.VisualLines.Count.ShouldBe(4);
            var boxes = new LineBoxes();
            int compared = 0;

            foreach (VisualLine line in textView.VisualLines)
            {
                boxes.Show(textView, line);
                // Each character from a caret stop to the next, as the text layout bounds it.
                int column = 0;
                while (column < line.VisualLength)
                {
                    int next = line.GetNextCaretPosition(
                        column,
                        LogicalDirection.Forward,
                        CaretPositioningMode.Normal,
                        allowVirtualSpace: false
                    );
                    next = next < 0 ? line.VisualLength : next;
                    TextLine row = line.GetTextLine(column);
                    Rect expected = Bounds(textView, line, row, column, next - column);
                    string where = $"line {line.FirstDocumentLine.LineNumber}, column {column}";
                    var spans = new List<Rect>();
                    boxes.AddSpans(column, next, spans);
                    Rect span = spans.ShouldHaveSingleItem(where);
                    span.Left.ShouldBe(expected.Left, 0.01, where);
                    span.Right.ShouldBe(expected.Right, 0.01, where);
                    span.Top.ShouldBe(expected.Top, 0.01, where);
                    span.Bottom.ShouldBe(expected.Bottom, 0.01, where);
                    // A character over several columns, as a tab, has a box in each.
                    Rect box = boxes.CharacterAt(column);
                    (box.Left >= span.Left - 0.01 && box.Right <= span.Right + 0.01).ShouldBeTrue(where);
                    box.Top.ShouldBe(span.Top, 0.01, where);
                    compared++;
                    column = next;
                }

                // A line's end is where its last character ends.
                double end = line.GetTextLineVisualXPosition(line.TextLines[^1], line.VisualLength);
                boxes.CharacterAt(line.VisualLength).Left.ShouldBe(end - textView.ScrollOffset.X, 0.01);

                // Text over rows is a box on each, from its first character to its last.
                var rows = new List<Rect>();
                boxes.AddSpans(0, line.VisualLength, rows);
                rows.Count.ShouldBe(line.TextLines.Count);
            }

            textView.VisualLines[^1].TextLines.Count.ShouldBeGreaterThan(1);
            compared.ShouldBeGreaterThan(3_000);
        });

    /// <summary>
    /// The box that the text layout gives the characters from <paramref name="column"/> on: as wide as they are drawn,
    /// as high as their row, in the view's coordinates.
    /// </summary>
    private static Rect Bounds(TextView textView, VisualLine line, TextLine row, int column, int length)
    {
        Rect[] rectangles = [.. row.GetTextBounds(column, length).Select(b => b.Rectangle)];
        double left = rectangles.Min(r => r.Left);
        double right = rectangles.Max(r => r.Right);
        double top = line.GetTextLineVisualYPosition(row, VisualYPosition.LineTop);
        double bottom = line.GetTextLineVisualYPosition(row, VisualYPosition.LineBottom);
        return new Rect(left, top, right - left, bottom - top).Translate(-textView.ScrollOffset);
    }
}
