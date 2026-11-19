using Avalonia.Media;
using Darp.CodeEditor.Highlighting;

namespace Darp.CodeEditor.Tests;

/// <summary>The defaults of the highlights' colors, as VS Code computes them.</summary>
public sealed class HighlightColorsTests
{
    /// <summary>
    /// The default of <c>editor.selectionHighlightBackground</c>: VS Code's <c>lessProminent</c> of the selection on the
    /// background, by the values that VS Code's own color code computes.
    /// </summary>
    [Theory]
    [InlineData("#ADD6FF", "#FFFFFF", "#99DBEDFF")]
    [InlineData("#264F78", "#1E1E1E", "#991D3B5A")]
    [InlineData("#FFFF00", "#FFFFFF", "#99FFFF06")]
    [InlineData("#3A3D41", "#252526", "#99303235")]
    [InlineData("#E5EBF1", "#F3F3F3", "#99ECF1F5")]
    public void The_selection_highlight_is_the_selection_less_prominent(
        string selection,
        string background,
        string expected
    ) =>
        HighlightColors
            .LessProminent(Color.Parse(selection), Color.Parse(background), 0.3, 0.6)
            .ShouldBe(Color.Parse(expected));
}
