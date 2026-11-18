using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// The colors of the text area's highlights besides the search matches, from a VS Code theme: the range that Find in
/// Selection searches, the occurrences of the symbol at the caret, and those of the selection. Colors the theme leaves
/// out take VS Code's defaults.
/// </summary>
/// <param name="FindRange">The background of the lines that Find in Selection searches.</param>
/// <param name="FindRangeBorder">Their border, if the theme has one.</param>
/// <param name="WordHighlight">The background of an occurrence that reads the symbol.</param>
/// <param name="WordHighlightBorder">Its border, if the theme has one.</param>
/// <param name="WordHighlightStrong">The background of an occurrence that writes the symbol.</param>
/// <param name="WordHighlightStrongBorder">Its border, if the theme has one.</param>
/// <param name="WordHighlightText">The background of an occurrence of the word, without a language server.</param>
/// <param name="WordHighlightTextBorder">Its border, if the theme has one.</param>
/// <param name="SelectionHighlight">The background of the other occurrences of the selection.</param>
/// <param name="SelectionHighlightBorder">Their border, if the theme has one.</param>
internal sealed record HighlightColors(
    IBrush FindRange,
    IPen? FindRangeBorder,
    IBrush WordHighlight,
    IPen? WordHighlightBorder,
    IBrush WordHighlightStrong,
    IPen? WordHighlightStrongBorder,
    IBrush WordHighlightText,
    IPen? WordHighlightTextBorder,
    IBrush SelectionHighlight,
    IPen? SelectionHighlightBorder
)
{
    /// <summary>Reads the colors of <paramref name="theme"/>, dark or light as <paramref name="dark"/> says.</summary>
    public static HighlightColors From(ColorThemeFile theme, bool dark)
    {
        IReadOnlyDictionary<string, Color> colors = theme.Colors;
        Color wordHighlight = Get("editor.wordHighlightBackground") ?? Argb(dark ? 0xB8575757u : 0x40575757u);
        ImmutablePen? wordHighlightBorder = Pen("editor.wordHighlightBorder");
        return new HighlightColors(
            Brush(Get("editor.findRangeHighlightBackground") ?? Argb(dark ? 0x663A3D41u : 0x4DB4B4B4u)),
            Pen("editor.findRangeHighlightBorder"),
            Brush(wordHighlight),
            wordHighlightBorder,
            Brush(Get("editor.wordHighlightStrongBackground") ?? Argb(dark ? 0xB8004972u : 0x400E639Cu)),
            Pen("editor.wordHighlightStrongBorder"),
            Brush(Get("editor.wordHighlightTextBackground") ?? wordHighlight),
            Pen("editor.wordHighlightTextBorder") ?? wordHighlightBorder,
            Brush(Get("editor.selectionHighlightBackground") ?? DefaultSelectionHighlight()),
            Pen("editor.selectionHighlightBorder")
        );

        Color? Get(string key) => colors.TryGetValue(key, out Color color) ? color : null;

        ImmutablePen? Pen(string key) => Get(key) is { } color ? new ImmutablePen(Brush(color)) : null;

        // VS Code's lessProminent(editor.selectionBackground, editor.background, 0.3, 0.6).
        Color DefaultSelectionHighlight()
        {
            Color selection = Get("editor.selectionBackground") ?? Argb(dark ? 0xFF264F78u : 0xFFADD6FFu);
            Color background = Get("editor.background") ?? Argb(dark ? 0xFF1E1E1Eu : 0xFFFFFFFFu);
            return LessProminent(selection, background, 0.3, 0.6);
        }
    }

    /// <summary>
    /// A port of VS Code's <c>lessProminent</c> color transform: <paramref name="color"/> moved by
    /// <paramref name="factor"/> towards the luminance of <paramref name="background"/>, at
    /// <paramref name="transparency"/> of its opacity.
    /// </summary>
    internal static Color LessProminent(Color color, Color background, double factor, double transparency)
    {
        double luminance = RelativeLuminance(color);
        double backgroundLuminance = RelativeLuminance(background);
        Color moved =
            luminance < backgroundLuminance
                ? Lighten(color, factor * (backgroundLuminance - luminance) / backgroundLuminance)
            : luminance > backgroundLuminance ? Darken(color, factor * (luminance - backgroundLuminance) / luminance)
            : color;
        return Transparent(moved, transparency);
    }

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static Color Argb(uint argb) => Color.FromUInt32(argb);

    private static double RelativeLuminance(Color color)
    {
        double luminance = 0.2126 * Component(color.R) + 0.7152 * Component(color.G) + 0.0722 * Component(color.B);
        return Math.Round(luminance, 4, MidpointRounding.AwayFromZero);

        static double Component(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static Color Transparent(Color color, double factor) =>
        Color.FromArgb(
            ToByte(Math.Round(color.A / 255.0 * factor, 3, MidpointRounding.AwayFromZero) * 255),
            color.R,
            color.G,
            color.B
        );

    private static Color Lighten(Color color, double factor)
    {
        Hsl hsl = Hsl.Of(color);
        return (hsl with { L = hsl.L + hsl.L * factor }).ToColor(color.A);
    }

    private static Color Darken(Color color, double factor)
    {
        Hsl hsl = Hsl.Of(color);
        return (hsl with { L = hsl.L - hsl.L * factor }).ToColor(color.A);
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>A color's hue, saturation and luminosity, as VS Code's <c>HSLA</c> converts them.</summary>
    private readonly record struct Hsl(int H, double S, double L)
    {
        public static Hsl Of(Color color)
        {
            double r = color.R / 255.0;
            double g = color.G / 255.0;
            double b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double l = (min + max) / 2;
            double chroma = max - min;
            double h = 0;
            double s = 0;
            if (chroma > 0)
            {
                s = Math.Min(l <= 0.5 ? chroma / (2 * l) : chroma / (2 - 2 * l), 1);
                h =
                    max == r ? (g - b) / chroma + (g < b ? 6 : 0)
                    : max == g ? (b - r) / chroma + 2
                    : (r - g) / chroma + 4;
                h = Math.Round(h * 60, MidpointRounding.AwayFromZero);
            }
            return new Hsl((int)Math.Clamp(h, 0, 360), Round(s), Round(l));
        }

        public Color ToColor(byte alpha)
        {
            double h = H / 360.0;
            double s = Round(S);
            double l = Round(L);
            double r;
            double g;
            double b;
            if (s == 0)
                r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3);
            }
            return Color.FromArgb(alpha, ToByte(r * 255), ToByte(g * 255), ToByte(b * 255));
        }

        private static double Round(double value) =>
            Math.Round(Math.Clamp(value, 0, 1), 3, MidpointRounding.AwayFromZero);

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0)
                t += 1;
            if (t > 1)
                t -= 1;
            if (t < 1.0 / 6)
                return p + (q - p) * 6 * t;
            if (t < 1.0 / 2)
                return q;
            if (t < 2.0 / 3)
                return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
