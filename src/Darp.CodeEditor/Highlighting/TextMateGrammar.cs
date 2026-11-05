using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Loading;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.Highlighting;

/// <summary>
/// A language's TextMate grammar, loaded with one color theme, and the brushes of that theme by color id: a grammar's
/// tokens carry the colors of the theme it was loaded with.
/// </summary>
/// <remarks>
/// One editor's UI thread and its background work tokenize with it. A grammar compiles and caches its expressions as
/// it goes, and TextMateSharp resets the state a line starts from, so only one thread tokenizes at a time: the UI
/// thread first.
/// </remarks>
internal sealed class TextMateGrammar
{
    /// <summary>How long background tokenizing yields to the UI thread at most, before it takes the grammar anyway.</summary>
    private static readonly TimeSpan s_maxYield = TimeSpan.FromMilliseconds(20);

    /// <summary>Held while tokenizing.</summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// True while the UI thread waits for or holds the grammar. Locks are not fair: a background loop that takes the
    /// grammar for one line after another would otherwise keep the UI thread waiting past its frame budget.
    /// </summary>
    private volatile bool _uiThreadWaiting;

    private TextMateGrammar(ColorThemeFile theme, IRawGrammar source, IGrammar grammar, IBrush?[] brushes)
    {
        Theme = theme;
        Source = source;
        Grammar = grammar;
        Brushes = brushes;
    }

    /// <summary>The theme whose colors the tokens carry.</summary>
    public ColorThemeFile Theme { get; }

    /// <summary>The language's grammar, as read from its file.</summary>
    public IRawGrammar Source { get; }

    public IGrammar Grammar { get; }

    /// <summary>The theme's token colors, by the color id that tokens carry.</summary>
    public IBrush?[] Brushes { get; }

    /// <summary>
    /// Set on the UI thread when one line took much longer than the frame budget there: TextMate cannot stop inside
    /// one regex search, so a grammar with such an expression is tokenized only in the background from then on.
    /// </summary>
    public bool TooSlowForUiThread { get; set; }

    /// <summary>Loads <paramref name="grammar"/> with <paramref name="theme"/>, in a registry of its own.</summary>
    public static TextMateGrammar Load(ColorThemeFile theme, IRawGrammar grammar)
    {
        var registry = new Registry(new RegistryOptions(theme, grammar));
        IGrammar loaded = registry.LoadGrammar(grammar.GetScopeName());
        Theme colors = registry.GetTheme();
        ICollection<string> colorMap = colors.GetColorMap();
        var brushes = new IBrush?[colorMap.Count + 1];
        foreach (string color in colorMap)
        {
            int id = colors.GetColorId(color);
            if (id < brushes.Length && ColorThemeFile.TryParseColor(color, out Color parsed))
                brushes[id] = new ImmutableSolidColorBrush(parsed);
        }
        return new TextMateGrammar(theme, grammar, loaded, brushes);
    }

    /// <summary>Takes the grammar on the UI thread, ahead of background tokenizing; false after the timeout.</summary>
    public bool TryEnterOnUiThread(TimeSpan timeout)
    {
        _uiThreadWaiting = true;
        if (_gate.TryEnter(timeout))
            return true;
        _uiThreadWaiting = false;
        return false;
    }

    public void ExitOnUiThread()
    {
        _gate.Exit();
        _uiThreadWaiting = false;
    }

    /// <summary>
    /// Tokenizes one line in the background, after the UI thread when it is waiting. Null when it took the whole time
    /// limit: TextMate then stops without saying so, and the result may be incomplete.
    /// </summary>
    public ITokenizeLineResult2? TokenizeInBackground(string line, IStateStack? state, TimeSpan timeLimit)
    {
        if (_uiThreadWaiting)
            SpinWait.SpinUntil(() => !_uiThreadWaiting, s_maxYield);
        lock (_gate)
        {
            long start = Stopwatch.GetTimestamp();
            ITokenizeLineResult2 result = Grammar.TokenizeLine2(new LineText(line.AsMemory()), state, timeLimit);
            return Stopwatch.GetElapsedTime(start) < timeLimit ? result : null;
        }
    }

    /// <summary>
    /// The language's own grammar, the built-in grammars it may include, and the theme, already read with the themes
    /// it includes.
    /// </summary>
    private sealed class RegistryOptions(ColorThemeFile theme, IRawGrammar grammar) : IRegistryOptions
    {
        private readonly ColorThemeFile _theme = theme;
        private readonly IRawGrammar _grammar = grammar;

        public IRawGrammar? GetGrammar(string scopeName) =>
            scopeName == _grammar.GetScopeName() ? _grammar : TextMateGrammars.GetBuiltIn(scopeName);

        public IRawTheme GetDefaultTheme() => _theme.TokenRules;

        public IRawTheme? GetTheme(string scopeName) => null;

        public ICollection<string>? GetInjections(string scopeName) => null;
    }
}
