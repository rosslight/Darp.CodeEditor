using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.Loading;
using TextMateSharp.Internal.Themes;

namespace Darp.CodeEditor.Popups;

/// <summary>
/// The colors of the editor's popups, from a VS Code color theme by VS Code's keys: completion's list and details, the
/// hover and signature help. Keys that the theme leaves out take VS Code's defaults for dark or light themes, resolved
/// as VS Code's color registry resolves them, so that a theme made for VS Code looks as it does there.
/// </summary>
internal sealed record PopupColors
{
    /// <summary>VS Code's light colors, while the editor's color theme loads.</summary>
    public static PopupColors Default { get; } = Light();

    /// <summary><c>widget.shadow</c>, as a shadow around popups.</summary>
    public required BoxShadows Shadow { get; init; }

    /// <summary><c>editorSuggestWidget.background</c>.</summary>
    public required IBrush SuggestBackground { get; init; }

    /// <summary><c>editorSuggestWidget.border</c>.</summary>
    public required IBrush SuggestBorder { get; init; }

    /// <summary><c>editorSuggestWidget.foreground</c>.</summary>
    public required IBrush SuggestForeground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedBackground</c>.</summary>
    public required IBrush SuggestSelectedBackground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedForeground</c>.</summary>
    public required IBrush SuggestSelectedForeground { get; init; }

    /// <summary><c>editorSuggestWidget.selectedIconForeground</c>; null keeps the icon's own color.</summary>
    public required IBrush? SuggestSelectedIconForeground { get; init; }

    /// <summary><c>editorSuggestWidget.highlightForeground</c>: the characters that matched.</summary>
    public required IBrush SuggestHighlight { get; init; }

    /// <summary><c>editorSuggestWidget.focusHighlightForeground</c>: the characters that matched, in the selected row.</summary>
    public required IBrush SuggestFocusHighlight { get; init; }

    /// <summary><c>list.hoverBackground</c>: the row under the pointer.</summary>
    public required IBrush ListHoverBackground { get; init; }

    /// <summary><c>editorHoverWidget.background</c>: behind every popup of code and docs, the code included.</summary>
    public required IBrush HoverBackground { get; init; }

    /// <summary><c>editorHoverWidget.foreground</c>: the docs' text, muted.</summary>
    public required IBrush HoverForeground { get; init; }

    /// <summary>
    /// <c>editorHoverWidget.border</c>, also the line between code and the docs below it and between a popup's parts.
    /// </summary>
    public required IBrush HoverBorder { get; init; }

    /// <summary><c>editorHoverWidget.highlightForeground</c>: signature help's active parameter.</summary>
    public required IBrush HoverHighlight { get; init; }

    /// <summary>The text area's foreground: code without a token color of its own.</summary>
    public required IBrush CodeForeground { get; init; }

    /// <summary><c>textCodeBlock.background</c>: behind code spans in text.</summary>
    public required IBrush CodeSpanBackground { get; init; }

    /// <summary><c>textLink.foreground</c>.</summary>
    public required IBrush Link { get; init; }

    /// <summary><c>problemsErrorIcon.foreground</c>: the icon of an error in a hover.</summary>
    public required IBrush ErrorIcon { get; init; }

    /// <summary><c>problemsWarningIcon.foreground</c>: the icon of a warning in a hover.</summary>
    public required IBrush WarningIcon { get; init; }

    /// <summary><c>problemsInfoIcon.foreground</c>: the icon of information or a hint in a hover.</summary>
    public required IBrush InfoIcon { get; init; }

    /// <summary><c>focusBorder</c>: the border of a popup that has the keyboard.</summary>
    public required IBrush FocusBorder { get; init; }

    /// <summary>The text area's selection: selected text in a hover.</summary>
    public required IBrush Selection { get; init; }

    /// <summary><c>scrollbarSlider.background</c>, <c>.hoverBackground</c> and <c>.activeBackground</c>.</summary>
    public required (IBrush Normal, IBrush Hover, IBrush Active) Slider { get; init; }

    /// <summary>Reads the popups' colors of <paramref name="theme"/>, whose text area has <paramref name="text"/>.</summary>
    public static PopupColors From(ColorThemeFile theme, TextAreaColors text)
    {
        IReadOnlyDictionary<string, Color> colors = theme.Colors;
        bool dark = TextAreaColors.IsDark(theme);
        Color foreground = Own("foreground") ?? Hex(dark ? 0xFFCCCCCC : 0xFF616161);
        Color widgetBackground = Own("editorWidget.background") ?? Hex(dark ? 0xFF252526 : 0xFFF3F3F3);
        Color widgetForeground = Own("editorWidget.foreground") ?? foreground;
        Color widgetBorder = Own("editorWidget.border") ?? Transparent(widgetForeground, 0.2);
        Color shadow = Own("widget.shadow") ?? Transparent(Colors.Black, dark ? 0.36 : 0.16);
        Color? selectedIcon =
            Own("editorSuggestWidget.selectedIconForeground")
            ?? Own("quickInputList.focusIconForeground")
            ?? Own("list.activeSelectionIconForeground");
        Color listHighlight = Own("list.highlightForeground") ?? Hex(dark ? 0xFF2AAAFF : 0xFF0066BF);
        Color listFocusHighlight =
            Own("quickInputList.focusHighlightForeground")
            ?? Own("list.focusHighlightForeground")
            ?? (dark || colors.ContainsKey("list.activeSelectionBackground") ? listHighlight : Hex(0xFFBBE7FF));
        return new PopupColors
        {
            Shadow = new BoxShadows(
                new BoxShadow
                {
                    Blur = 8,
                    Spread = 2,
                    Color = shadow,
                }
            ),
            SuggestBackground = Brush(Own("editorSuggestWidget.background") ?? widgetBackground),
            SuggestBorder = Brush(Own("editorSuggestWidget.border") ?? widgetBorder),
            SuggestForeground = Own("editorSuggestWidget.foreground") is { } suggest ? Brush(suggest) : text.Foreground,
            // The selected row's colors come from the list's selection by way of the quick input's.
            SuggestSelectedBackground = Brush(
                Own("editorSuggestWidget.selectedBackground")
                    ?? Own("quickInputList.focusBackground")
                    ?? Own("list.activeSelectionBackground")
                    ?? Hex(dark ? 0xFF04395E : 0xFF0060C0)
            ),
            SuggestSelectedForeground = Brush(
                Own("editorSuggestWidget.selectedForeground")
                    ?? Own("quickInputList.focusForeground")
                    ?? Own("list.activeSelectionForeground")
                    ?? Colors.White
            ),
            SuggestSelectedIconForeground = selectedIcon is { } icon ? Brush(icon) : null,
            SuggestHighlight = Brush(Own("editorSuggestWidget.highlightForeground") ?? listHighlight),
            SuggestFocusHighlight = Brush(Own("editorSuggestWidget.focusHighlightForeground") ?? listFocusHighlight),
            ListHoverBackground = Brush(Own("list.hoverBackground") ?? Hex(dark ? 0xFF2A2D2E : 0xFFF0F0F0)),
            HoverBackground = Brush(Own("editorHoverWidget.background") ?? widgetBackground),
            HoverForeground = Brush(Own("editorHoverWidget.foreground") ?? widgetForeground),
            HoverBorder = Brush(Own("editorHoverWidget.border") ?? widgetBorder),
            HoverHighlight = Brush(Own("editorHoverWidget.highlightForeground") ?? listHighlight),
            CodeForeground = text.Foreground,
            CodeSpanBackground = Brush(Own("textCodeBlock.background") ?? Hex(dark ? 0x660A0A0Au : 0x66DCDCDCu)),
            Link = Brush(Own("textLink.foreground") ?? Hex(dark ? 0xFF3794FF : 0xFF006AB1)),
            ErrorIcon = Icon("problemsErrorIcon.foreground", "editorError.foreground", 0xFFF14C4C, 0xFFE51400),
            WarningIcon = Icon("problemsWarningIcon.foreground", "editorWarning.foreground", 0xFFCCA700, 0xFFBF8803),
            InfoIcon = Icon("problemsInfoIcon.foreground", "editorInfo.foreground", 0xFF3794FF, 0xFF1A85FF),
            FocusBorder = Brush(Own("focusBorder") ?? Hex(dark ? 0xFF007FD4 : 0xFF0090F1)),
            Selection = text.Selection,
            Slider = (
                Brush(Own("scrollbarSlider.background") ?? Hex(dark ? 0x66797979u : 0x66646464u)),
                Brush(Own("scrollbarSlider.hoverBackground") ?? Hex(0xB3646464)),
                Brush(Own("scrollbarSlider.activeBackground") ?? Hex(dark ? 0x66BFBFBF : 0x99000000))
            ),
        };

        Color? Own(string key) => colors.TryGetValue(key, out Color color) ? color : null;

        // A problem's icon: its own color, else the editor's for its severity, else VS Code's default.
        IBrush Icon(string own, string editor, uint onDark, uint onLight) =>
            Brush(Own(own) ?? Own(editor) ?? Hex(dark ? onDark : onLight));
    }

    /// <summary>The colors of a light theme that sets nothing but its background.</summary>
    private static PopupColors Light()
    {
        var theme = new ColorThemeFile(
            new ThemeRaw(),
            [],
            new Dictionary<string, Color> { ["editor.background"] = Colors.White },
            new Dictionary<string, SemanticTokenStyle>()
        );
        return From(theme, TextAreaColors.From(theme));
    }

    private static Color Hex(uint argb) => Color.FromUInt32(argb);

    /// <summary><paramref name="color"/> with its alpha times <paramref name="factor"/>, as VS Code's <c>transparent</c>.</summary>
    private static Color Transparent(Color color, double factor) =>
        Color.FromArgb((byte)Math.Round(color.A * factor), color.R, color.G, color.B);

    private static ImmutableSolidColorBrush Brush(Color color) => new(color);
}
