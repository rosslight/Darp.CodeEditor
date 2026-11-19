using System.Text.Json;
using Avalonia.Media;
using TextMateSharp.Internal.Themes;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.Loading;

/// <summary>
/// A VS Code color theme, read with the themes it includes: the TextMate token rules, the editor's colors and the
/// semantic token colors. Immutable once read.
/// </summary>
/// <param name="TokenRules">The <c>tokenColors</c>, included themes first, as TextMateSharp reads a theme.</param>
/// <param name="TokenColors">
/// The <c>tokenColors</c> that set a foreground, in the same order, for finding the color of a scope as VS Code does
/// for semantic tokens.
/// </param>
/// <param name="Colors">The <c>colors</c>, such as <c>editor.background</c>; the theme's own override included ones.</param>
/// <param name="SemanticTokenColors">The <c>semanticTokenColors</c>, by selector, such as <c>stringLiteral</c>.</param>
/// <param name="Type">
/// The theme's <c>type</c>, such as <c>dark</c> or <c>hcLight</c>, where a file names one; null otherwise.
/// </param>
internal sealed record ColorThemeFile(
    IRawTheme TokenRules,
    IReadOnlyList<TokenColorRule> TokenColors,
    IReadOnlyDictionary<string, Color> Colors,
    IReadOnlyDictionary<string, SemanticTokenStyle> SemanticTokenColors,
    string? Type = null
)
{
    /// <summary>Reads a theme file and the themes it includes, relative to it.</summary>
    public static ColorThemeFile Read(Uri source)
    {
        var rules = new List<object>();
        var tokenColors = new List<TokenColorRule>();
        var colors = new Dictionary<string, Color>();
        var semanticTokenColors = new Dictionary<string, SemanticTokenStyle>();
        var files = new HashSet<Uri>();
        string? type = null;
        ReadFile(source);
        // As VS Code does, text without a rule of its own has the editor's foreground and background.
        var defaults = new ThemeRaw();
        if (colors.TryGetValue("editor.foreground", out Color foreground))
            defaults["foreground"] = Hex(foreground);
        if (colors.TryGetValue("editor.background", out Color background))
            defaults["background"] = Hex(background);
        rules.Insert(0, new ThemeRaw { ["settings"] = defaults });
        return new ColorThemeFile(
            new ThemeRaw { ["tokenColors"] = rules },
            tokenColors,
            colors,
            semanticTokenColors,
            type
        );

        void ReadFile(Uri file)
        {
            if (!files.Add(file))
                throw new InvalidDataException($"The theme includes itself: {file}");
            using Stream stream = AssetFiles.Open(file);
            using JsonDocument json = JsonDocument.Parse(stream, AssetFiles.Jsonc);
            JsonElement root = json.RootElement;
            if (root.TryGetProperty("include", out JsonElement include))
                ReadFile(new Uri(file, include.GetString()));
            if (root.TryGetProperty("type", out JsonElement typeValue) && typeValue.ValueKind == JsonValueKind.String)
                type = typeValue.GetString();
            if (root.TryGetProperty("colors", out JsonElement colorValues))
            {
                foreach (JsonProperty color in colorValues.EnumerateObject())
                {
                    // VS Code ignores colors it cannot read, and so does the editor.
                    if (color.Value.GetString() is { } value && TryParseColor(value, out Color parsed))
                        colors[color.Name] = parsed;
                }
            }
            if (root.TryGetProperty("tokenColors", out JsonElement tokenRules))
            {
                rules.AddRange(tokenRules.EnumerateArray().Select(TokenRule));
                tokenColors.AddRange(tokenRules.EnumerateArray().Select(TokenColor).OfType<TokenColorRule>());
            }
            if (root.TryGetProperty("semanticTokenColors", out JsonElement semantic))
            {
                // A theme's rule overrides only what it sets of an included rule for the same selector.
                foreach (JsonProperty style in semantic.EnumerateObject())
                {
                    SemanticTokenStyle read = SemanticStyle(style.Value);
                    semanticTokenColors[style.Name] = semanticTokenColors.TryGetValue(style.Name, out var included)
                        ? read.Over(included)
                        : read;
                }
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

    /// <summary>A rule of <c>tokenColors</c> with its scopes, if it sets a foreground that VS Code can read.</summary>
    private static TokenColorRule? TokenColor(JsonElement rule)
    {
        if (
            !rule.TryGetProperty("settings", out JsonElement settings)
            || !settings.TryGetProperty("foreground", out JsonElement foreground)
            || foreground.GetString() is not { } value
            || !TryParseColor(value, out Color color)
            || !rule.TryGetProperty("scope", out JsonElement scope)
        )
        {
            return null;
        }
        IEnumerable<string?> scopes =
            scope.ValueKind == JsonValueKind.Array
                ? scope.EnumerateArray().Select(s => s.GetString())
                : (scope.GetString() ?? "").Split(',');
        return new TokenColorRule([.. scopes.OfType<string>().Select(s => s.Trim())], color);
    }

    /// <summary>
    /// A color, or an object with <c>foreground</c>, <c>fontStyle</c>, and <c>bold</c>, <c>italic</c>,
    /// <c>underline</c> and <c>strikethrough</c>. As in VS Code, <c>fontStyle</c> sets all four styles and wins over them.
    /// </summary>
    private static SemanticTokenStyle SemanticStyle(JsonElement style)
    {
        if (style.ValueKind == JsonValueKind.String)
            return new SemanticTokenStyle(ReadColor(style), null, null, null, null);
        Color? foreground = style.TryGetProperty("foreground", out JsonElement color) ? ReadColor(color) : null;
        if (style.TryGetProperty("fontStyle", out JsonElement fontStyle))
        {
            // As VS Code does, finds each name anywhere in the value, whatever separates them.
            string names = fontStyle.GetString() ?? "";
            return new SemanticTokenStyle(
                foreground,
                names.Contains("bold"),
                names.Contains("italic"),
                names.Contains("underline"),
                names.Contains("strikethrough")
            );
        }
        return new SemanticTokenStyle(
            foreground,
            Flag("bold"),
            Flag("italic"),
            Flag("underline"),
            Flag("strikethrough")
        );

        bool? Flag(string name) => style.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : null;

        static Color ReadColor(JsonElement value) =>
            value.GetString() is { } text && TryParseColor(text, out Color parsed)
                ? parsed
                : throw new InvalidDataException($"Not a color: {value}");
    }
}

/// <summary>A rule of a theme's <c>tokenColors</c> that sets a foreground.</summary>
/// <param name="Scopes">Its scope selectors, such as <c>entity.name.type</c> or <c>source.cs keyword</c>.</param>
/// <param name="Foreground">The color of the scopes it selects.</param>
internal sealed record TokenColorRule(IReadOnlyList<string> Scopes, Color Foreground);

/// <summary>How a theme shows a kind of semantic token. Null keeps what the TextMate rules give.</summary>
/// <param name="Foreground">The text color.</param>
/// <param name="Bold">Whether the text is bold.</param>
/// <param name="Italic">Whether the text is italic.</param>
/// <param name="Underline">Whether the text is underlined.</param>
/// <param name="Strikethrough">Whether the text is struck through.</param>
internal readonly record struct SemanticTokenStyle(
    Color? Foreground,
    bool? Bold,
    bool? Italic,
    bool? Underline,
    bool? Strikethrough
)
{
    /// <summary>This style, with what it leaves null taken from <paramref name="included"/>.</summary>
    public SemanticTokenStyle Over(SemanticTokenStyle included) =>
        new(
            Foreground ?? included.Foreground,
            Bold ?? included.Bold,
            Italic ?? included.Italic,
            Underline ?? included.Underline,
            Strikethrough ?? included.Strikethrough
        );
}
