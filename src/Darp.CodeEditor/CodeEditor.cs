using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using Darp.CodeEditor.Editing;
using Darp.CodeEditor.Highlighting;
using Darp.CodeEditor.LanguageServices;
using Darp.CodeEditor.Theming;
using TextMateSharp.Grammars;

namespace Darp.CodeEditor;

/// <summary>
/// A text editor with TextMate highlighting, find and replace (AvaloniaEdit's search panel), and optional language
/// features.
/// </summary>
/// <remarks>
/// <para>
/// The editor is fully usable without a <see cref="LanguageService"/>. Setting one switches on completion,
/// diagnostics, semantic colors, quick info and signature help.
/// </para>
/// <para>
/// Colors come from a VS Code theme: <see cref="DarkTheme"/> while the editor's theme variant (from the app or a
/// <see cref="ThemeVariantScope"/>) is dark, <see cref="LightTheme"/> otherwise. It follows variant changes at run time.
/// </para>
/// </remarks>
public class CodeEditor : TextEditor
{
    private readonly LanguageFeatures _languageFeatures;
    private BackgroundTextMateHighlighter? _highlighter;
    private TextMate.Installation? _textMate;
    private string? _languageId;
    private CodeEditorTheme _darkTheme = CodeEditorTheme.DarkPlus;
    private CodeEditorTheme _lightTheme = CodeEditorTheme.LightPlus;
    private CodeEditorTheme? _requestedTheme;
    private Task<LoadedTheme>? _theme;
    private LanguageConfiguration _languageConfiguration = LanguageConfiguration.Default;
    private bool _languageConfigurationSet;

    public CodeEditor()
    {
        FontFamily = new FontFamily("Cascadia Code, JetBrains Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
        ShowLineNumbers = true;
        Options.ConvertTabsToSpaces = true;
        Options.HighlightCurrentLine = true;
        // Until the editor is in a window its variant is unknown: start loading the app's, which it usually inherits.
        ThemeVariant variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        ApplyColors(EditorTheme.Fallback(IsDark(variant)));
        _languageFeatures = new LanguageFeatures(this);
        // After the language features, so completion sees typed characters first and can commit with them.
        _ = new EditingHelpers(this);
        TextArea.TextView.BackgroundRenderers.Add(new BracketHighlighter(this));
        ActualThemeVariantChanged += (_, _) => SelectTheme(ActualThemeVariant);
        SelectTheme(variant);
    }

    /// <summary>
    /// True (the default) to tokenize on the thread pool. False uses AvaloniaEdit.TextMate, which tokenizes the
    /// visible lines on the UI thread, with Dark+ only; kept for comparison. Set it before the grammar.
    /// </summary>
    public bool UseBackgroundTokenization { get; init; } = true;

    /// <summary>The theme while the editor's theme variant is dark. VS Code's Dark+ by default.</summary>
    public CodeEditorTheme DarkTheme
    {
        get => _darkTheme;
        set
        {
            _darkTheme = value;
            SelectTheme(ActualThemeVariant);
        }
    }

    /// <summary>The theme while the editor's theme variant is light. VS Code's Light+ by default.</summary>
    public CodeEditorTheme LightTheme
    {
        get => _lightTheme;
        set
        {
            _lightTheme = value;
            SelectTheme(ActualThemeVariant);
        }
    }

    /// <summary>Colors of the editor's own UI: popups, squiggles and markers.</summary>
    internal EditorTheme EditorTheme { get; private set; } = EditorTheme.Default;

    /// <summary>Raised on the UI thread when <see cref="EditorTheme"/> changed.</summary>
    internal event EventHandler? EditorThemeChanged;

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
            if (value is null)
                return;
            SetGrammar(new GrammarSource(value, null));
            LoadLanguageConfiguration(value);
        }
    }

    /// <summary>
    /// Comment tokens and brackets for the editing helpers: auto-indent, bracket matching, auto-closing pairs and
    /// toggle comment. Taken from the bundled grammar of <see cref="LanguageId"/> unless set; set it for a grammar file.
    /// </summary>
    public LanguageConfiguration LanguageConfiguration
    {
        get => _languageConfiguration;
        set
        {
            _languageConfiguration = value;
            _languageConfigurationSet = true;
        }
    }

    /// <summary>Loads a TextMate grammar (.tmLanguage.json) from a file, for languages the bundled grammars lack.</summary>
    public void SetGrammarFile(string path)
    {
        _languageId = null;
        SetGrammar(new GrammarSource(null, path));
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

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // The search panel exists from here on.
        ApplySearchPanelColors(EditorTheme);
    }

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

    /// <summary>Parses a theme color: #RGB, #RGBA, #RRGGBB or #RRGGBBAA, where Avalonia expects alpha first.</summary>
    internal static bool TryParseThemeColor(string color, out Color parsed) =>
        Color.TryParse(
            color switch
            {
                ['#', _, _, _, _, _, _, _, _] => $"#{color[7..9]}{color[1..7]}",
                ['#', var r, var g, var b, var a] => $"#{a}{r}{g}{b}",
                _ => color,
            },
            out parsed
        );

    /// <inheritdoc cref="TryParseThemeColor"/>
    internal static Color ParseThemeColor(string color) =>
        TryParseThemeColor(color, out Color parsed) ? parsed : throw new FormatException($"Not a color: {color}");

    private static bool IsDark(ThemeVariant variant)
    {
        for (ThemeVariant? current = variant; current is not null; current = current.InheritVariant)
        {
            if (current == ThemeVariant.Dark)
                return true;
            if (current == ThemeVariant.Light)
                return false;
        }
        return false;
    }

    /// <summary>Whether a position is code, comment or string, as far as highlighting knows; code when unknown.</summary>
    internal TokenKind TokenKindAt(int offset) => _highlighter?.TokenKindAt(offset) ?? TokenKind.Code;

    /// <summary>Whether text inserted at a position would be code, comment or string; code when unknown.</summary>
    internal TokenKind TokenKindBefore(int offset) => _highlighter?.TokenKindBefore(offset) ?? TokenKind.Code;

    private async void LoadLanguageConfiguration(string languageId)
    {
        try
        {
            LanguageConfiguration? configuration = await TextMateCatalog.GetLanguageConfigurationAsync(languageId);
            if (configuration is not null && languageId == _languageId && !_languageConfigurationSet)
                _languageConfiguration = configuration;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Loading the language configuration of {languageId} failed: {exception}");
        }
    }

    /// <summary>Loads the theme for a variant on the thread pool; it applies with the grammar's tokens.</summary>
    private async void SelectTheme(ThemeVariant variant)
    {
        CodeEditorTheme requested = IsDark(variant) ? _darkTheme : _lightTheme;
        if (requested == _requestedTheme)
            return;
        _requestedTheme = requested;
        Task<LoadedTheme> theme = TextMateCatalog.GetThemeAsync(requested);
        _theme = theme;
        if (_highlighter is { } highlighter)
        {
            highlighter.SetTheme(theme);
            return;
        }
        try
        {
            // Completes synchronously for a theme loaded before.
            LoadedTheme loaded = await theme;
            if (_theme == theme && _highlighter is null)
                ApplyTheme(loaded);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Loading the theme {requested} failed: {exception}");
        }
    }

    private void SetGrammar(GrammarSource source)
    {
        if (!UseBackgroundTokenization)
        {
            SetStockGrammar(source);
            return;
        }
        if (_highlighter is null)
        {
            _highlighter = new BackgroundTextMateHighlighter(this);
            _highlighter.ThemeApplied += (_, theme) => ApplyTheme(theme);
            // Before the semantic colorizer, which colors on top.
            TextArea.TextView.LineTransformers.Insert(0, _highlighter);
            if (_theme is { } theme)
                _highlighter.SetTheme(theme);
        }
        _highlighter.SetGrammar(source);
    }

    private async void SetStockGrammar(GrammarSource source)
    {
        RegistryOptions options = await TextMateCatalog.Options;
        long start = Stopwatch.GetTimestamp();
        _textMate ??= this.InstallTextMate(options);
        if (source.File is { } file)
            _textMate.SetGrammarFile(file);
        else
            _textMate.SetGrammar(options.GetScopeByLanguageId(source.LanguageId));
        ReportTiming("textmate-set-grammar", Stopwatch.GetElapsedTime(start));
    }

    private void ApplyTheme(LoadedTheme theme)
    {
        EditorTheme colors = theme.Colors;
        if (colors == EditorTheme)
            return;
        long start = Stopwatch.GetTimestamp();
        ApplyColors(colors);
        // Rendering parts added by others, such as the semantic colorizer and the squiggles.
        TextView textView = TextArea.TextView;
        foreach (IThemedRenderer renderer in textView.LineTransformers.OfType<IThemedRenderer>())
            renderer.EditorTheme = colors;
        foreach (IThemedRenderer renderer in textView.BackgroundRenderers.OfType<IThemedRenderer>())
            renderer.EditorTheme = colors;
        EditorThemeChanged?.Invoke(this, EventArgs.Empty);
        textView.Redraw();
        ReportTiming("theme-applied", Stopwatch.GetElapsedTime(start));
    }

    private void ApplyColors(EditorTheme colors)
    {
        EditorTheme = colors;
        Background = colors.Background;
        Foreground = colors.Foreground;
        LineNumbersForeground = colors.LineNumbers;
        SearchResultsBrush = colors.FindMatch;
        TextArea.SelectionBrush = colors.Selection;
        TextArea.Caret.CaretBrush = colors.Caret;
        TextView textView = TextArea.TextView;
        textView.CurrentLineBackground = colors.CurrentLineBackground;
        textView.CurrentLineBorder = colors.CurrentLineBorder is { } border ? new Pen(border.Brush) : null;
        ApplySearchPanelColors(colors);
    }

    private void ApplySearchPanelColors(EditorTheme colors)
    {
        if (SearchPanel is not { } panel)
            return;
        panel.Background = colors.WidgetBackground;
        panel.BorderBrush = colors.WidgetBorder;
        panel.Foreground = colors.WidgetForeground;
    }
}

/// <param name="Name">What finished, for example "completion" or "classification".</param>
/// <param name="Count">How many items the operation produced, if that applies.</param>
public sealed record EditorTiming(string Name, TimeSpan Duration, int Count = 0);
