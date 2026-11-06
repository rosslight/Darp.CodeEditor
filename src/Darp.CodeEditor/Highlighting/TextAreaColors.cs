using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// The colors of the text area besides the tokens, from a VS Code theme. Colors the theme leaves out take VS Code's
/// defaults for dark or light themes, as VS Code shows them.
/// </summary>
/// <param name="Background">The text area's background.</param>
/// <param name="Foreground">Text without a token color of its own.</param>
/// <param name="Selection">The background of the selection.</param>
/// <param name="Caret">The caret.</param>
/// <param name="LineNumbers">The line numbers in the left margin.</param>
/// <param name="CurrentLineBackground">The background of the caret's line.</param>
/// <param name="CurrentLineBorder">The border around the caret's line.</param>
/// <param name="BracketMatchBackground">The box around a bracket next to the caret and its match.</param>
/// <param name="BracketMatchBorder">The border of that box.</param>
/// <param name="Whitespace">The dots and arrows of spaces and tabs inside the selection.</param>
/// <param name="FindMatch">The background of the current search match.</param>
/// <param name="FindMatchBorder">Its border, if the theme has one.</param>
/// <param name="FindMatchHighlight">The background of the other search matches.</param>
/// <param name="FindMatchHighlightBorder">Their border, if the theme has one.</param>
internal sealed record TextAreaColors(
    IBrush Background,
    IBrush Foreground,
    IBrush Selection,
    IBrush Caret,
    IBrush LineNumbers,
    IBrush CurrentLineBackground,
    IPen CurrentLineBorder,
    IBrush BracketMatchBackground,
    IPen BracketMatchBorder,
    IBrush Whitespace,
    IBrush FindMatch,
    IPen? FindMatchBorder,
    IBrush FindMatchHighlight,
    IPen? FindMatchHighlightBorder
)
{
    /// <summary>Reads the colors of <paramref name="theme"/>.</summary>
    public static TextAreaColors From(ColorThemeFile theme)
    {
        IReadOnlyDictionary<string, Color> colors = theme.Colors;
        // VS Code knows a theme's kind from its extension; the file has only its background to tell.
        bool dark = !colors.TryGetValue("editor.background", out Color background) || Luminance(background) < 0.5;
        ImmutableSolidColorBrush? currentLine = Brush("editor.lineHighlightBackground");
        return new TextAreaColors(
            Brush("editor.background") ?? Solid(dark ? 0xFF1E1E1E : 0xFFFFFFFF),
            Brush("editor.foreground") ?? Solid(dark ? 0xFFBBBBBB : 0xFF333333),
            Brush("editor.selectionBackground") ?? Solid(dark ? 0xFF264F78 : 0xFFADD6FF),
            Brush("editorCursor.foreground") ?? Solid(dark ? 0xFFAEAFAD : 0xFF000000),
            Brush("editorLineNumber.foreground") ?? Solid(dark ? 0xFF858585 : 0xFF237893),
            // Transparent rather than none: AvaloniaEdit draws its own colors otherwise.
            currentLine ?? Solid(0),
            // As in VS Code, the default border shows only around a line without a background of its own.
            new ImmutablePen(
                Brush("editor.lineHighlightBorder")
                    ?? (currentLine is null ? Solid(dark ? 0xFF282828 : 0xFFEEEEEE) : Solid(0))
            ),
            Brush("editorBracketMatch.background") ?? Solid(0x1A006400u),
            new ImmutablePen(Brush("editorBracketMatch.border") ?? Solid(dark ? 0xFF888888 : 0xFFB9B9B9)),
            Brush("editorWhitespace.foreground") ?? Solid(dark ? 0x29E3E4E2u : 0x33333333u),
            Brush("editor.findMatchBackground") ?? Solid(dark ? 0xFF515C6A : 0xFFA8AC94),
            Pen("editor.findMatchBorder"),
            Brush("editor.findMatchHighlightBackground") ?? Solid(0x55EA5C00u),
            Pen("editor.findMatchHighlightBorder")
        );

        ImmutableSolidColorBrush? Brush(string key) =>
            colors.TryGetValue(key, out Color color) ? new ImmutableSolidColorBrush(color) : null;

        ImmutablePen? Pen(string key) => Brush(key) is { } brush ? new ImmutablePen(brush) : null;
    }

    private static ImmutableSolidColorBrush Solid(uint argb) => new(Color.FromUInt32(argb));

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
}
