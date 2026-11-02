using System.Collections.Concurrent;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Darp.CodeEditor.Highlighting;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Darp.CodeEditor.Theming;

/// <summary>
/// Loads TextMate registries, themes and grammars on the thread pool, once per process. Reading the bundled grammar
/// list takes 10-30 ms and a theme several more, so none of it may run on the UI thread.
/// </summary>
/// <remarks>
/// A grammar is loaded per theme, because its tokens carry the theme's colors. Editors share grammars: a
/// <see cref="TextMateGrammar"/> lets one thread tokenize at a time, the UI thread first, and expressions compiled for
/// one editor are ready for the next.
/// </remarks>
internal static class TextMateCatalog
{
    private static readonly Lazy<Task<RegistryOptions>> s_options = new(() =>
        Task.Run(() => new RegistryOptions(ThemeName.DarkPlus))
    );
    private static readonly ConcurrentDictionary<CodeEditorTheme, Lazy<Task<LoadedTheme>>> s_themes = new();
    private static readonly ConcurrentDictionary<
        (GrammarSource, CodeEditorTheme),
        Lazy<Task<TextMateGrammar?>>
    > s_grammars = new();

    /// <summary>The bundled grammars and themes, with each language's comment tokens and brackets.</summary>
    public static Task<RegistryOptions> Options => s_options.Value;

    public static Task<LoadedTheme> GetThemeAsync(CodeEditorTheme theme) =>
        s_themes.GetOrAdd(theme, t => new Lazy<Task<LoadedTheme>>(() => Task.Run(() => LoadThemeAsync(t)))).Value;

    /// <returns>Null when no bundled grammar has the language id.</returns>
    public static Task<TextMateGrammar?> GetGrammarAsync(GrammarSource source, LoadedTheme theme) =>
        s_grammars
            .GetOrAdd(
                (source, theme.Source),
                _ => new Lazy<Task<TextMateGrammar?>>(() => Task.Run(() => LoadGrammarAsync(source, theme)))
            )
            .Value;

    /// <summary>The comment tokens and brackets of a bundled language, or null when it has none.</summary>
    public static async Task<LanguageConfiguration?> GetLanguageConfigurationAsync(string languageId)
    {
        RegistryOptions options = await Options.ConfigureAwait(false);
        Language? language = options.GetAvailableLanguages().Find(l => l.Id == languageId);
        return LanguageConfiguration.FromTextMate(language?.Configuration);
    }

    /// <summary>A grammar loaded before, with its expressions compiled by earlier use.</summary>
    public static bool TryGetLoadedGrammar(GrammarSource source, LoadedTheme theme, out TextMateGrammar? grammar)
    {
        grammar = null;
        if (
            !s_grammars.TryGetValue((source, theme.Source), out Lazy<Task<TextMateGrammar?>>? lazy)
            || !lazy.IsValueCreated
            || !lazy.Value.IsCompletedSuccessfully
        )
            return false;
        grammar = lazy.Value.Result;
        return true;
    }

    private static async Task<LoadedTheme> LoadThemeAsync(CodeEditorTheme source)
    {
        RegistryOptions options = await Options.ConfigureAwait(false);
        IRawTheme raw;
        string? directory = null;
        if (source.File is { } file)
        {
            using var reader = new StreamReader(file);
            raw = ThemeReader.ReadThemeSync(reader);
            directory = Path.GetDirectoryName(file);
        }
        else
        {
            raw = options.LoadTheme(source.Bundled ?? ThemeName.DarkPlus);
        }
        var themedOptions = new ThemedRegistryOptions(options, raw, directory);
        return new LoadedTheme(source, themedOptions, EditorTheme.Create(Theme.CreateFromRawTheme(raw, themedOptions)));
    }

    private static async Task<TextMateGrammar?> LoadGrammarAsync(GrammarSource source, LoadedTheme theme)
    {
        RegistryOptions options = await Options.ConfigureAwait(false);
        // The registry's default theme is the one the grammar's tokens get colors from.
        var registry = new Registry(theme.RegistryOptions);
        IGrammar? grammar;
        if (source.File is { } file)
            grammar = registry.LoadGrammarFromPathSync(file, 0, null);
        else if (options.GetScopeByLanguageId(source.LanguageId) is { } scope)
            grammar = registry.LoadGrammar(scope);
        else
            return null;
        if (grammar is null)
            return null;
        Theme registryTheme = registry.GetTheme();
        ICollection<string> colors = registryTheme.GetColorMap();
        var brushes = new IBrush?[colors.Count + 1];
        foreach (string color in colors)
        {
            int id = registryTheme.GetColorId(color);
            if (id >= 0 && id < brushes.Length && CodeEditor.TryParseThemeColor(color, out Color parsed))
                brushes[id] = new ImmutableSolidColorBrush(parsed);
        }
        return new TextMateGrammar(grammar, brushes);
    }

    /// <summary>Bundled grammars with one theme as the default; files a theme file includes are read next to it.</summary>
    private sealed class ThemedRegistryOptions(RegistryOptions bundled, IRawTheme theme, string? directory)
        : IRegistryOptions
    {
        private readonly RegistryOptions _bundled = bundled;
        private readonly IRawTheme _theme = theme;
        private readonly string? _directory = directory;

        public IRawTheme GetDefaultTheme() => _theme;

        public IRawTheme GetTheme(string scopeName)
        {
            if (_directory is not null && Path.Combine(_directory, scopeName) is var path && File.Exists(path))
            {
                using var reader = new StreamReader(path);
                return ThemeReader.ReadThemeSync(reader);
            }
            return _bundled.GetTheme(scopeName);
        }

        public IRawGrammar GetGrammar(string scopeName) => _bundled.GetGrammar(scopeName);

        public ICollection<string> GetInjections(string scopeName) => _bundled.GetInjections(scopeName);
    }
}

/// <summary>A theme, ready to color the editor and to load grammars with.</summary>
/// <param name="RegistryOptions">Bundled grammars with this theme as the default theme.</param>
internal sealed record LoadedTheme(CodeEditorTheme Source, IRegistryOptions RegistryOptions, EditorTheme Colors);

/// <summary>A bundled grammar by language id, such as "csharp", or a .tmLanguage.json file.</summary>
internal readonly record struct GrammarSource(string? LanguageId, string? File);
