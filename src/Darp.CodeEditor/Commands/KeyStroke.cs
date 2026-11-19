using Avalonia.Input;

namespace Darp.CodeEditor.Commands;

/// <summary>A key as the user pressed it: the key, the modifiers held, and the character it types, if any.</summary>
internal readonly record struct PressedKey(Key Key, KeyModifiers Modifiers, string? Symbol)
{
    public static PressedKey Of(KeyEventArgs e) => new(e.Key, e.KeyModifiers, e.KeySymbol);

    public KeyGesture ToGesture() => new(Key, Modifiers);
}

/// <summary>One stroke of a key sequence: a key and the modifiers held with it, read from VS Code's syntax.</summary>
/// <param name="Key">The key; for a character key, its place on a US layout.</param>
/// <param name="Modifiers">The modifiers, all of which must be held, and no others.</param>
/// <param name="Symbol">
/// The character of a punctuation key, such as <c>/</c>, which also matches the key that types it on other layouts;
/// null for other keys.
/// </param>
internal readonly record struct KeyStroke(Key Key, KeyModifiers Modifiers, string? Symbol = null)
{
    private static readonly Dictionary<string, Key> s_named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = Key.Space,
        ["tab"] = Key.Tab,
        ["enter"] = Key.Enter,
        ["escape"] = Key.Escape,
        ["backspace"] = Key.Back,
        ["delete"] = Key.Delete,
        ["insert"] = Key.Insert,
        ["home"] = Key.Home,
        ["end"] = Key.End,
        ["pageup"] = Key.PageUp,
        ["pagedown"] = Key.PageDown,
        ["up"] = Key.Up,
        ["down"] = Key.Down,
        ["left"] = Key.Left,
        ["right"] = Key.Right,
        ["numpad_add"] = Key.Add,
        ["numpad_subtract"] = Key.Subtract,
        ["numpad_multiply"] = Key.Multiply,
        ["numpad_divide"] = Key.Divide,
        ["numpad_decimal"] = Key.Decimal,
    };

    /// <summary>
    /// The punctuation keys of a US layout by their character, with the number pad's key where it has one.
    /// </summary>
    private static readonly Dictionary<string, (Key Key, Key? NumPad)> s_symbols = new()
    {
        ["`"] = (Key.OemTilde, null),
        ["-"] = (Key.OemMinus, Key.Subtract),
        ["="] = (Key.OemPlus, null),
        ["["] = (Key.OemOpenBrackets, null),
        ["]"] = (Key.OemCloseBrackets, null),
        ["\\"] = (Key.OemPipe, null),
        [";"] = (Key.OemSemicolon, null),
        ["'"] = (Key.OemQuotes, null),
        [","] = (Key.OemComma, null),
        ["."] = (Key.OemPeriod, Key.Decimal),
        ["/"] = (Key.OemQuestion, Key.Divide),
    };

    /// <summary>
    /// Reads a key sequence of one or two strokes apart by a space, such as <c>ctrl+k ctrl+i</c>; none for an empty
    /// text.
    /// </summary>
    /// <exception cref="FormatException">The text is no sequence in VS Code's syntax.</exception>
    public static (KeyStroke? First, KeyStroke? Second) ParseSequence(string keys)
    {
        string[] strokes = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return strokes switch
        {
            [] => (null, null),
            [var first] => (Parse(first), null),
            [var first, var second] => (Parse(first), Parse(second)),
            _ => throw new FormatException($"'{keys}' has more than two strokes."),
        };
    }

    /// <summary>The stroke as Avalonia's gesture, which shows it in the platform's way, such as in a menu.</summary>
    public KeyGesture ToGesture() => new(Key, Modifiers);

    /// <summary>
    /// Whether <paramref name="key"/> is this stroke: its key with exactly its modifiers. A punctuation stroke also
    /// matches the key that types its character, with the Shift that the layout needs for it unless the stroke has
    /// Shift itself, and the number pad's key.
    /// </summary>
    public bool Matches(PressedKey key)
    {
        if (Symbol is null)
            return key.Key == Key && key.Modifiers == Modifiers;
        if (key.Symbol == Symbol)
            return Modifiers.HasFlag(KeyModifiers.Shift)
                ? key.Modifiers == Modifiers
                : (key.Modifiers & ~KeyModifiers.Shift) == Modifiers;
        return key.Modifiers == Modifiers && (key.Key == Key || key.Key == s_symbols[Symbol].NumPad);
    }

    /// <summary>Reads one stroke, such as <c>ctrl+k</c> or <c>shift+cmd+space</c>, in any case.</summary>
    private static KeyStroke Parse(string stroke)
    {
        int split = stroke.LastIndexOf('+');
        string key = stroke[(split + 1)..];
        KeyModifiers modifiers = KeyModifiers.None;
        foreach (string modifier in split < 0 ? [] : stroke[..split].Split('+'))
        {
            modifiers |= modifier.ToLowerInvariant() switch
            {
                "ctrl" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "cmd" or "meta" or "win" => KeyModifiers.Meta,
                _ => throw new FormatException($"'{modifier}' in '{stroke}' is no modifier."),
            };
        }
        return new KeyStroke(KeyOf(key, stroke), modifiers, s_symbols.ContainsKey(key) ? key : null);
    }

    private static Key KeyOf(string key, string stroke)
    {
        if (s_symbols.TryGetValue(key, out (Key Key, Key? NumPad) symbol))
            return symbol.Key;
        if (key is [char letter] && char.IsAsciiLetter(letter))
            return Key.A + (char.ToUpperInvariant(letter) - 'A');
        if (key is [char digit] && char.IsAsciiDigit(digit))
            return Key.D0 + (digit - '0');
        if (key is ['f' or 'F', _, ..] && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 24)
            return Key.F1 + (function - 1);
        if (
            key.Length == 7
            && key.StartsWith("numpad", StringComparison.OrdinalIgnoreCase)
            && char.IsAsciiDigit(key[6])
        )
            return Key.NumPad0 + (key[6] - '0');
        if (s_named.TryGetValue(key, out Key named))
            return named;
        throw new FormatException($"'{key}' in '{stroke}' is no key.");
    }
}
