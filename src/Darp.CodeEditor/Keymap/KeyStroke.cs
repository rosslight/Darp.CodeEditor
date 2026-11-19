using Avalonia.Input;

namespace Darp.CodeEditor;

/// <summary>
/// One stroke of a key sequence: a key with its modifiers, read from VS Code's syntax, such as <c>ctrl+shift+space</c>.
/// </summary>
/// <param name="Key">The key; for a character key, its place on a US layout.</param>
/// <param name="Modifiers">The modifiers, all of which must be held, and no others.</param>
/// <param name="Symbol">
/// The character of a punctuation key, such as <c>/</c>; null for other keys. A key that types it counts, also where the
/// layout needs Shift for it.
/// </param>
internal readonly record struct KeyStroke(Key Key, KeyModifiers Modifiers, string? Symbol = null)
{
    /// <summary>VS Code's names of keys that are not a letter, a digit or a function key.</summary>
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

    /// <summary>The punctuation keys of a US layout, by their character, and the number pad's where it has one.</summary>
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
    /// Reads a key sequence: strokes apart by spaces, at most two; none for an empty text.
    /// </summary>
    /// <exception cref="FormatException">The text is not a sequence in VS Code's syntax.</exception>
    public static IReadOnlyList<KeyStroke> ParseSequence(string keys)
    {
        string[] parts = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2)
            throw new FormatException($"'{keys}' has more than two strokes.");
        return [.. parts.Select(Parse)];
    }

    /// <summary>Reads one stroke, such as <c>ctrl+k</c> or <c>shift+cmd+space</c>, ignoring case.</summary>
    /// <exception cref="FormatException">The text is not a stroke in VS Code's syntax.</exception>
    public static KeyStroke Parse(string stroke)
    {
        // The key may be "+" itself, as in "ctrl++".
        string keyName = stroke.EndsWith("++", StringComparison.Ordinal)
            ? "+"
            : stroke[(stroke.LastIndexOf('+') + 1)..];
        string modifierPart = stroke[..Math.Max(0, stroke.Length - keyName.Length - 1)];
        KeyModifiers modifiers = KeyModifiers.None;
        foreach (string modifier in modifierPart.Split('+', StringSplitOptions.RemoveEmptyEntries))
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
        if (keyName.Length == 0)
            throw new FormatException($"'{stroke}' has no key.");
        if (s_symbols.TryGetValue(keyName, out (Key Key, Key? NumPad) symbol))
            return new KeyStroke(symbol.Key, modifiers, keyName);
        if (keyName.Length == 1 && char.IsAsciiLetter(keyName[0]))
            return new KeyStroke(Key.A + (char.ToUpperInvariant(keyName[0]) - 'A'), modifiers);
        if (keyName.Length == 1 && char.IsAsciiDigit(keyName[0]))
            return new KeyStroke(Key.D0 + (keyName[0] - '0'), modifiers);
        if (
            keyName.Length is 2 or 3
            && keyName[0] is 'f' or 'F'
            && int.TryParse(keyName.AsSpan(1), out int function)
            && function is >= 1 and <= 24
        )
            return new KeyStroke(Key.F1 + (function - 1), modifiers);
        if (
            keyName.StartsWith("numpad", StringComparison.OrdinalIgnoreCase)
            && keyName.Length == 7
            && char.IsAsciiDigit(keyName[6])
        )
            return new KeyStroke(Key.NumPad0 + (keyName[6] - '0'), modifiers);
        if (s_named.TryGetValue(keyName, out Key named))
            return new KeyStroke(named, modifiers);
        throw new FormatException($"'{keyName}' in '{stroke}' is no key.");
    }

    /// <summary>
    /// Whether <paramref name="e"/> is this stroke: its key with exactly its modifiers. A punctuation stroke also matches
    /// the key that types its character, with Shift where the layout needs it, and the number pad's key.
    /// </summary>
    public bool Matches(KeyEventArgs e)
    {
        if (Symbol is null)
            return e.Key == Key && e.KeyModifiers == Modifiers;
        if (e.KeySymbol == Symbol)
            return (e.KeyModifiers & ~KeyModifiers.Shift) == (Modifiers & ~KeyModifiers.Shift);
        if (e.KeyModifiers != Modifiers)
            return false;
        return e.Key == Key || (s_symbols[Symbol].NumPad is { } numPad && e.Key == numPad);
    }

    /// <summary>The stroke as Avalonia's gesture, for showing it.</summary>
    public KeyGesture ToGesture() => new(Key, Modifiers);

    /// <summary>The stroke in VS Code's syntax.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("shift");
        if (Modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("alt");
        if (Modifiers.HasFlag(KeyModifiers.Meta))
            parts.Add("cmd");
        parts.Add(Symbol ?? KeyName(Key));
        return string.Join('+', parts);
    }

    private static string KeyName(Key key) =>
        key switch
        {
            >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.F1 and <= Key.F24 => $"f{key - Key.F1 + 1}",
            >= Key.NumPad0 and <= Key.NumPad9 => $"numpad{key - Key.NumPad0}",
            _ => s_named.FirstOrDefault(pair => pair.Value == key).Key ?? key.ToString().ToLowerInvariant(),
        };
}
