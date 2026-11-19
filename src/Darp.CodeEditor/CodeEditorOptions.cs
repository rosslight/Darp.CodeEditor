namespace Darp.CodeEditor;

/// <summary>
/// The editor's settings that apps set differently for different editors or users, each at the path of its VS Code
/// setting: <c>editor.autoClosingBrackets</c> is <c>AutoClosing.Brackets</c>. Behavior without a setting here is built
/// in, with VS Code's default. Immutable: set it once, or share one instance between editors, as a keymap.
/// </summary>
public sealed record CodeEditorOptions
{
    /// <summary>Every setting at its default.</summary>
    public static CodeEditorOptions Default { get; } = new();

    /// <summary>Which opening characters bring their closing ones: <c>editor.autoClosing*</c>.</summary>
    public AutoClosingOptions AutoClosing { get; init; } = new();

    /// <summary>
    /// Whether copy and cut without a selection take the caret's whole line, which then pastes as a whole line above
    /// the caret's: <c>editor.emptySelectionClipboard</c>. Off, they need a selection, so that a stray Ctrl+C keeps the
    /// clipboard.
    /// </summary>
    public bool EmptySelectionClipboard { get; init; } = true;

    /// <summary>What the search bar starts with as it opens: <c>editor.find.*</c>.</summary>
    public FindOptions Find { get; init; } = new();

    /// <summary>
    /// Which brackets are boxed at a caret without a selection: <c>editor.matchBrackets</c>.
    /// <see cref="MatchBrackets.Near"/> is the cheaper, quieter mode.
    /// </summary>
    public MatchBrackets MatchBrackets { get; init; } = MatchBrackets.Always;

    /// <summary>
    /// Whether the occurrences of the symbol at the caret are highlighted, the language server's or else the word's:
    /// <c>editor.occurrencesHighlight</c>. Off, a caret move asks the server nothing.
    /// </summary>
    public bool OccurrencesHighlight { get; init; } = true;

    /// <summary>Signature help as the user types: <c>editor.parameterHints</c>.</summary>
    public ParameterHintsOptions ParameterHints { get; init; } = new();
}

/// <summary>
/// Signature help as the user types: the signatures of the call around the caret, on the server's trigger characters.
/// </summary>
public sealed record ParameterHintsOptions
{
    /// <summary>
    /// Whether typing opens signature help: <c>editor.parameterHints.enabled</c>. Off, only its command opens it, and
    /// turning it off closes signature help that shows.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>
/// Which typed opening brackets and quotes bring their closing ones. Typing a closing character steps over one that the
/// editor typed, and Backspace between an empty pair that it closed deletes both; with a kind set to
/// <see cref="AutoClosingMode.Never"/>, no pairs of that kind are deleted so.
/// </summary>
public sealed record AutoClosingOptions
{
    /// <summary>Brackets, such as <c>(</c>, <c>[</c> and <c>{</c>: <c>editor.autoClosingBrackets</c>.</summary>
    public AutoClosingMode Brackets { get; init; } = AutoClosingMode.LanguageDefined;

    /// <summary>Quotes, such as <c>"</c> and <c>'</c>: <c>editor.autoClosingQuotes</c>.</summary>
    public AutoClosingMode Quotes { get; init; } = AutoClosingMode.LanguageDefined;
}

/// <summary>Where a typed opening character brings its closing one, as VS Code's <c>editor.autoClosing*</c> values.</summary>
public enum AutoClosingMode
{
    /// <summary>Wherever the language's pairs allow, also before a word, and a quote right after one.</summary>
    Always,

    /// <summary>
    /// Before whitespace, the end of the line, another closing character, or a character of the language's
    /// <c>autoCloseBefore</c>, or by default one that can't start an expression; a quote not right after a word.
    /// </summary>
    LanguageDefined,

    /// <summary>Before whitespace, the end of the line or another closing character only.</summary>
    BeforeWhitespace,

    /// <summary>Never.</summary>
    Never,
}

/// <summary>What the search bar takes from the text as it opens, as Ctrl+F, Ctrl+H or F3 opens it.</summary>
public sealed record FindOptions
{
    /// <summary>
    /// What the query starts with: <c>editor.find.seedSearchStringFromSelection</c>. The query keeps its text unless
    /// this gives one, and F3 takes one only for an empty query.
    /// </summary>
    public SeedSearchString SeedSearchStringFromSelection { get; init; } = SeedSearchString.Always;

    /// <summary>When the bar searches only the selection as it opens: <c>editor.find.autoFindInSelection</c>.</summary>
    public AutoFindInSelection AutoFindInSelection { get; init; } = AutoFindInSelection.Never;
}

/// <summary>
/// What the search bar's query starts with as the bar opens, as VS Code's
/// <c>editor.find.seedSearchStringFromSelection</c> values; escaped while the bar uses a regular expression.
/// </summary>
public enum SeedSearchString
{
    /// <summary>Nothing: the query keeps its text.</summary>
    Never,

    /// <summary>The selection within one line, or without a selection the word at the caret.</summary>
    Always,

    /// <summary>The selection within one line only.</summary>
    Selection,
}

/// <summary>
/// When the search bar turns Find in Selection on as it opens, as VS Code's <c>editor.find.autoFindInSelection</c>
/// values. Alt+L and the bar's toggle turn it on and off either way.
/// </summary>
public enum AutoFindInSelection
{
    /// <summary>Never.</summary>
    Never,

    /// <summary>Whenever something is selected.</summary>
    Always,

    /// <summary>When the selection spans several lines.</summary>
    Multiline,
}

/// <summary>Which brackets are boxed at the caret, as VS Code's <c>editor.matchBrackets</c> values.</summary>
public enum MatchBrackets
{
    /// <summary>The bracket next to the caret and its match, or else the innermost pair around the caret.</summary>
    Always,

    /// <summary>Only the bracket next to the caret and its match.</summary>
    Near,

    /// <summary>None.</summary>
    Never,
}
