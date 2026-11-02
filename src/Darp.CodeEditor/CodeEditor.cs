using System.Diagnostics;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Theming;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Darp.CodeEditor;

/// <summary>
/// A text editor with TextMate highlighting, find and replace (AvaloniaEdit's search panel), and optional language
/// features.
/// </summary>
/// <remarks>
/// The editor is fully usable without a <see cref="LanguageService"/>. Setting one switches on completion,
/// diagnostics, semantic colors, quick info and signature help.
/// </remarks>
public class CodeEditor : TextEditor
{
    private static RegistryOptions? s_registryOptions;
    private static IReadOnlyDictionary<string, string>? s_themeColors;

    private readonly LanguageFeatures _languageFeatures;
    private BackgroundTextMateHighlighter? _highlighter;
    private TextMate.Installation? _textMate;
    private string? _languageId;

    public CodeEditor()
    {
        FontFamily = new FontFamily("Cascadia Code, JetBrains Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
        ShowLineNumbers = true;
        Options.ConvertTabsToSpaces = true;
        Options.HighlightCurrentLine = true;

        // The theme is read once per process, on first use; grammars load on the thread pool.
        long start = Stopwatch.GetTimestamp();
        s_registryOptions ??= new RegistryOptions(ThemeName.DarkPlus);
        s_themeColors ??= new Registry(s_registryOptions).GetTheme().GetGuiColorDictionary();
        ApplyThemeColors(s_themeColors);
        StartupTimings.Add(new EditorTiming("textmate-theme", Stopwatch.GetElapsedTime(start)));

        _languageFeatures = new LanguageFeatures(this);
    }

    /// <summary>
    /// True (the default) to tokenize on the thread pool. False uses AvaloniaEdit.TextMate, which tokenizes the
    /// visible lines on the UI thread; kept for comparison. Set it before the grammar.
    /// </summary>
    public bool UseBackgroundTokenization { get; init; } = true;

    /// <summary>Colors of the editor's own UI: popups, squiggles and markers.</summary>
    internal EditorTheme EditorTheme { get; private set; } = EditorTheme.Default;

    /// <summary>Diagnostics: rendered lines that background tokenization has not reached yet.</summary>
    public int VisibleLinesWithoutHighlighting => _highlighter?.CountVisibleLinesWithoutTokens() ?? 0;

    /// <summary>Timings recorded before a <see cref="Timing"/> handler could be attached.</summary>
    public List<EditorTiming> StartupTimings { get; } = [];

    /// <summary>The TextMate language id used for highlighting, such as "csharp", "lua" or "json".</summary>
    public string? LanguageId
    {
        get => _languageId;
        set
        {
            if (value == _languageId)
                return;
            _languageId = value;
            if (value is not null && s_registryOptions is { } options)
                SetGrammar(options.GetScopeByLanguageId(value), null);
        }
    }

    /// <summary>Loads a TextMate grammar (.tmLanguage.json) from a file, for languages the bundled grammars lack.</summary>
    public void SetGrammarFile(string path)
    {
        _languageId = null;
        SetGrammar(null, path);
    }

    /// <summary>Identifies the document in requests to <see cref="LanguageService"/>.</summary>
    public string DocumentId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// The language features for this document, or null for highlighting and search only. Can be set at any time,
    /// for example once a language service finished loading in the background.
    /// </summary>
    public ILanguageService? LanguageService
    {
        get => _languageFeatures.Service;
        set => _languageFeatures.Service = value;
    }

    /// <summary>Raised on the UI thread when an editor operation finished, with how long it took.</summary>
    public event EventHandler<EditorTiming>? Timing;

    /// <summary>Raised on the UI thread when the language service failed a request.</summary>
    public event EventHandler<Exception>? LanguageServiceFailed;

    protected override Type StyleKeyOverride => typeof(TextEditor);

    internal void ReportTiming(string name, TimeSpan duration, int count = 0)
    {
        var timing = new EditorTiming(name, duration, count);
        if (Timing is { } handler)
            handler(this, timing);
        else
            StartupTimings.Add(timing);
    }

    /// <summary>For measurements: reports a timing taken on another thread, on the UI thread.</summary>
    internal void ReportTimingFromAnyThread(string name, TimeSpan duration) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ReportTiming(name, duration));

    internal void ReportFailure(Exception exception)
    {
        Trace.TraceWarning($"Language service request failed: {exception}");
        LanguageServiceFailed?.Invoke(this, exception);
    }

    /// <summary>Parses a TextMate theme color: #RRGGBB, or #RRGGBBAA where Avalonia expects #AARRGGBB.</summary>
    internal static Color ParseThemeColor(string color) =>
        Color.Parse(color is ['#', _, _, _, _, _, _, _, _] ? $"#{color[7..9]}{color[1..7]}" : color);

    private void SetGrammar(string? scopeName, string? grammarFile)
    {
        if (UseBackgroundTokenization)
        {
            _highlighter ??= CreateHighlighter();
            _highlighter.SetGrammar(scopeName, grammarFile);
            return;
        }
        long start = Stopwatch.GetTimestamp();
        _textMate ??= this.InstallTextMate(s_registryOptions);
        if (grammarFile is not null)
            _textMate.SetGrammarFile(grammarFile);
        else
            _textMate.SetGrammar(scopeName);
        ReportTiming("textmate-set-grammar", Stopwatch.GetElapsedTime(start));
    }

    private BackgroundTextMateHighlighter CreateHighlighter()
    {
        var highlighter = new BackgroundTextMateHighlighter(
            this,
            s_registryOptions ?? new RegistryOptions(ThemeName.DarkPlus)
        );
        // Before the semantic colorizer, which colors on top.
        TextArea.TextView.LineTransformers.Insert(0, highlighter);
        return highlighter;
    }

    private void ApplyThemeColors(IReadOnlyDictionary<string, string> colors)
    {
        if (GetThemeBrush("editor.background") is { } background)
            Background = background;
        if (GetThemeBrush("editor.foreground") is { } foreground)
            Foreground = foreground;
        if (GetThemeBrush("editorLineNumber.foreground") is { } lineNumbers)
            LineNumbersForeground = lineNumbers;
        if (GetThemeBrush("editor.selectionBackground") is { } selection)
            TextArea.SelectionBrush = selection;
        if (GetThemeBrush("editor.lineHighlightBackground") is { } currentLine)
        {
            TextArea.TextView.CurrentLineBackground = currentLine;
            TextArea.TextView.CurrentLineBorder = new Pen(currentLine);
        }

        IBrush? GetThemeBrush(string key) =>
            colors.TryGetValue(key, out string? color) ? new SolidColorBrush(ParseThemeColor(color)) : null;
    }
}

/// <param name="Name">What finished, for example "completion" or "classification".</param>
/// <param name="Count">How many items the operation produced, if that applies.</param>
public sealed record EditorTiming(string Name, TimeSpan Duration, int Count = 0);
