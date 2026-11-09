using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.LanguageClient;
using Darp.CodeEditor.Loading;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// The colors of a server's semantic tokens in a color theme, found as VS Code finds them: the theme's
/// <c>semanticTokenColors</c> first, otherwise the theme's TextMate color of the scopes that VS Code maps the token's type
/// and modifiers to. Tokens without either keep their TextMate colors.
/// </summary>
/// <remarks>
/// One set of colors serves one theme, one language and one server's legend. It finds each combination of type and
/// modifiers once, on the UI thread, when a token of it is first drawn. A server chooses the types and modifiers, so
/// that work is bounded: only the modifiers that a selector names count, and only the first
/// <see cref="MaxTypes"/> types of a legend are colored.
/// </remarks>
internal sealed class SemanticTokenStyles
{
    /// <summary>
    /// How many of a legend's types are colored, at most: a server names dozens, while each one colored costs the UI
    /// thread a search of the theme's rules.
    /// </summary>
    public const int MaxTypes = 1_000;

    /// <summary>The protocol's standard token types, which the client advertises and themes color.</summary>
    public static readonly string[] TokenTypes =
    [
        "namespace",
        "type",
        "class",
        "enum",
        "interface",
        "struct",
        "typeParameter",
        "parameter",
        "variable",
        "property",
        "enumMember",
        "event",
        "function",
        "method",
        "macro",
        "keyword",
        "modifier",
        "comment",
        "string",
        "number",
        "regexp",
        "operator",
        "decorator",
        "label",
    ];

    /// <summary>The protocol's standard token modifiers, which the client advertises and themes color.</summary>
    public static readonly string[] TokenModifiers =
    [
        "declaration",
        "definition",
        "readonly",
        "static",
        "deprecated",
        "abstract",
        "async",
        "modification",
        "documentation",
        "defaultLibrary",
    ];

    /// <summary>The types that VS Code makes subtypes of another: a selector of the supertype matches them too.</summary>
    private static readonly Dictionary<string, string> s_superTypes = new()
    {
        ["struct"] = "type",
        ["class"] = "type",
        ["interface"] = "type",
        ["enum"] = "type",
        ["typeParameter"] = "type",
        ["member"] = "method",
    };

    /// <summary>
    /// VS Code's standard mapping of semantic tokens to TextMate scopes, in its order: for each selector, the scopes to
    /// try one after another. The first scope that the theme colors gives the color.
    /// </summary>
    private static readonly (Selector Selector, string[] Scopes)[] s_scopeRules =
    [
        .. new (string, string[])[]
        {
            ("comment", ["comment"]),
            ("string", ["string"]),
            ("keyword", ["keyword.control"]),
            ("number", ["constant.numeric"]),
            ("regexp", ["constant.regexp"]),
            ("operator", ["keyword.operator"]),
            ("namespace", ["entity.name.namespace"]),
            ("type", ["entity.name.type", "support.type"]),
            ("struct", ["entity.name.type.struct"]),
            ("class", ["entity.name.type.class", "support.class"]),
            ("interface", ["entity.name.type.interface"]),
            ("enum", ["entity.name.type.enum"]),
            ("typeParameter", ["entity.name.type.parameter"]),
            ("function", ["entity.name.function", "support.function"]),
            ("method", ["entity.name.function.member", "support.function"]),
            ("macro", ["entity.name.function.preprocessor"]),
            ("variable", ["variable.other.readwrite", "entity.name.variable"]),
            ("parameter", ["variable.parameter"]),
            ("property", ["variable.other.property"]),
            ("enumMember", ["variable.other.enummember"]),
            ("event", ["variable.other.event"]),
            ("decorator", ["entity.name.decorator", "entity.name.function"]),
            ("variable.readonly", ["variable.other.constant"]),
            ("property.readonly", ["variable.other.constant.property"]),
            ("type.defaultLibrary", ["support.type"]),
            ("class.defaultLibrary", ["support.class"]),
            ("interface.defaultLibrary", ["support.class"]),
            ("variable.defaultLibrary", ["support.variable", "support.other.variable"]),
            ("variable.defaultLibrary.readonly", ["support.constant"]),
            ("property.defaultLibrary", ["support.variable.property"]),
            ("property.defaultLibrary.readonly", ["support.constant.property"]),
            ("function.defaultLibrary", ["support.function"]),
            ("member.defaultLibrary", ["support.function"]),
        }.Select(rule => (Selector.Parse(rule.Item1), rule.Item2)),
    ];

    private readonly string _language;
    private readonly SemanticTokensLegend _legend;
    private readonly (Selector Selector, Color Foreground)[] _themeRules;

    /// <summary>The standard mapping's selectors with the color the theme gives their scopes, where it gives one.</summary>
    private readonly (Selector Selector, Color Foreground)[] _scopeRules;

    /// <summary>The bits of the legend's modifiers that a selector names: the others change no color.</summary>
    private readonly int _namedModifiers;

    /// <summary>The foreground of each combination of type and modifiers found so far; null for none.</summary>
    private readonly Dictionary<(int Type, int Modifiers), IBrush?> _foregrounds = [];

    public SemanticTokenStyles(ColorThemeFile theme, string language, SemanticTokensLegend legend)
    {
        _language = language;
        _legend = legend;
        _themeRules =
        [
            .. theme
                .SemanticTokenColors.Where(rule => rule.Value.Foreground is not null)
                .Select(rule => (Selector.Parse(rule.Key), rule.Value.Foreground.GetValueOrDefault())),
        ];
        _scopeRules =
        [
            .. s_scopeRules
                .Select(rule => (rule.Selector, Foreground: ScopeColor(theme, rule.Scopes)))
                .Where(rule => rule.Foreground is not null)
                .Select(rule => (rule.Selector, rule.Foreground.GetValueOrDefault())),
        ];
        HashSet<string> named = [.. _themeRules.Concat(_scopeRules).SelectMany(rule => rule.Selector.Modifiers)];
        for (int i = 0; i < legend.TokenModifiers.Length && i < 32; i++)
        {
            if (named.Contains(legend.TokenModifiers[i]))
                _namedModifiers |= 1 << i;
        }
    }

    /// <summary>
    /// The foreground of a token with <paramref name="type"/> and <paramref name="modifiers"/>, an index and bits of the
    /// legend; null where the TextMate colors stay.
    /// </summary>
    public IBrush? Foreground(int type, int modifiers)
    {
        if (type >= MaxTypes)
            return null;
        modifiers &= _namedModifiers;
        if (!_foregrounds.TryGetValue((type, modifiers), out IBrush? brush))
        {
            Color? color = FindForeground(_legend.TokenTypes[type], ModifierNames(modifiers));
            brush = color is { } c ? new ImmutableSolidColorBrush(c) : null;
            _foregrounds[(type, modifiers)] = brush;
        }
        return brush;
    }

    /// <summary>
    /// As VS Code: the theme's rule that matches best, a later one on a tie; without one, the best of the standard
    /// mapping whose scopes the theme colors.
    /// </summary>
    private Color? FindForeground(string type, string[] modifiers)
    {
        Color? foreground = null;
        int best = -1;
        foreach ((Selector selector, Color color) in _themeRules)
        {
            if (selector.Match(type, modifiers, _language) is var score and >= 0 && score >= best)
                (foreground, best) = (color, score);
        }
        if (foreground is not null)
            return foreground;
        foreach ((Selector selector, Color color) in _scopeRules)
        {
            if (selector.Match(type, modifiers, _language) is var score and >= 0 && score >= best)
                (foreground, best) = (color, score);
        }
        return foreground;
    }

    /// <summary>The color of the first of <paramref name="scopes"/> that a rule of <paramref name="theme"/> colors.</summary>
    private static Color? ScopeColor(ColorThemeFile theme, string[] scopes)
    {
        foreach (string scope in scopes)
        {
            Color? color = null;
            int longest = -1;
            // The longest selector that is the scope or one of its parents wins, a later rule on a tie. Selectors with
            // ancestors, such as "source.cs keyword", need more than one scope.
            foreach (TokenColorRule rule in theme.TokenColors)
            {
                foreach (string selector in rule.Scopes)
                {
                    if (selector.Length >= longest && IsScopeOrParent(selector, scope))
                        (color, longest) = (rule.Foreground, selector.Length);
                }
            }
            if (color is not null)
                return color;
        }
        return null;
    }

    private static bool IsScopeOrParent(string selector, string scope) =>
        selector.Length > 0
        && scope.StartsWith(selector, StringComparison.Ordinal)
        && (scope.Length == selector.Length || scope[selector.Length] == '.');

    private string[] ModifierNames(int modifiers)
    {
        var names = new List<string>();
        for (int i = 0; i < _legend.TokenModifiers.Length && i < 32; i++)
        {
            if ((modifiers & (1 << i)) != 0)
                names.Add(_legend.TokenModifiers[i]);
        }
        return [.. names];
    }

    /// <summary>
    /// A selector of semantic tokens, as VS Code writes them: a type or <c>*</c>, modifiers after dots, and a language
    /// after a colon, such as <c>variable.readonly</c>, <c>*.deprecated</c> or <c>type:yaml</c>.
    /// </summary>
    private readonly record struct Selector(string Type, string[] Modifiers, string? Language)
    {
        public static Selector Parse(string text)
        {
            string? language = null;
            if (text.IndexOf(':') is var colon and >= 0)
                (text, language) = (text[..colon], text[(colon + 1)..]);
            string[] parts = text.Split('.');
            return new Selector(parts[0], parts[1..], language);
        }

        /// <summary>
        /// How well the selector matches, as VS Code scores it: 100 for the token's type, a little less for a
        /// supertype, nothing for <c>*</c>, 100 more for each modifier and 10 for the language; -1 for no match.
        /// </summary>
        public int Match(string type, string[] modifiers, string language)
        {
            int score = 0;
            if (Language is not null)
            {
                if (Language != language)
                    return -1;
                score += 10;
            }
            if (Type != "*")
            {
                int level = 0;
                string? current = type;
                while (current is not null && current != Type)
                {
                    current = s_superTypes.GetValueOrDefault(current);
                    level++;
                }
                if (current is null)
                    return -1;
                score += 100 - level;
            }
            foreach (string modifier in Modifiers)
            {
                if (Array.IndexOf(modifiers, modifier) < 0)
                    return -1;
            }
            return score + Modifiers.Length * 100;
        }
    }
}
