using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Completion;

/// <summary>A completion item's icon: the codicon of its kind, 16 by 16 pixels, in its color.</summary>
internal sealed class CompletionIcon : Control
{
    private readonly Geometry[] _shapes;
    private readonly IBrush _brush;

    /// <param name="kind">The item's kind, from 1 to 25.</param>
    /// <param name="brush">The icon's color.</param>
    public CompletionIcon(int kind, IBrush brush)
    {
        _shapes = Codicons.Shapes(kind);
        _brush = brush;
        Width = 16;
        Height = 16;
    }

    public override void Render(DrawingContext context)
    {
        foreach (Geometry shape in _shapes)
            context.DrawGeometry(_brush, null, shape);
    }
}

/// <summary>
/// The colors of completion icons in a color theme: its <c>symbolIcon.*</c> colors, as VS Code reads them, and VS Code's
/// defaults where the theme sets none. The defaults of the kinds without a color of their own are the theme's
/// <c>foreground</c>.
/// </summary>
internal sealed class SymbolIconColors
{
    /// <summary>VS Code's defaults that differ from <c>foreground</c>, dark and light.</summary>
    private static readonly Dictionary<string, (uint Dark, uint Light)> s_defaults = new()
    {
        ["symbolIcon.classForeground"] = (0xFFEE9D28, 0xFFD67E00),
        ["symbolIcon.enumeratorForeground"] = (0xFFEE9D28, 0xFFD67E00),
        ["symbolIcon.eventForeground"] = (0xFFEE9D28, 0xFFD67E00),
        ["symbolIcon.constructorForeground"] = (0xFFB180D7, 0xFF652D90),
        ["symbolIcon.functionForeground"] = (0xFFB180D7, 0xFF652D90),
        ["symbolIcon.methodForeground"] = (0xFFB180D7, 0xFF652D90),
        ["symbolIcon.enumeratorMemberForeground"] = (0xFF75BEFF, 0xFF007ACC),
        ["symbolIcon.fieldForeground"] = (0xFF75BEFF, 0xFF007ACC),
        ["symbolIcon.interfaceForeground"] = (0xFF75BEFF, 0xFF007ACC),
        ["symbolIcon.variableForeground"] = (0xFF75BEFF, 0xFF007ACC),
    };

    /// <summary>The icons' brushes by the protocol's kind, from 1 at index 0.</summary>
    private readonly IBrush[] _brushes;

    private SymbolIconColors(IBrush[] brushes) => _brushes = brushes;

    public static SymbolIconColors From(ColorThemeFile theme)
    {
        bool dark = TextAreaColors.IsDark(theme);
        Color foreground = theme.Colors.TryGetValue("foreground", out Color color)
            ? color
            : Color.FromUInt32(dark ? 0xFFCCCCCC : 0xFF616161);
        var brushes = new IBrush[25];
        for (int kind = 1; kind <= 25; kind++)
        {
            string key = Codicons.ColorKey(kind);
            brushes[kind - 1] = new ImmutableSolidColorBrush(
                theme.Colors.TryGetValue(key, out Color own) ? own
                : s_defaults.TryGetValue(key, out (uint Dark, uint Light) defaults)
                    ? Color.FromUInt32(dark ? defaults.Dark : defaults.Light)
                : foreground
            );
        }
        return new SymbolIconColors(brushes);
    }

    /// <summary>The color of the icon of <paramref name="kind"/>, from 1 to 25.</summary>
    public IBrush Of(int kind) => _brushes[kind - 1];
}
