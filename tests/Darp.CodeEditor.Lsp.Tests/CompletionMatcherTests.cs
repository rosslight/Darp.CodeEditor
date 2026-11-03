using Shouldly;

namespace Darp.CodeEditor.Lsp.Tests;

public sealed class CompletionMatcherTests
{
    [Theory]
    [InlineData("ReadTemperature", "Read", 90)]
    [InlineData("ReadTemperature", "read", 80)]
    [InlineData("ReadTemperature", "rt", 60)]
    [InlineData("ReadTemperature", "ReTe", 60)]
    [InlineData("set_led", "sl", 60)]
    [InlineData("ReadTemperature", "temp", 60)]
    [InlineData("ReadTemperature", "ature", 40)]
    [InlineData("ReadTemperature", "xyz", 0)]
    [InlineData("i", "i", 100)]
    public void Scores_prefix_over_word_parts_over_substring(string text, string query, int expected) =>
        CompletionMatcher.Score(text, query).ShouldBe(expected);
}
