using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.LanguageServices;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.Theming;

/// <summary>
/// The colors of one VS Code theme for everything the editor draws besides TextMate tokens: editor chrome, popups,
/// squiggles, markers and semantic colors. Immutable, so any thread may create one.
/// </summary>
internal sealed class EditorTheme
{
    private static readonly EditorTheme s_dark = new();

    private static readonly EditorTheme s_light = new()
    {
        IsDark = false,
        Background = Brush("#FFFFFF"),
        Foreground = Brush("#000000"),
        LineNumbers = Brush("#237893"),
        Selection = Brush("#ADD6FF"),
        CurrentLineBorder = Pen("#EEEEEE"),
        Caret = Brush("#000000"),
        WidgetBackground = Brush("#F3F3F3"),
        WidgetBorder = Brush("#C8C8C8"),
        WidgetForeground = Brush("#000000"),
        WidgetSecondaryForeground = Brush("#717171"),
        ListSelectionBackground = Brush("#0060C0"),
        ListSelectionForeground = Brush("#FFFFFF"),
        SymbolMethod = Brush("#652D90"),
        SymbolType = Brush("#D67E00"),
        SymbolKeyword = Brush("#616161"),
        SymbolVariable = Brush("#007ACC"),
        Error = Pen("#E51400"),
        Warning = Pen("#BF8803"),
        Info = Pen("#1A85FF"),
        BracketMatchBorder = Pen("#B9B9B9"),
    };

    /// <summary>VS Code's Dark+ colors.</summary>
    public static EditorTheme Default => s_dark;

    /// <summary>VS Code's default colors for dark or light themes, until a theme has loaded.</summary>
    public static EditorTheme Fallback(bool dark) => dark ? s_dark : s_light;

    public bool IsDark { get; private init; } = true;

    public IBrush Background { get; private init; } = Brush("#1E1E1E");
    public IBrush Foreground { get; private init; } = Brush("#D4D4D4");
    public IBrush LineNumbers { get; private init; } = Brush("#858585");
    public IBrush Selection { get; private init; } = Brush("#264F78");
    public IBrush? CurrentLineBackground { get; private init; }
    public IPen? CurrentLineBorder { get; private init; } = Pen("#282828");
    public IBrush Caret { get; private init; } = Brush("#AEAFAD");

    public IBrush WidgetBackground { get; private init; } = Brush("#252526");
    public IBrush WidgetBorder { get; private init; } = Brush("#454545");
    public IBrush WidgetForeground { get; private init; } = Brush("#CCCCCC");

    /// <summary>Details next to completion items and other secondary text in popups.</summary>
    public IBrush WidgetSecondaryForeground { get; private init; } = Brush("#9D9D9D");

    public IBrush ListSelectionBackground { get; private init; } = Brush("#04395E");
    public IBrush ListSelectionForeground { get; private init; } = Brush("#FFFFFF");

    public IBrush SymbolMethod { get; private init; } = Brush("#B180D7");
    public IBrush SymbolType { get; private init; } = Brush("#EE9D28");
    public IBrush SymbolKeyword { get; private init; } = Brush("#CCCCCC");
    public IBrush SymbolVariable { get; private init; } = Brush("#75BEFF");

    public IPen Error { get; private init; } = Pen("#F14C4C");
    public IPen Warning { get; private init; } = Pen("#CCA700");
    public IPen Info { get; private init; } = Pen("#3794FF");

    public IBrush FindMatch { get; private init; } = Brush("#EA5C0055");
    public IBrush BracketMatchBackground { get; private init; } = Brush("#0064001A");
    public IPen BracketMatchBorder { get; private init; } = Pen("#888888");

    private IReadOnlyDictionary<ClassificationKind, IBrush> Semantic { get; init; } =
        new Dictionary<ClassificationKind, IBrush>
        {
            [ClassificationKind.Namespace] = Brush("#4EC9B0"),
            [ClassificationKind.Class] = Brush("#4EC9B0"),
            [ClassificationKind.Struct] = Brush("#4EC9B0"),
            [ClassificationKind.Interface] = Brush("#4EC9B0"),
            [ClassificationKind.Enum] = Brush("#4EC9B0"),
            [ClassificationKind.Delegate] = Brush("#4EC9B0"),
            [ClassificationKind.TypeParameter] = Brush("#4EC9B0"),
            [ClassificationKind.Method] = Brush("#DCDCAA"),
            [ClassificationKind.ExtensionMethod] = Brush("#DCDCAA"),
            [ClassificationKind.Property] = Brush("#9CDCFE"),
            [ClassificationKind.Event] = Brush("#9CDCFE"),
            [ClassificationKind.Field] = Brush("#9CDCFE"),
            [ClassificationKind.Local] = Brush("#9CDCFE"),
            [ClassificationKind.Parameter] = Brush("#9CDCFE"),
            [ClassificationKind.Constant] = Brush("#4FC1FF"),
            [ClassificationKind.EnumMember] = Brush("#4FC1FF"),
        };

    /// <summary>The color of a semantic classification, or null to keep the TextMate color.</summary>
    public IBrush? GetSemanticBrush(ClassificationKind kind) => Semantic.GetValueOrDefault(kind);

    /// <summary>Reads the colors from a parsed VS Code theme.</summary>
    public static EditorTheme Create(Theme theme)
    {
        IReadOnlyDictionary<string, string> colors = theme.GetGuiColorDictionary();
        IBrush? background = Get("editor.background");
        bool dark = background is not ISolidColorBrush { Color: var color } || Luminance(color) < 0.5;
        EditorTheme fallback = dark ? s_dark : s_light;
        IBrush widgetBackground =
            Get("editorSuggestWidget.background") ?? Get("editorWidget.background") ?? fallback.WidgetBackground;
        return new EditorTheme
        {
            IsDark = dark,
            Background = background ?? fallback.Background,
            Foreground = Get("editor.foreground") ?? fallback.Foreground,
            LineNumbers = Get("editorLineNumber.foreground") ?? fallback.LineNumbers,
            Selection = Get("editor.selectionBackground") ?? fallback.Selection,
            CurrentLineBackground = Get("editor.lineHighlightBackground"),
            CurrentLineBorder =
                GetPen("editor.lineHighlightBorder")
                ?? (Get("editor.lineHighlightBackground") is null ? fallback.CurrentLineBorder : null),
            Caret = Get("editorCursor.foreground") ?? fallback.Caret,
            WidgetBackground = widgetBackground,
            WidgetBorder = Get("editorSuggestWidget.border") ?? Get("editorWidget.border") ?? fallback.WidgetBorder,
            WidgetForeground =
                Get("editorSuggestWidget.foreground") ?? Get("editorWidget.foreground") ?? fallback.WidgetForeground,
            WidgetSecondaryForeground = Get("descriptionForeground") ?? fallback.WidgetSecondaryForeground,
            ListSelectionBackground =
                Get("editorSuggestWidget.selectedBackground")
                ?? Get("list.activeSelectionBackground")
                ?? fallback.ListSelectionBackground,
            ListSelectionForeground =
                Get("editorSuggestWidget.selectedForeground")
                ?? Get("list.activeSelectionForeground")
                ?? fallback.ListSelectionForeground,
            SymbolMethod = Get("symbolIcon.methodForeground") ?? fallback.SymbolMethod,
            SymbolType = Get("symbolIcon.classForeground") ?? fallback.SymbolType,
            SymbolKeyword = Get("symbolIcon.keywordForeground") ?? fallback.SymbolKeyword,
            SymbolVariable = Get("symbolIcon.variableForeground") ?? fallback.SymbolVariable,
            Error = GetPen("editorError.foreground") ?? fallback.Error,
            Warning = GetPen("editorWarning.foreground") ?? fallback.Warning,
            Info = GetPen("editorInfo.foreground") ?? fallback.Info,
            FindMatch = Get("editor.findMatchHighlightBackground") ?? fallback.FindMatch,
            BracketMatchBackground = Get("editorBracketMatch.background") ?? fallback.BracketMatchBackground,
            BracketMatchBorder = GetPen("editorBracketMatch.border") ?? fallback.BracketMatchBorder,
            Semantic = ReadSemanticColors(theme),
        };

        IBrush? Get(string key) => colors.TryGetValue(key, out string? value) ? Brush(value) : null;

        IPen? GetPen(string key) => colors.TryGetValue(key, out string? value) ? Pen(value) : null;
    }

    /// <summary>
    /// The theme's token colors for the TextMate scopes VS Code uses when a theme has no semantic token rules, so
    /// semantic colors never clash with TextMate colors.
    /// </summary>
    private static Dictionary<ClassificationKind, IBrush> ReadSemanticColors(Theme theme)
    {
        (ClassificationKind Kind, string[] Scopes)[] scopes =
        [
            (ClassificationKind.Namespace, ["entity.name.namespace"]),
            (ClassificationKind.Class, ["entity.name.type.class", "entity.name.type"]),
            (ClassificationKind.Struct, ["entity.name.type.struct", "entity.name.type"]),
            (ClassificationKind.Interface, ["entity.name.type.interface", "entity.name.type"]),
            (ClassificationKind.Enum, ["entity.name.type.enum", "entity.name.type"]),
            (ClassificationKind.Delegate, ["entity.name.type"]),
            (ClassificationKind.TypeParameter, ["entity.name.type.parameter", "entity.name.type"]),
            (ClassificationKind.Method, ["entity.name.function.member", "entity.name.function"]),
            (ClassificationKind.ExtensionMethod, ["entity.name.function.member", "entity.name.function"]),
            (ClassificationKind.Property, ["variable.other.property", "variable"]),
            (ClassificationKind.Event, ["variable.other.event", "variable.other.property", "variable"]),
            (ClassificationKind.Field, ["variable.other.property", "variable"]),
            (ClassificationKind.Local, ["variable.other.readwrite", "variable"]),
            (ClassificationKind.Parameter, ["variable.parameter", "variable"]),
            (ClassificationKind.Constant, ["variable.other.constant", "variable"]),
            (ClassificationKind.EnumMember, ["variable.other.enummember", "variable"]),
            (ClassificationKind.Label, ["entity.name.label"]),
        ];
        var brushes = new Dictionary<ClassificationKind, IBrush>();
        foreach ((ClassificationKind kind, string[] candidates) in scopes)
        {
            foreach (string scope in candidates)
            {
                if (TokenColor(theme, scope) is { } color)
                {
                    brushes[kind] = Brush(color);
                    break;
                }
            }
        }
        return brushes;
    }

    /// <summary>The foreground a theme gives a single scope, or null when only its default foreground applies.</summary>
    private static string? TokenColor(Theme theme, string scope)
    {
        foreach (ThemeTrieElementRule rule in theme.Match([scope]))
        {
            if (rule.parentScopes is not { Count: > 0 })
                // Id 1 is the theme's default foreground.
                return rule.foreground > 1 ? theme.GetColor(rule.foreground) : null;
        }
        return null;
    }

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255;

    private static ImmutableSolidColorBrush Brush(string color) => new(CodeEditor.ParseThemeColor(color));

    private static ImmutablePen Pen(string color) => new(Brush(color));
}
