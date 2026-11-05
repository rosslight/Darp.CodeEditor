using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// The colors of the text area besides the tokens, from a VS Code theme. Colors the theme leaves out take VS Code's
/// defaults for dark or light themes, as VS Code shows them.
/// </summary>
internal sealed record TextAreaColors(
    IBrush Background,
    IBrush Foreground,
    IBrush Selection,
    IBrush Caret,
    IBrush LineNumbers,
    IBrush CurrentLineBackground,
    IPen CurrentLineBorder
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
            )
        );

        ImmutableSolidColorBrush? Brush(string key) =>
            colors.TryGetValue(key, out Color color) ? new ImmutableSolidColorBrush(color) : null;
    }

    private static ImmutableSolidColorBrush Solid(uint argb) => new(Color.FromUInt32(argb));

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;
}
