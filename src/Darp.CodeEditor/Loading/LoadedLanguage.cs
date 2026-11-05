using TextMateSharp.Internal.Types;

namespace Darp.CodeEditor.Loading;

/// <summary>A language's files, read: its TextMate grammar, if any, and its editing rules.</summary>
internal sealed record LoadedLanguage(IRawGrammar? Grammar, LanguageConfiguration Configuration)
{
    /// <summary>No colors and no editing rules: plain text, or a language whose files failed to load.</summary>
    public static LoadedLanguage None { get; } = new(null, LanguageConfiguration.None);
}
