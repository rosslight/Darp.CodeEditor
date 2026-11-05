using Avalonia.Headless.XUnit;
using Shouldly;

namespace Darp.CodeEditor.Tests;

/// <summary>Positions follow one rule everywhere: lines end at \r\n, \n or \r, and columns count UTF-16 units from 1.</summary>
public sealed class TextSnapshotTests
{
    [Theory]
    [InlineData("a\nb", 2, 2, 1)]
    [InlineData("a\r\nb", 3, 2, 1)]
    [InlineData("a\rb", 2, 2, 1)]
    [InlineData("a\n\rb", 3, 3, 1)] // \n then \r: two line breaks
    [InlineData("ab\n", 3, 2, 1)] // a final line break starts an empty last line
    [InlineData("\uD83D\uDE00x", 2, 1, 3)] // a surrogate pair takes two columns
    public void Every_line_break_ends_a_line(string text, int offset, int line, int column)
    {
        TextSnapshot snapshot = TextSnapshot.FromString(text);

        snapshot.GetPosition(offset).ShouldBe(new TextPosition(line, column));
        snapshot.GetOffset(new TextPosition(line, column)).ShouldBe(offset);
    }

    [Theory]
    [InlineData(-5, 1, 1)]
    [InlineData(int.MaxValue, 2, 4)]
    public void Offsets_outside_the_text_are_clamped(int offset, int line, int column)
    {
        TextSnapshot.FromString("ab\r\ncde").GetPosition(offset).ShouldBe(new TextPosition(line, column));
    }

    [Theory]
    [InlineData(1, 99, 2)] // to the end of the line, before its line break
    [InlineData(1, int.MaxValue, 2)]
    [InlineData(2, int.MaxValue, 7)]
    [InlineData(1, 0, 0)]
    [InlineData(1, int.MinValue, 0)]
    [InlineData(0, 2, 1)] // to the first line
    [InlineData(int.MinValue, 2, 1)]
    [InlineData(9, 1, 4)] // to the last line
    [InlineData(int.MaxValue, int.MaxValue, 7)]
    public void Positions_outside_the_text_are_clamped(int line, int column, int offset)
    {
        TextSnapshot.FromString("ab\r\ncde").GetOffset(new TextPosition(line, column)).ShouldBe(offset);
    }

    [Theory]
    [InlineData(1, 2, "bc")]
    [InlineData(-3, 5, "ab")]
    [InlineData(2, int.MaxValue, "cd")]
    [InlineData(9, 2, "")]
    [InlineData(3, -2, "")]
    public void Ranges_are_clamped_to_the_text(int start, int length, string expected)
    {
        TextSnapshot.FromString("abcd").GetText(new TextRange(start, length)).ShouldBe(expected);
    }

    [Fact]
    public void An_empty_text_has_one_line()
    {
        TextSnapshot snapshot = TextSnapshot.FromString("");

        snapshot.GetPosition(0).ShouldBe(new TextPosition(1, 1));
        snapshot.GetOffset(new TextPosition(3, 3)).ShouldBe(0);
    }

    [AvaloniaFact]
    public void Long_texts_follow_the_same_rule()
    {
        // A \r\n across 4096 characters, and line breaks of every kind at every distance.
        string text =
            new string('x', 4095)
            + "\r\n"
            + string.Concat(
                Enumerable
                    .Range(0, 3000)
                    .Select(i =>
                        new string('x', i % 7)
                        + (i % 3) switch
                        {
                            0 => "\r\n",
                            1 => "\n",
                            _ => "\r",
                        }
                    )
            );
        TextPosition[] expected = PositionsOf(text);

        foreach (
            TextSnapshot snapshot in (TextSnapshot[])
                [TextSnapshot.FromString(text), CodeDocument.Create(CodeLanguage.PlainText, text).CurrentSnapshot]
        )
        {
            for (int offset = 0; offset <= text.Length; offset++)
            {
                snapshot.GetPosition(offset).ShouldBe(expected[offset]);
                // Between \r and \n is no position of its own.
                bool insideLineBreak = offset > 0 && offset < text.Length && text[(offset - 1)..(offset + 1)] == "\r\n";
                if (!insideLineBreak)
                    snapshot.GetOffset(expected[offset]).ShouldBe(offset);
            }
        }
    }

    [AvaloniaFact]
    public async Task Threads_that_use_a_new_snapshot_at_once_see_the_same_lines()
    {
        string text = string.Concat(Enumerable.Range(0, 2000).Select(i => $"line {i}\r\n"));
        TextPosition[] expected = PositionsOf(text);

        for (int round = 0; round < 20; round++)
        {
            // Each round a snapshot without its line index, used by eight threads at once.
            TextSnapshot snapshot = CodeDocument.Create(CodeLanguage.PlainText, text).CurrentSnapshot;
            await Task.WhenAll(
                Enumerable
                    .Range(0, 8)
                    .Select(thread =>
                        Task.Run(() =>
                        {
                            int offset = Array.IndexOf(expected, new TextPosition(thread * 200 + 1, 3));
                            snapshot.GetPosition(offset).ShouldBe(expected[offset]);
                            snapshot.GetOffset(expected[offset]).ShouldBe(offset);
                        })
                    )
            );
        }
    }

    /// <summary>The position of every offset, counted character by character.</summary>
    private static TextPosition[] PositionsOf(string text)
    {
        var positions = new TextPosition[text.Length + 1];
        (int line, int column) = (1, 1);
        for (int i = 0; i <= text.Length; i++)
        {
            positions[i] = new TextPosition(line, column);
            if (
                i < text.Length
                && (text[i] == '\n' || text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
            )
                (line, column) = (line + 1, 1);
            else
                column++;
        }
        return positions;
    }
}
