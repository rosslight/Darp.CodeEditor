using System.Text.Json;
using Avalonia.Media;
using TextMateSharp.Internal.Themes;
using TextMateSharp.Themes;
using FontStyle = TextMateSharp.Themes.FontStyle;

namespace Darp.CodeEditor.Loading;

/// <summary>
/// A VS Code color theme, read with the themes it includes: the TextMate token rules, the editor's colors and the
/// semantic token colors. Immutable once read.
/// </summary>
/// <param name="TokenRules">The <c>tokenColors</c>, included themes first, as TextMateSharp reads a theme.</param>
/// <param name="Colors">The <c>colors</c>, such as <c>editor.background</c>; the theme's own override included ones.</param>
/// <param name="SemanticTokenColors">The <c>semanticTokenColors</c>, by selector, such as <c>stringLiteral</c>.</param>
internal sealed record ColorThemeFile(
    IRawTheme TokenRules,
    IReadOnlyDictionary<string, Color> Colors,
    IReadOnlyDictionary<string, SemanticTokenStyle> SemanticTokenColors
)
{
    /// <summary>Reads a theme file and the themes it includes, relative to it.</summary>
    public static ColorThemeFile Read(Uri source)
    {
        var rules = new List<object>();
        var colors = new Dictionary<string, Color>();
        var semanticTokenColors = new Dictionary<string, SemanticTokenStyle>();
        var files = new HashSet<Uri>();
        ReadFile(source);
        // As VS Code does, text without a rule of its own has the editor's foreground and background.
        var defaults = new ThemeRaw();
        if (colors.TryGetValue("editor.foreground", out Color foreground))
            defaults["foreground"] = Hex(foreground);
        if (colors.TryGetValue("editor.background", out Color background))
            defaults["background"] = Hex(background);
        rules.Insert(0, new ThemeRaw { ["settings"] = defaults });
        return new ColorThemeFile(new ThemeRaw { ["tokenColors"] = rules }, colors, semanticTokenColors);

        void ReadFile(Uri file)
        {
            if (!files.Add(file))
                throw new InvalidDataException($"The theme includes itself: {file}");
            using Stream stream = AssetFiles.Open(file);
            using JsonDocument json = JsonDocument.Parse(stream, AssetFiles.Jsonc);
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("include", out JsonElement include))
                ReadFile(new Uri(file, include.GetString()));
            if (root.TryGetProperty("colors", out JsonElement colorValues))
            {
                foreach (JsonProperty color in colorValues.EnumerateObject())
                {
                    // VS Code ignores colors it cannot read, and so does the editor.
                    if (color.Value.GetString() is { } value && TryParseColor(value, out Color parsed))
                        colors[color.Name] = parsed;
                }
            }
            if (root.TryGetProperty("tokenColors", out JsonElement tokenColors))
                rules.AddRange(tokenColors.EnumerateArray().Select(TokenRule));
            if (root.TryGetProperty("semanticTokenColors", out JsonElement semantic))
            {
                foreach (JsonProperty style in semantic.EnumerateObject())
                    semanticTokenColors[style.Name] = SemanticStyle(style.Value);
            }
        }
    }

    /// <summary>
    /// Parses a VS Code color: <c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>. Avalonia puts the alpha
    /// first, VS Code last.
    /// </summary>
    public static bool TryParseColor(string color, out Color parsed) =>
        Color.TryParse(
            color switch
            {
                ['#', _, _, _, _, _, _, _, _] => $"#{color[7..9]}{color[1..7]}",
                ['#', var r, var g, var b, var a] => $"#{a}{r}{g}{b}",
                _ => color,
            },
            out parsed
        );

    /// <summary>A color as VS Code and TextMateSharp write it, with the alpha last.</summary>
    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    private static readonly string[] s_settingNames = ["foreground", "background", "fontStyle"];

    /// <summary>A rule of <c>tokenColors</c>, as TextMateSharp's own theme reader makes it.</summary>
    private static ThemeRaw TokenRule(JsonElement rule)
    {
        var raw = new ThemeRaw();
        if (rule.TryGetProperty("scope", out JsonElement scope))
        {
            raw["scope"] =
                scope.ValueKind == JsonValueKind.Array
                    ? scope.EnumerateArray().Select(object? (s) => s.GetString()).ToList()
                    : scope.GetString();
        }
        if (rule.TryGetProperty("settings", out JsonElement settings))
        {
            var rawSettings = new ThemeRaw();
            foreach (string name in s_settingNames)
            {
                if (settings.TryGetProperty(name, out JsonElement value))
                    rawSettings[name] = value.GetString();
            }
            raw["settings"] = rawSettings;
        }
        return raw;
    }

    /// <summary>A color, or an object with <c>foreground</c> and <c>fontStyle</c>.</summary>
    private static SemanticTokenStyle SemanticStyle(JsonElement style)
    {
        if (style.ValueKind == JsonValueKind.String)
            return new SemanticTokenStyle(ReadColor(style), FontStyle.NotSet);
        return new SemanticTokenStyle(
            style.TryGetProperty("foreground", out JsonElement foreground) ? ReadColor(foreground) : null,
            style.TryGetProperty("fontStyle", out JsonElement fontStyle)
                ? ParseFontStyle(fontStyle.GetString() ?? "")
                : FontStyle.NotSet
        );

        static Color ReadColor(JsonElement value) =>
            value.GetString() is { } text && TryParseColor(text, out Color parsed)
                ? parsed
                : throw new InvalidDataException($"Not a color: {value}");
    }

    /// <summary>A font style as VS Code writes it: names separated by spaces, such as <c>italic bold</c>.</summary>
    private static FontStyle ParseFontStyle(string names)
    {
        FontStyle style = FontStyle.None;
        foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            style |= name switch
            {
                "italic" => FontStyle.Italic,
                "bold" => FontStyle.Bold,
                "underline" => FontStyle.Underline,
                "strikethrough" => FontStyle.Strikethrough,
                _ => FontStyle.None,
            };
        }
        return style;
    }
}

/// <summary>How a theme shows a kind of semantic token.</summary>
/// <param name="Foreground">The text color; null to keep the TextMate color.</param>
/// <param name="FontStyle">The font style; <see cref="FontStyle.NotSet"/> to keep the TextMate style.</param>
internal readonly record struct SemanticTokenStyle(Color? Foreground, FontStyle FontStyle);
