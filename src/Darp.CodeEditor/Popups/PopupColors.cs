using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Loading;
using TextMateSharp.Internal.Themes;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The colors of the editor's popups, from a VS Code color theme by VS Code's own keys: completion's list and details,
/// the hover, signature help and "No suggestions". Keys the theme leaves out take VS Code's defaults for its kind,
/// resolved as VS Code's color registry resolves them, so a theme made for VS Code looks as it does there.
/// </summary>
internal sealed record PopupColors
{
    private static readonly Lazy<PopupColors> s_default = new(() =>
        From(
            new ColorThemeFile(
                new ThemeRaw(),
                [],
                new Dictionary<string, Color>(),
                new Dictionary<string, SemanticTokenStyle>()
            ),
            new ImmutableSolidColorBrush(Color.FromUInt32(0xFF333333)),
            new ImmutableSolidColorBrush(Color.FromUInt32(0xFFADD6FF))
        )
    );

    /// <summary>VS Code's light defaults, while the editor's color theme loads.</summary>
    public static PopupColors Default => s_default.Value;

    /// <summary><c>editorWidget.background</c>.</summary>
    public required IBrush WidgetBackground { get; init; }

    /// <summary><c>editorWidget.foreground</c>.</summary>
    public required IBrush WidgetForeground { get; init; }

    /// <summary><c>widget.shadow</c> as a box shadow around popups; none in high contrast.</summary>
    public required BoxShadows Shadow { get; init; }

    /// <summary><c>editorSuggestWidget.background</c>.</summary>
    public required IBrush SuggestBackground { get; init; }

    /// <summary><c>editorSuggestWidget.border</c>.</summary>
    public required IBrush SuggestBorder { get; init; }

    /// <summary><c>editorSuggestWidget.foreground</c>.</summary>
    public required IBrush SuggestForeground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedBackground</c>.</summary>
    public required IBrush SuggestSelectedBackground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedForeground</c>; null keeps the row's foreground.</summary>
    public required IBrush? SuggestSelectedForeground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedIconForeground</c>; null keeps the icon's color.</summary>
    public required IBrush? SuggestSelectedIconForeground { get; init; }

    /// <summary><c>editorSuggestWidget.highlightForeground</c>: the characters that matched.</summary>
    public required IBrush SuggestHighlight { get; init; }

    /// <summary><c>editorSuggestWidget.focusHighlightForeground</c>: the characters that matched, in the selected row.</summary>
    public required IBrush SuggestFocusHighlight { get; init; }

    /// <summary><c>editorSuggestWidget.focusOutline</c>; null for none, as outside high contrast.</summary>
    public required IBrush? SuggestFocusOutline { get; init; }

    /// <summary><c>list.hoverBackground</c>: the row under the pointer.</summary>
    public required IBrush ListHoverBackground { get; init; }

    /// <summary><c>list.inactiveSelectionBackground</c>: the active signature among the overloads.</summary>
    public required IBrush ListInactiveSelectionBackground { get; init; }

    /// <summary><c>editorHoverWidget.background</c>.</summary>
    public required IBrush HoverBackground { get; init; }

    /// <summary><c>editorHoverWidget.foreground</c>.</summary>
    public required IBrush HoverForeground { get; init; }

    /// <summary><c>editorHoverWidget.border</c>, also the line between a hover's parts.</summary>
    public required IBrush HoverBorder { get; init; }

    /// <summary><c>editorHoverWidget.highlightForeground</c>: the active parameter of signature help.</summary>
    public required IBrush HoverHighlight { get; init; }

    /// <summary><c>descriptionForeground</c>: dimmed text, such as an item's detail.</summary>
    public required IBrush Description { get; init; }

    /// <summary><c>textLink.foreground</c>.</summary>
    public required IBrush Link { get; init; }

    /// <summary><c>textCodeBlock.background</c>: behind code spans in text.</summary>
    public required IBrush CodeSpanBackground { get; init; }

    /// <summary><c>scrollbarSlider.background</c>.</summary>
    public required IBrush Slider { get; init; }

    /// <summary><c>scrollbarSlider.hoverBackground</c>.</summary>
    public required IBrush SliderHover { get; init; }

    /// <summary><c>scrollbarSlider.activeBackground</c>.</summary>
    public required IBrush SliderActive { get; init; }

    /// <summary>The text area's foreground: code without a token color of its own.</summary>
    public required IBrush CodeForeground { get; init; }

    /// <summary>The text area's selection: selected text in popups.</summary>
    public required IBrush Selection { get; init; }

    /// <summary>Reads the popups' colors of <paramref name="theme"/>.</summary>
    public static PopupColors From(ColorThemeFile theme, IBrush codeForeground, IBrush selection)
    {
        IReadOnlyDictionary<string, Color> colors = theme.Colors;
        ThemeKind kind = KindOf(theme);
        bool hc = kind is ThemeKind.HighContrastDark or ThemeKind.HighContrastLight;

        Color? contrastBorder =
            Own("contrastBorder")
            ?? kind switch
            {
                ThemeKind.HighContrastDark => Hex(0xFF6FC3DF),
                ThemeKind.HighContrastLight => Hex(0xFF0F4A85),
                _ => null,
            };
        Color focusBorder =
            Own("focusBorder") ?? Pick(dark: 0xFF007FD4, light: 0xFF0090F1, hcDark: 0xFFF38518, hcLight: 0xFF006BBD);
        Color? activeContrastBorder = Own("contrastActiveBorder") ?? (hc ? focusBorder : null);
        Color foreground =
            Own("foreground") ?? Pick(dark: 0xFFCCCCCC, light: 0xFF616161, hcDark: 0xFFFFFFFF, hcLight: 0xFF292929);
        Color editorForeground =
            Own("editor.foreground")
            ?? (kind == ThemeKind.HighContrastLight ? foreground : Pick(0xFFBBBBBB, 0xFF333333, 0xFFFFFFFF, 0));
        Color widgetBackground = Own("editorWidget.background") ?? Pick(0xFF252526, 0xFFF3F3F3, 0xFF0C141F, 0xFFFFFFFF);
        Color widgetForeground = Own("editorWidget.foreground") ?? foreground;
        Color widgetBorder =
            Own("editorWidget.border")
            ?? (hc ? contrastBorder ?? Colors.Transparent : Transparent(widgetForeground, 0.2));
        Color? shadow =
            Own("widget.shadow")
            ?? kind switch
            {
                ThemeKind.Dark => Transparent(Colors.Black, 0.36),
                ThemeKind.Light => Transparent(Colors.Black, 0.16),
                _ => null,
            };

        Color? activeSelectionBackground =
            Own("list.activeSelectionBackground")
            ?? kind switch
            {
                ThemeKind.Dark => Hex(0xFF04395E),
                ThemeKind.Light => Hex(0xFF0060C0),
                ThemeKind.HighContrastLight => Transparent(Hex(0xFF0F4A85), 0.1),
                _ => null,
            };
        Color? activeSelectionForeground = Own("list.activeSelectionForeground") ?? (hc ? null : Colors.White);
        Color? activeSelectionIconForeground = Own("list.activeSelectionIconForeground");
        Color listHighlight =
            Own("list.highlightForeground") ?? (hc ? focusBorder : Pick(0xFF2AAAFF, 0xFF0066BF, 0, 0));
        Color listFocusHighlight =
            Own("list.focusHighlightForeground")
            ?? (
                kind == ThemeKind.Light && !colors.ContainsKey("list.activeSelectionBackground")
                    ? Hex(0xFFBBE7FF)
                    : listHighlight
            );

        Color suggestBackground = Own("editorSuggestWidget.background") ?? widgetBackground;
        Color suggestForeground = Own("editorSuggestWidget.foreground") ?? editorForeground;
        Color? quickInputFocusBackground = hc
            ? null
            : Own("quickInputList.focusBackground")
                ?? Own("quickInput.list.focusBackground")
                ?? activeSelectionBackground;
        Color? suggestSelectedForeground =
            Own("editorSuggestWidget.selectedForeground")
            ?? (hc ? suggestBackground : Own("quickInputList.focusForeground") ?? activeSelectionForeground);

        return new PopupColors
        {
            WidgetBackground = Brush(widgetBackground),
            WidgetForeground = Brush(widgetForeground),
            Shadow = shadow is { } s
                ? new BoxShadows(
                    new BoxShadow
                    {
                        Blur = 8,
                        Spread = 2,
                        Color = s,
                    }
                )
                : default,
            SuggestBackground = Brush(suggestBackground),
            SuggestBorder = Brush(Own("editorSuggestWidget.border") ?? widgetBorder),
            SuggestForeground = Brush(suggestForeground),
            SuggestSelectedBackground = Brush(
                Own("editorSuggestWidget.selectedBackground")
                    ?? (hc ? suggestForeground : quickInputFocusBackground ?? Colors.Transparent)
            ),
            SuggestSelectedForeground = OptionalBrush(suggestSelectedForeground),
            SuggestSelectedIconForeground = OptionalBrush(
                Own("editorSuggestWidget.selectedIconForeground")
                    ?? (
                        hc
                            ? suggestBackground
                            : Own("quickInputList.focusIconForeground") ?? activeSelectionIconForeground
                    )
            ),
            SuggestHighlight = Brush(Own("editorSuggestWidget.highlightForeground") ?? listHighlight),
            SuggestFocusHighlight = Brush(
                Own("editorSuggestWidget.focusHighlightForeground")
                    ?? (hc ? suggestSelectedForeground ?? listHighlight : listFocusHighlight)
            ),
            SuggestFocusOutline = OptionalBrush(Own("editorSuggestWidget.focusOutline") ?? activeContrastBorder),
            ListHoverBackground = Brush(
                Own("list.hoverBackground")
                    ?? kind switch
                    {
                        ThemeKind.Dark => Hex(0xFF2A2D2E),
                        ThemeKind.Light => Hex(0xFFF0F0F0),
                        ThemeKind.HighContrastDark => Transparent(Colors.White, 0.1),
                        _ => Transparent(Hex(0xFF0F4A85), 0.1),
                    }
            ),
            ListInactiveSelectionBackground = Brush(
                Own("list.inactiveSelectionBackground")
                    ?? kind switch
                    {
                        ThemeKind.Dark => Hex(0xFF37373D),
                        ThemeKind.Light => Hex(0xFFE4E6F1),
                        ThemeKind.HighContrastDark => Transparent(Colors.White, 0.1),
                        _ => Transparent(Hex(0xFF0F4A85), 0.1),
                    }
            ),
            HoverBackground = Brush(Own("editorHoverWidget.background") ?? widgetBackground),
            HoverForeground = Brush(Own("editorHoverWidget.foreground") ?? widgetForeground),
            HoverBorder = Brush(Own("editorHoverWidget.border") ?? widgetBorder),
            HoverHighlight = Brush(Own("editorHoverWidget.highlightForeground") ?? listHighlight),
            Description = Brush(
                Own("descriptionForeground")
                    ?? (kind == ThemeKind.Light ? Hex(0xFF717171) : Transparent(foreground, 0.7))
            ),
            Link = Brush(Own("textLink.foreground") ?? Pick(0xFF3794FF, 0xFF006AB1, 0xFF21A6FF, 0xFF0F4A85)),
            CodeSpanBackground = Brush(
                Own("textCodeBlock.background") ?? Pick(0x660A0A0A, 0x66DCDCDC, 0xFF000000, 0xFFF2F2F2)
            ),
            Slider = Brush(
                Own("scrollbarSlider.background")
                    ?? (
                        hc
                            ? Transparent(contrastBorder ?? Colors.Gray, kind == ThemeKind.HighContrastDark ? 0.6 : 0.4)
                            : Transparent(Hex(kind == ThemeKind.Dark ? 0xFF797979 : 0xFF646464), 0.4)
                    )
            ),
            SliderHover = Brush(
                Own("scrollbarSlider.hoverBackground")
                    ?? (hc ? Transparent(contrastBorder ?? Colors.Gray, 0.8) : Transparent(Hex(0xFF646464), 0.7))
            ),
            SliderActive = Brush(
                Own("scrollbarSlider.activeBackground")
                    ?? (
                        hc ? contrastBorder ?? Colors.Gray
                        : kind == ThemeKind.Dark ? Transparent(Hex(0xFFBFBFBF), 0.4)
                        : Transparent(Colors.Black, 0.6)
                    )
            ),
            CodeForeground = codeForeground,
            Selection = selection,
        };

        Color? Own(string key) => colors.TryGetValue(key, out Color color) ? color : null;

        Color Pick(uint dark, uint light, uint hcDark, uint hcLight) =>
            Hex(
                kind switch
                {
                    ThemeKind.Dark => dark,
                    ThemeKind.Light => light,
                    ThemeKind.HighContrastDark => hcDark,
                    _ => hcLight,
                }
            );
    }

    /// <summary>
    /// The kind of a theme, for VS Code's defaults: its <c>type</c> where the file names one, as theme generators
    /// write it, else dark or light by its background. VS Code knows it from the extension that contributes the theme.
    /// </summary>
    private static ThemeKind KindOf(ColorThemeFile theme) =>
        theme.Type?.ToLowerInvariant() switch
        {
            "hc" or "hcdark" or "hc-black" or "hc-dark" => ThemeKind.HighContrastDark,
            "hclight" or "hc-light" => ThemeKind.HighContrastLight,
            "dark" or "vs-dark" => ThemeKind.Dark,
            "light" or "vs" => ThemeKind.Light,
            _ => Highlighting.TextAreaColors.IsDark(theme) ? ThemeKind.Dark : ThemeKind.Light,
        };

    private static Color Hex(uint argb) => Color.FromUInt32(argb);

    private static Color Transparent(Color color, double factor) =>
        Color.FromArgb((byte)Math.Round(color.A * factor), color.R, color.G, color.B);

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);

    private static ImmutableSolidColorBrush? OptionalBrush(Color? color) => color is { } c ? new(c) : null;

    private enum ThemeKind
    {
        Dark,
        Light,
        HighContrastDark,
        HighContrastLight,
    }
}
