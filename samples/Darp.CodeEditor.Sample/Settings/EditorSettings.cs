using CommunityToolkit.Mvvm.ComponentModel;
using Darp.CodeEditor;

namespace Sample.Settings;

/// <summary>
/// The editor's options on the settings page: one <see cref="CodeEditorOptions"/> that every editor of the app takes.
/// The record is immutable, so each of the page's values replaces it with a copy that differs in that value. They last
/// as long as the app runs.
/// </summary>
public sealed partial class EditorSettings : ObservableObject
{
    public static IReadOnlyList<AutoClosingMode> AutoClosingModes { get; } = Enum.GetValues<AutoClosingMode>();

    public static IReadOnlyList<SeedSearchString> SeedSearchStrings { get; } = Enum.GetValues<SeedSearchString>();

    public static IReadOnlyList<AutoFindInSelection> AutoFindInSelections { get; } =
        Enum.GetValues<AutoFindInSelection>();

    public static IReadOnlyList<MatchBrackets> MatchBracketsModes { get; } = Enum.GetValues<MatchBrackets>();

    /// <summary>The options, which the editors bind to.</summary>
    [ObservableProperty]
    public partial CodeEditorOptions Options { get; set; } = CodeEditorOptions.Default;

    public AutoClosingMode AutoClosingBrackets
    {
        get => Options.AutoClosing.Brackets;
        set => Options = Options with { AutoClosing = Options.AutoClosing with { Brackets = value } };
    }

    public AutoClosingMode AutoClosingQuotes
    {
        get => Options.AutoClosing.Quotes;
        set => Options = Options with { AutoClosing = Options.AutoClosing with { Quotes = value } };
    }

    public bool EmptySelectionClipboard
    {
        get => Options.EmptySelectionClipboard;
        set => Options = Options with { EmptySelectionClipboard = value };
    }

    public SeedSearchString SeedSearchStringFromSelection
    {
        get => Options.Find.SeedSearchStringFromSelection;
        set => Options = Options with { Find = Options.Find with { SeedSearchStringFromSelection = value } };
    }

    public AutoFindInSelection AutoFindInSelection
    {
        get => Options.Find.AutoFindInSelection;
        set => Options = Options with { Find = Options.Find with { AutoFindInSelection = value } };
    }

    /// <summary>
    /// The hover's delay in milliseconds, for a number box. A blank box gives NaN, which the delay ignores; the page then
    /// shows the delay again.
    /// </summary>
    public double HoverDelay
    {
        get => Options.Hover.Delay.TotalMilliseconds;
        set
        {
            if (!double.IsNaN(value))
                Options = Options with { Hover = Options.Hover with { Delay = TimeSpan.FromMilliseconds(value) } };
        }
    }

    public MatchBrackets MatchBrackets
    {
        get => Options.MatchBrackets;
        set => Options = Options with { MatchBrackets = value };
    }

    public bool OccurrencesHighlight
    {
        get => Options.OccurrencesHighlight;
        set => Options = Options with { OccurrencesHighlight = value };
    }

    public bool ParameterHints
    {
        get => Options.ParameterHints.Enabled;
        set => Options = Options with { ParameterHints = Options.ParameterHints with { Enabled = value } };
    }
}
